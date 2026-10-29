using SchoolMessenger.Shared;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.AnnouncementServer;

public record Account(string Id, string? Username, string Name, string Role, string? ClassId, string? InternalUserId,
    bool CanBroadcast, bool Active, int Version, string? PasswordHash, int FailedAttempts, long LockedUntil, int ScopeVersion=1);

public sealed class PortalStore : IDisposable
{
    readonly FileStream lease;
    public void Dispose() => lease.Dispose();
    public string Root { get; }
    public string Files => Path.Combine(Root, "files");
    public string BackupDirectory { get; }
    public bool AttachmentBackupEnabled { get; }
    public PortalStore(IConfiguration config)
    {
        Root = Path.GetFullPath(config["Portal:DataDirectory"] ?? "portal-data");
        BackupDirectory = Path.GetFullPath(config["Portal:BackupDirectory"] ?? Path.Combine(Root, "backups"));
        AttachmentBackupEnabled = config.GetValue("Portal:AttachmentBackup", false);
        if(File.Exists(Path.Combine(Root,"school.db")))throw new InvalidOperationException("외부 공지 서버에 교직원 데이터 폴더를 사용할 수 없습니다.");
        Directory.CreateDirectory(Files);
        lease = BackendDatabase.Acquire(Root);
        using (var migrated = Open()) BackendDatabase.Migrate(migrated, Root);
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Classes(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Grade INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS People(Id TEXT PRIMARY KEY,Username TEXT COLLATE NOCASE UNIQUE,Name TEXT NOT NULL,Role TEXT NOT NULL,
              ClassId TEXT REFERENCES Classes(Id),InternalUserId TEXT UNIQUE,CanBroadcast INTEGER NOT NULL DEFAULT 0,Active INTEGER NOT NULL DEFAULT 1,
              Version INTEGER NOT NULL DEFAULT 1,PasswordHash TEXT,FailedAttempts INTEGER NOT NULL DEFAULT 0,LockedUntil INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS TeacherClasses(TeacherId TEXT NOT NULL REFERENCES People(Id),ClassId TEXT NOT NULL REFERENCES Classes(Id),PRIMARY KEY(TeacherId,ClassId));
            CREATE TABLE IF NOT EXISTS Families(ParentId TEXT NOT NULL REFERENCES People(Id),StudentId TEXT NOT NULL REFERENCES People(Id),PRIMARY KEY(ParentId,StudentId));
            CREATE TABLE IF NOT EXISTS Invites(Hash TEXT PRIMARY KEY,PersonId TEXT NOT NULL REFERENCES People(Id),ExpiresAt INTEGER NOT NULL,UsedAt INTEGER);
            CREATE TABLE IF NOT EXISTS StudentApplications(PersonId TEXT PRIMARY KEY REFERENCES People(Id),StudentNumber INTEGER NOT NULL,RequestedAt INTEGER NOT NULL,ApprovedAt INTEGER);
            CREATE TABLE IF NOT EXISTS StudentCodes(Hash TEXT PRIMARY KEY,StudentId TEXT NOT NULL REFERENCES People(Id),ProtectedCode TEXT NOT NULL,Active INTEGER NOT NULL DEFAULT 1,CreatedAt INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_StudentCodes_Active ON StudentCodes(StudentId) WHERE Active=1;
            CREATE TABLE IF NOT EXISTS FamilyCodeAttempts(ParentId TEXT PRIMARY KEY REFERENCES People(Id),Failures INTEGER NOT NULL DEFAULT 0,LockedUntil INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS TeacherProofs(PersonId TEXT PRIMARY KEY REFERENCES People(Id),Active INTEGER NOT NULL,CanBroadcast INTEGER NOT NULL,VerifiedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Notices(Id TEXT PRIMARY KEY,SenderId TEXT NOT NULL REFERENCES People(Id),ClientId TEXT NOT NULL,Fingerprint TEXT NOT NULL,
              Title TEXT NOT NULL,Body TEXT NOT NULL,Audience TEXT NOT NULL,PublishedAt INTEGER NOT NULL,Withdrawn INTEGER NOT NULL DEFAULT 0,UNIQUE(SenderId,ClientId));
            CREATE TABLE IF NOT EXISTS Targets(NoticeId TEXT NOT NULL REFERENCES Notices(Id),UserId TEXT NOT NULL REFERENCES People(Id),StudentId TEXT NOT NULL REFERENCES People(Id),
              ClassId TEXT NOT NULL REFERENCES Classes(Id),ReadAt INTEGER,Revoked INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(NoticeId,UserId,StudentId));
            CREATE INDEX IF NOT EXISTS IX_Targets_User ON Targets(UserId,NoticeId);
            CREATE TABLE IF NOT EXISTS Files(Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES People(Id),ClientId TEXT NOT NULL,Hash TEXT NOT NULL,Name TEXT NOT NULL,
              Size INTEGER NOT NULL,UploadedAt INTEGER NOT NULL,NoticeId TEXT REFERENCES Notices(Id),ExpiresAt INTEGER,DeletedAt INTEGER,UNIQUE(OwnerId,ClientId));
            """);
        if(!Query("PRAGMA table_info(People)",r=>r.GetString(1)).Contains("ScopeVersion"))Execute("ALTER TABLE People ADD COLUMN ScopeVersion INTEGER NOT NULL DEFAULT 1");
        if(!Query("PRAGMA table_info(Families)",r=>r.GetString(1)).Contains("Stamp"))Execute("ALTER TABLE Families ADD COLUMN Stamp TEXT NOT NULL DEFAULT ''");
        if(!Query("PRAGMA table_info(Notices)",r=>r.GetString(1)).Contains("Kind"))Execute("ALTER TABLE Notices ADD COLUMN Kind TEXT NOT NULL DEFAULT 'notice' CHECK(Kind IN ('notice','home-letter'))");
        Execute("UPDATE Families SET Stamp=lower(hex(randomblob(16))) WHERE Stamp=''");
    }
    public SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "announcements.db"), ForeignKeys = true, DefaultTimeout = 30 }.ToString());
        db.Open(); return db;
    }
    public static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Key, object? Value)[] args)
    { var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value); return command; }
    public int Execute(string sql, params (string Key, object? Value)[] args)
    { using var db = Open(); using var command = Command(db, sql, null, args); return command.ExecuteNonQuery(); }
    public List<T> Query<T>(string sql, Func<SqliteDataReader,T> map, params (string Key, object? Value)[] args)
    { using var db = Open(); using var command = Command(db, sql, null, args); using var reader = command.ExecuteReader(); var rows = new List<T>(); while (reader.Read()) rows.Add(map(reader)); return rows; }
    public static string? Text(SqliteDataReader r, int n) => r.IsDBNull(n) ? null : r.GetString(n);
    public static Account ReadAccount(SqliteDataReader r) => new(r.GetString(0),Text(r,1),r.GetString(2),r.GetString(3),Text(r,4),Text(r,5),r.GetBoolean(6),r.GetBoolean(7),r.GetInt32(8),Text(r,9),r.GetInt32(10),r.GetInt64(11),r.GetInt32(12));
    public Account? Person(string id) => Query("SELECT * FROM People WHERE Id=$id",ReadAccount,("$id",id)).FirstOrDefault();
    public Account? Username(string name) => Query("SELECT * FROM People WHERE Username=$name",ReadAccount,("$name",name)).FirstOrDefault();
    public string[] Classes(string teacherId) => Query("SELECT ClassId FROM TeacherClasses WHERE TeacherId=$id ORDER BY ClassId",r=>r.GetString(0),("$id",teacherId)).ToArray();
    public bool Regular(Account a) => a.Role!="parent" || Query("SELECT 1 FROM Families f JOIN People s ON s.Id=f.StudentId WHERE f.ParentId=$id AND s.Role='student' AND s.Active=1 AND s.ClassId IS NOT NULL LIMIT 1",r=>r.GetInt32(0),("$id",a.Id)).Count>0;
    public PortalUser Public(Account a) => new(a.Id,a.Name,a.Role,a.CanBroadcast&&PortalSecurity.VerifiedTeacher(this,a,true),Classes(a.Id),Regular(a)?"regular":"temporary");
    public PortalDirectory DirectorySnapshot()
    {
        var rooms=Query("SELECT * FROM Classes ORDER BY Grade,Name",r=>new PortalClass(r.GetString(0),r.GetString(1),r.GetInt32(2))).ToArray();
        var scopes=Query("SELECT TeacherId,ClassId FROM TeacherClasses",r=>(teacher:r.GetString(0),room:r.GetString(1))).ToLookup(x=>x.teacher,x=>x.room);
        var families=Query("SELECT f.ParentId,f.StudentId,f.Stamp FROM Families f JOIN People s ON s.Id=f.StudentId WHERE s.Active=1",r=>(parent:r.GetString(0),student:r.GetString(1),stamp:r.GetString(2))).ToLookup(x=>x.parent);
        var people=Query("SELECT * FROM People WHERE Active=1 AND Role!='admin'",ReadAccount).Select(a=>new DirectoryPerson(a.Id,a.Role,a.ClassId,a.InternalUserId,a.CanBroadcast,scopes[a.Id].ToArray(),families[a.Id].Select(f=>f.student).ToArray(),a.ScopeVersion,families[a.Id].ToDictionary(f=>f.student,f=>f.stamp),a.Name)).ToArray();
        var homes=Query("SELECT name FROM sqlite_master WHERE type='table' AND name='TimetableHomerooms'",r=>r.GetString(0)).Count==0?[]:Query("SELECT ClassId,TeacherId FROM TimetableHomerooms",r=>(room:r.GetString(0),teacher:r.GetString(1))).Select(h=>new PortalHomeroom(h.room,people.FirstOrDefault(p=>p.Role=="teacher"&&p.InternalUserId==h.teacher)?.Id??"")).Where(h=>h.TeacherId.Length>0).ToArray();
        return new(rooms,people,PortalSections.List(this),homes);
    }
    public void RevokeStudent(string student)
    { Execute("UPDATE Targets SET Revoked=1 WHERE StudentId=$id",("$id",student)); Execute("UPDATE People SET Version=Version+1 WHERE Id=$id OR Id IN(SELECT ParentId FROM Families WHERE StudentId=$id)",("$id",student)); }
}

using SchoolMessenger.Shared;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

public sealed record User(string Id, string Username, string Name, string Department, string PasswordHash,
    bool IsAdmin, bool CanBroadcast, bool Active, int SessionVersion, int FailedAttempts, long LockedUntil,string Position="");
public sealed record Registration(string Id, string Username, string Name, string Department, string PasswordHash, string Status, long CreatedAt);

public sealed class Store : IDisposable
{
    readonly FileStream lease;
    public void Dispose() => lease.Dispose();
    public string Root { get; }
    public string Files => Path.Combine(Root, "files");
    public string Database => Path.Combine(Root, "school.db");
    public string BackupDirectory { get; }
    public bool AttachmentBackupEnabled { get; }
    public Store(IConfiguration config)
    {
        Root = Path.GetFullPath(config["School:DataDirectory"] ?? "data");
        if(File.Exists(Path.Combine(Root,"announcements.db")))throw new InvalidOperationException("교직원 서버와 외부 공지 서버의 데이터 폴더를 분리하세요.");
        BackupDirectory = Path.GetFullPath(config["School:BackupDirectory"] ?? Path.Combine(Root, "backups"));
        AttachmentBackupEnabled = config.GetValue("School:AttachmentBackup", false);
        Directory.CreateDirectory(Files);
        lease = BackendDatabase.Acquire(Root);
        using (var migrated = Open()) BackendDatabase.Migrate(migrated, Root);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Todos(
              Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES Users(Id),SourceId TEXT REFERENCES Messages(Id),
              Evidence TEXT NOT NULL,Title TEXT NOT NULL,DueDate TEXT,Status TEXT NOT NULL,Revision INTEGER NOT NULL DEFAULT 1,
              CreatedAt INTEGER NOT NULL,UpdatedAt INTEGER NOT NULL,UNIQUE(OwnerId,SourceId,Evidence));
            CREATE INDEX IF NOT EXISTS IX_Todos_Owner ON Todos(OwnerId,UpdatedAt);
            CREATE TABLE IF NOT EXISTS SubmissionRequests(
              MessageId TEXT PRIMARY KEY REFERENCES Messages(Id),Deadline INTEGER NOT NULL,Closed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS SubmissionTargets(
              RequestId TEXT NOT NULL REFERENCES SubmissionRequests(MessageId),UserId TEXT NOT NULL REFERENCES Users(Id),
              Exempt INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(RequestId,UserId));
            CREATE TABLE IF NOT EXISTS Submissions(
              Id TEXT PRIMARY KEY,RequestId TEXT NOT NULL REFERENCES SubmissionRequests(MessageId),UserId TEXT NOT NULL REFERENCES Users(Id),
              ClientId TEXT NOT NULL,Fingerprint TEXT NOT NULL,CreatedAt INTEGER NOT NULL,UNIQUE(RequestId,UserId,ClientId));
            CREATE TABLE IF NOT EXISTS SubmissionFiles(
              SubmissionId TEXT NOT NULL REFERENCES Submissions(Id),AttachmentId TEXT NOT NULL UNIQUE REFERENCES Attachments(Id),
              PRIMARY KEY(SubmissionId,AttachmentId));
            CREATE TABLE IF NOT EXISTS SubmissionWithdrawals(
              SubmissionId TEXT PRIMARY KEY REFERENCES Submissions(Id),WithdrawnAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS SubmissionReminders(
              RequestId TEXT NOT NULL,UserId TEXT NOT NULL,Day TEXT NOT NULL,MessageId TEXT NOT NULL REFERENCES Messages(Id),
              PRIMARY KEY(RequestId,UserId,Day));
            CREATE TABLE IF NOT EXISTS Users(
              Id TEXT PRIMARY KEY, Username TEXT NOT NULL COLLATE NOCASE UNIQUE, Name TEXT NOT NULL,
              Department TEXT NOT NULL, PasswordHash TEXT NOT NULL, IsAdmin INTEGER NOT NULL,
              CanBroadcast INTEGER NOT NULL, Active INTEGER NOT NULL DEFAULT 1,
              SessionVersion INTEGER NOT NULL DEFAULT 1, FailedAttempts INTEGER NOT NULL DEFAULT 0,
              LockedUntil INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Messages(
              Id TEXT PRIMARY KEY, SenderId TEXT NOT NULL REFERENCES Users(Id), ClientId TEXT NOT NULL,
              Fingerprint TEXT NOT NULL, Title TEXT NOT NULL, Body TEXT NOT NULL, CreatedAt INTEGER NOT NULL,
              UNIQUE(SenderId,ClientId));
            CREATE TABLE IF NOT EXISTS Recipients(
              MessageId TEXT NOT NULL REFERENCES Messages(Id), UserId TEXT NOT NULL REFERENCES Users(Id),
              ReadAt INTEGER, PRIMARY KEY(MessageId,UserId));
            CREATE TABLE IF NOT EXISTS MessageFormats(
              MessageId TEXT PRIMARY KEY REFERENCES Messages(Id) ON DELETE CASCADE,Payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Recipients_User ON Recipients(UserId,MessageId);
            CREATE INDEX IF NOT EXISTS IX_Messages_Date ON Messages(CreatedAt);
            CREATE TABLE IF NOT EXISTS MessageRecalls(
              MessageId TEXT PRIMARY KEY REFERENCES Messages(Id),RecalledAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Attachments(
              Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL REFERENCES Users(Id), Name TEXT NOT NULL,
              Size INTEGER NOT NULL, UploadedAt INTEGER NOT NULL,
              MessageId TEXT REFERENCES Messages(Id), ExpiresAt INTEGER, DeletedAt INTEGER);
            CREATE TABLE IF NOT EXISTS Registrations(
              Id TEXT PRIMARY KEY, Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
              Name TEXT NOT NULL, Department TEXT NOT NULL, PasswordHash TEXT NOT NULL,
              Status TEXT NOT NULL DEFAULT 'pending', CreatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ChatRooms(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, DirectKey TEXT UNIQUE, CreatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ChatMembers(RoomId TEXT NOT NULL REFERENCES ChatRooms(Id), UserId TEXT NOT NULL REFERENCES Users(Id), LastRead INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(RoomId,UserId));
            CREATE TABLE IF NOT EXISTS ChatEntries(Sequence INTEGER PRIMARY KEY AUTOINCREMENT, RoomId TEXT NOT NULL REFERENCES ChatRooms(Id), MessageId TEXT NOT NULL UNIQUE REFERENCES Messages(Id));
            CREATE INDEX IF NOT EXISTS IX_ChatEntries_Room ON ChatEntries(RoomId,Sequence);
            CREATE TABLE IF NOT EXISTS Surveys(Id TEXT PRIMARY KEY, OwnerId TEXT NOT NULL REFERENCES Users(Id), Title TEXT NOT NULL, Description TEXT NOT NULL, Questions TEXT NOT NULL, Deadline INTEGER NOT NULL, Closed INTEGER NOT NULL DEFAULT 0, CreatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS SurveyTargets(SurveyId TEXT NOT NULL REFERENCES Surveys(Id), UserId TEXT NOT NULL REFERENCES Users(Id), PRIMARY KEY(SurveyId,UserId));
            CREATE TABLE IF NOT EXISTS SurveyResponses(SurveyId TEXT NOT NULL REFERENCES Surveys(Id), UserId TEXT NOT NULL REFERENCES Users(Id), Answers TEXT NOT NULL, UpdatedAt INTEGER NOT NULL, PRIMARY KEY(SurveyId,UserId));
            """;
        command.ExecuteNonQuery();
        if(!Query("PRAGMA table_info(Users)",r=>r.GetString(1)).Contains("Position"))Execute("ALTER TABLE Users ADD COLUMN Position TEXT NOT NULL DEFAULT ''");
    }
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Database, ForeignKeys = true, DefaultTimeout = 30 }.ToString());
        connection.Open();
        return connection;
    }
    public static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, null, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, null, parameters);
        return command.ExecuteNonQuery();
    }
    public User? GetUser(string id) => Query("SELECT * FROM Users WHERE Id=$id", ReadUser, ("$id", id)).FirstOrDefault();
    public User? FindUser(string username) => Query("SELECT * FROM Users WHERE Username=$name", ReadUser, ("$name", username)).FirstOrDefault();
    public List<User> Users() => Query("SELECT * FROM Users ORDER BY Department,Name", ReadUser);
    public Registration? FindRegistration(string username) => Query("SELECT * FROM Registrations WHERE Username=$username", ReadRegistration, ("$username", username)).FirstOrDefault();
    public List<Registration> PendingRegistrations() => Query("SELECT * FROM Registrations WHERE Status='pending' ORDER BY CreatedAt", ReadRegistration);
    public static Registration ReadRegistration(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6));
    static User ReadUser(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8), r.GetInt32(9), r.GetInt64(10),r.GetString(r.GetOrdinal("Position")));
    public void Backup()
    {
        lock (AttachmentBackup.Gate)
        {
        var directory = BackupDirectory;
        Directory.CreateDirectory(directory);
        using var source = Open();
        var path = Path.Combine(directory, $"school-{DateTime.UtcNow:yyyyMMdd}.db");
        BackendDatabase.Backup(source,path);
        if (AttachmentBackupEnabled) AttachmentBackup.Copy(source, Files, directory, false);
        foreach (var file in Directory.EnumerateFiles(directory, "school-*.db"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
        }
    }
}

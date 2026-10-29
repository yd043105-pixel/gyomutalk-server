using Microsoft.Data.Sqlite;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

// School-confirmed graduation removes structured account data, including managed DB snapshots.
public static class GraduationErasure
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static bool Table(SqliteConnection db,string table,SqliteTransaction? tx=null)
    {using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";c.Parameters.AddWithValue("$table",table);return c.ExecuteScalar() is not null;}
    public static void Portal(SqliteConnection db,string[] ids,bool snapshot=false)
    {
        if(ids.Any(id=>!Guid.TryParseExact(id,"N",out _)))throw new InvalidOperationException("잘못된 파기 대상입니다.");
        using(var secure=db.CreateCommand()){secure.CommandText="PRAGMA secure_delete=ON";secure.ExecuteNonQuery();}
        using var tx=db.BeginTransaction();
        void Execute(string table,string sql,string id){if(!Table(db,table,tx))return;using var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();}
        // A retired code retains only its unlinked hash to prevent assignment to another student.
        using(var retired=db.CreateCommand()){retired.Transaction=tx;retired.CommandText="CREATE TABLE IF NOT EXISTS RetiredStudentCodes(Hash TEXT PRIMARY KEY)";retired.ExecuteNonQuery();}
        foreach(var id in ids)
        {
            Execute("StudentCodes","INSERT OR IGNORE INTO RetiredStudentCodes SELECT Hash FROM StudentCodes WHERE StudentId=$id",id);
            foreach(var (table,predicate) in new[]{
                ("ElectiveChoices","StudentId=$id"),("ElectiveReceipts","ActorId=$id"),("ElectiveApplications","StudentId=$id"),
                ("CounselingBookings","StudentId=$id OR RequesterId=$id"),
                ("Targets","UserId=$id OR StudentId=$id"),("NoticeSectionMembers","StudentId=$id"),
                ("SectionMembers","StudentId=$id"),("Families","ParentId=$id OR StudentId=$id"),
                ("GuardianRequests","ParentId=$id OR StudentId=$id"),("Invites","PersonId=$id"),
                ("StudentApplications","PersonId=$id"),("StudentCodes","StudentId=$id"),
                ("FamilyCodeAttempts","ParentId=$id"),("DeviceSessions","UserId=$id"),("FeedbackMail","ReportId IN (SELECT Id FROM FeedbackReports WHERE OwnerId=$id)"),("FeedbackReports","OwnerId=$id"),
                ("RegistrationAgreements","SubjectId=$id"),("TimetableNotices","UserId=$id"),
                ("MessageEvents","RecipientId=$id"),("Graduations","StudentId=$id")})Execute(table,$"DELETE FROM {table} WHERE {predicate}",id);
            // Append-only events remain, but their link to a removed person is erased in this transaction.
            if(Table(db,"BackendAudit",tx))
            {
                using var audit=db.CreateCommand();audit.Transaction=tx;
                audit.CommandText="DROP TRIGGER IF EXISTS BackendAudit_NoUpdate;UPDATE BackendAudit SET ActorId=NULL WHERE ActorId=$id;CREATE TRIGGER BackendAudit_NoUpdate BEFORE UPDATE ON BackendAudit BEGIN SELECT RAISE(ABORT,'append only'); END";
                audit.Parameters.AddWithValue("$id",id);audit.ExecuteNonQuery();
            }
            if(snapshot)Execute("PrivacyErasure","DELETE FROM PrivacyErasure WHERE Id=$id",id);
            Execute("People","DELETE FROM People WHERE Id=$id AND Role IN ('student','parent')",id);
        }
        tx.Commit();BackendDatabase.Check(db);
    }
    public static void Office(SqliteConnection db,string[] ids)
    {
        if(!Table(db,"ExternalOutbox"))return;
        using(var secure=db.CreateCommand()){secure.CommandText="PRAGMA secure_delete=ON";secure.ExecuteNonQuery();}
        using var tx=db.BeginTransaction();var changes=new List<(string Id,string Targets,string? Detail)>();
        using(var read=db.CreateCommand())
        {
            read.Transaction=tx;read.CommandText="SELECT Id,Targets,Detail FROM ExternalOutbox";
            using var r=read.ExecuteReader();while(r.Read())
            {
                var original=r.GetString(1);var targets=JsonSerializer.Deserialize<AnnouncementTarget[]>(original,Json)!;
                var filtered=targets.Where(t=>!ids.Contains(t.UserId)&&!ids.Contains(t.StudentId)).ToArray();
                var affectedUsers=targets.Where(t=>ids.Contains(t.UserId)||ids.Contains(t.StudentId)).Select(t=>t.UserId).ToHashSet(StringComparer.Ordinal);
                var detail=r.IsDBNull(2)?null:JsonSerializer.Deserialize<PortalNoticeDetail>(r.GetString(2),Json);
                // Old receipts without a child ID cannot distinguish siblings: erase ambiguous cache copies.
                var receipts=detail?.Receipts?.Where(t=>!ids.Contains(t.UserId)&&(string.IsNullOrEmpty(t.StudentId)?!affectedUsers.Contains(t.UserId):!ids.Contains(t.StudentId))).ToArray();
                if(filtered.Length!=targets.Length||receipts?.Length!=detail?.Receipts?.Length)
                    changes.Add((r.GetString(0),JsonSerializer.Serialize(filtered,Json),detail is null?null:JsonSerializer.Serialize(detail with{Receipts=receipts},Json)));
            }
        }
        foreach(var row in changes)
        {using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="UPDATE ExternalOutbox SET Targets=$targets,Detail=$detail,Error=CASE WHEN State='pending' THEN '졸업 대상자가 포함되어 발송을 취소했습니다. 현재 수신자를 확인하고 다시 작성하세요.' ELSE Error END,State=CASE WHEN State='pending' THEN 'erasure-cancelled' ELSE State END WHERE Id=$id";c.Parameters.AddWithValue("$targets",row.Targets);c.Parameters.AddWithValue("$detail",row.Detail is null?DBNull.Value:row.Detail);c.Parameters.AddWithValue("$id",row.Id);c.ExecuteNonQuery();}
        tx.Commit();BackendDatabase.Check(db);
    }
    public static void Compact(SqliteConnection db)
    {
        using var command=db.CreateCommand();command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";
        using(var reader=command.ExecuteReader())if(reader.Read()&&reader.GetInt32(0)!=0)throw new IOException("파기 후 DB 정리 재시도가 필요합니다.");
        command.CommandText="VACUUM";command.ExecuteNonQuery();
    }
    public static void Backups(string root,string[] ids,bool portal,string? configuredDirectory=null)
    {
        root=Path.GetFullPath(root);
        var directories = new[]{"backups","migration-backups","recovery-backups"}.Select(folder=>Path.Combine(root,folder));
        if(configuredDirectory is not null)directories=directories.Append(Path.GetFullPath(configuredDirectory));
        foreach(var directory in directories.Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal))
        {
            if(!Directory.Exists(directory))
            {
                var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
                if(configuredDirectory is not null&&string.Equals(directory,Path.GetFullPath(configuredDirectory),comparison)&&!string.Equals(directory,Path.Combine(root,"backups"),comparison))throw new IOException("별도 백업 장치의 졸업 정보 파기 확인이 필요합니다.");
                continue;
            }
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("별도로 연결된 백업은 학교의 파기 확인이 필요합니다.");
            var builtIn=new[]{"backups","migration-backups","recovery-backups"}.Any(folder=>string.Equals(directory,Path.Combine(root,folder),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal));
            var filter=builtIn?"*.db":portal?"announcements-*.db":"school-*.db";
            foreach(var file in Directory.EnumerateFiles(directory,filter,SearchOption.TopDirectoryOnly))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("백업 링크는 학교의 파기 확인이 필요합니다.");
                var staged=Path.Combine(directory,Guid.NewGuid().ToString("N")+".tmp");
                var lastWrite=File.GetLastWriteTimeUtc(file);
                try
                {
                    using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
                    {source.Open();BackendDatabase.Backup(source,staged);}
                    using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=staged,ForeignKeys=true,Pooling=false}.ToString()))
                    {target.Open();if(portal)Portal(target,ids,true);else Office(target,ids);Compact(target);}
                    File.Move(staged,file,true);
                    File.SetLastWriteTimeUtc(file,lastWrite);
                }
                finally{if(File.Exists(staged))File.Delete(staged);}
            }
        }
    }
}

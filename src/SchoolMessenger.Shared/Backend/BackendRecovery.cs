using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

public static class BackendRecovery
{
    // Explicit local administrator command. Does not expose a restore HTTP endpoint.
    public static bool TryRun(IConfiguration config,string prefix,string filename)
    {
        if(config["backend-restore"] is not {Length:>0} snapshot)return false;
        var root=Path.GetFullPath(config[prefix+":DataDirectory"]??(prefix=="School"?"data":"portal-data"));Directory.CreateDirectory(root);
        using var lease=BackendDatabase.Acquire(root);
        var live=Path.Combine(root,filename);var sourcePath=Path.GetFullPath(snapshot);
        if(string.Equals(sourcePath,live,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||!File.Exists(sourcePath))throw new InvalidOperationException("별도의 유효한 백업 파일을 지정하세요.");
        var staged=Path.Combine(root,"restore-"+Guid.NewGuid().ToString("N")+".tmp");
        var before=Path.Combine(root,"recovery-backups","before-restore-"+DeviceSessions.Now+".db");
        bool Table(SqliteConnection db,string name,SqliteTransaction? transaction=null){using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";c.Parameters.AddWithValue("$name",name);return c.ExecuteScalar() is not null;}
        try
        {
            using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourcePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            {source.Open();BackendDatabase.Check(source);if(!Table(source,prefix=="School"?"Users":"People")||Table(source,prefix=="School"?"People":"Users"))throw new InvalidOperationException("서버 종류가 다른 백업입니다.");BackendDatabase.Backup(source,staged);}
            using(var candidate=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=staged,Pooling=false,ForeignKeys=true}.ToString()))
            {
                candidate.Open();using var tx=candidate.BeginTransaction();using var command=candidate.CreateCommand();command.Transaction=tx;
                command.CommandText="CREATE TABLE IF NOT EXISTS BackendRecoveryState(Id INTEGER PRIMARY KEY CHECK(Id=1),ReviewRequired INTEGER NOT NULL,RestoredAt INTEGER NOT NULL);INSERT INTO BackendRecoveryState(Id,ReviewRequired,RestoredAt) VALUES(1,1,"+DeviceSessions.Now+") ON CONFLICT(Id) DO UPDATE SET ReviewRequired=1,RestoredAt=excluded.RestoredAt";command.ExecuteNonQuery();
                command.CommandText=prefix=="School"?"UPDATE Users SET SessionVersion=SessionVersion+1":"UPDATE People SET Version=Version+1";command.ExecuteNonQuery();
                foreach(var sql in new[]{"UPDATE DeviceSessions SET RevokedAt="+DeviceSessions.Now,"UPDATE TeacherProofs SET Active=0","UPDATE StudentCodes SET Active=0","UPDATE GuardianRequests SET State='rejected' WHERE State='pending'","UPDATE Invites SET UsedAt="+DeviceSessions.Now+" WHERE UsedAt IS NULL","UPDATE StaffProfiles SET ActivationHash=NULL,ExpiresAt=0 WHERE NeedsActivation=1","UPDATE ScheduledMessages SET State='cancelled',Error='백업 복원으로 예약을 취소했습니다. 확인 후 다시 예약하세요.' WHERE State='pending'"})
                {var table=sql.Split(' ')[1];if(Table(candidate,table,tx)){command.CommandText=sql;command.ExecuteNonQuery();}}
                if(Table(candidate,"ScheduledAttachments",tx)){command.CommandText="DELETE FROM ScheduledAttachments";command.ExecuteNonQuery();}
                foreach(var (table,folder) in new[]{("Attachments","files"),("Files","files"),("ExternalFiles","external-files")})
                {
                    if(!Table(candidate,table,tx))continue;
                    command.CommandText=$"SELECT Id FROM {table} WHERE DeletedAt IS NULL";var missing=new List<string>();
                    using(var reader=command.ExecuteReader())while(reader.Read()){var id=reader.GetString(0);if(!Guid.TryParseExact(id,"N",out _)||!File.Exists(Path.Combine(root,folder,id)))missing.Add(id);}
                    foreach(var id in missing){command.CommandText=$"UPDATE {table} SET DeletedAt=$now WHERE Id=$id";command.Parameters.Clear();command.Parameters.AddWithValue("$now",DeviceSessions.Now);command.Parameters.AddWithValue("$id",id);command.ExecuteNonQuery();}command.Parameters.Clear();
                    // Expired rows remain eligible for the startup cleanup; access is denied by ExpiresAt meanwhile.
                }
                tx.Commit();BackendDatabase.Check(candidate);
            }
            using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=live,Pooling=false,ForeignKeys=true}.ToString());target.Open();
            if(Table(target,prefix=="School"?"Users":"People"))BackendDatabase.Backup(target,before);
            try{using var candidate=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=staged,Pooling=false}.ToString());candidate.Open();candidate.BackupDatabase(target);BackendDatabase.Check(target);}
            catch{if(File.Exists(before)){using var undo=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=before,Pooling=false}.ToString());undo.Open();undo.BackupDatabase(target);}throw;}
            Console.WriteLine("백업 복원 완료. 세션·초대·교직원 연결을 재확인하고 파일 정리를 실행한 뒤 서버를 시작하세요.");return true;
        }
        finally{if(File.Exists(staged))File.Delete(staged);}
    }
}

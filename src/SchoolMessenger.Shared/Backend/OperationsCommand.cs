using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

public static class OperationsCommand
{
    public static bool TryRun(IConfiguration config, string prefix, string database)
    {
        var args = Environment.GetCommandLineArgs();
        var backup = args.Contains("--operations-backup"); var restore = args.Contains("--operations-restore-attachments");
        if (!backup && !restore) return false;
        if (backup && restore) throw new InvalidOperationException("백업과 복원 명령을 함께 실행할 수 없습니다.");
        var root = Path.GetFullPath(config[prefix + ":DataDirectory"] ?? (prefix == "School" ? "data" : "portal-data"));
        var path = Path.Combine(root, database); if (!File.Exists(path)) throw new IOException("초기화된 서버 데이터가 필요합니다.");
        var folder = config[prefix + ":BackupDirectory"] ?? throw new InvalidOperationException("백업 경로를 지정하세요.");
        using var lease = BackendDatabase.Acquire(root); // Refuse live-server updates/restores.
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = true }.ToString());
        db.Open(); BackendDatabase.Check(db);
        using(var table=db.CreateCommand()) { table.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table";table.Parameters.AddWithValue("$table",prefix=="School"?"Users":"People");if(Convert.ToInt32(table.ExecuteScalar())!=1)throw new InvalidOperationException("서버 종류와 데이터가 일치하지 않습니다."); }
        if (restore)
        {
            using(var review=db.CreateCommand()){review.CommandText="SELECT ReviewRequired FROM BackendRecoveryState WHERE Id=1";if(Convert.ToInt32(review.ExecuteScalar())!=1)throw new InvalidOperationException("DB 복원 후 권한 검토 대기 상태에서만 첨부를 복원하세요.");}
            AttachmentBackup.Prune(db, folder, prefix == "Portal");
            Console.WriteLine("Attachments restored: " + AttachmentBackup.Restore(db, Path.Combine(root, "files"), folder, prefix == "Portal"));
        }
        else BackendOperations.Run(() => { var connection = new SqliteConnection(db.ConnectionString); connection.Open(); return connection; }, "backup", () =>
        {
            BackendDatabase.Backup(db, Path.Combine(folder, (prefix == "School" ? "school-" : "announcements-") + DateTime.UtcNow.ToString("yyyyMMdd") + ".db"));
            if (config.GetValue(prefix + ":AttachmentBackup", false)) AttachmentBackup.Copy(db, Path.Combine(root, "files"), folder, prefix == "Portal");
        });
        Console.WriteLine("Operations command completed."); return true;
    }
}

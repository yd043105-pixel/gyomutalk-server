using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

// One-time retirement of app-owned data. Staff messages, attachments, schedules and accounts are not candidates.
public static class PcOnlyRetirement
{
    static readonly string[] RetiredTables = ["ExternalFiles", "ExternalOutbox", "GuardianRequests", "BridgeNonces", "ElectiveManagers"];
    public static void Apply(SqliteConnection db, string root)
    {
        bool Exists(string name)
        {
            using var q = db.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name"; q.Parameters.AddWithValue("$name", name);
            return Convert.ToInt32(q.ExecuteScalar()) > 0;
        }
        long Count(string sql) { using var q = db.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        var retired = RetiredTables.Where(Exists).ToArray();
        var staleNotices = Exists("TimetableNotices") && Exists("Users") ? Count("SELECT COUNT(*) FROM TimetableNotices WHERE UserId NOT IN (SELECT Id FROM Users)") : 0;
        var staleScans = Exists("FileVerification") ? Count("SELECT COUNT(*) FROM FileVerification WHERE Kind='external'") : 0;
        var files = Path.GetFullPath(Path.Combine(root, "external-files"));
        if (retired.Length == 0 && staleNotices == 0 && staleScans == 0 && !Directory.Exists(files)) return;
        // A retained table referencing an app table needs a separate migration, never a cascading delete.
        using (var tables = db.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            var names = new List<string>(); using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
            foreach (var name in names.Except(retired))
            {
                using var keys = db.CreateCommand(); keys.CommandText = "PRAGMA foreign_key_list(\"" + name.Replace("\"", "\"\"") + "\")";
                using var reader = keys.ExecuteReader(); while (reader.Read()) if (retired.Contains(reader.GetString(2))) throw new InvalidOperationException("앱 데이터 정리 전에 남은 기능의 참조 관계를 검토해야 합니다.");
            }
        }
        var parent = Path.GetFullPath(Path.Combine(root, "retired-app-backups"));
        if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("백업 폴더의 연결 경로를 확인하세요.");
        var backup = Path.Combine(parent, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
        BackendDatabase.Backup(db, Path.Combine(backup, "school.db"));
        using (var saved = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(backup, "school.db"), Mode = SqliteOpenMode.ReadOnly }.ToString())) { saved.Open(); BackendDatabase.Check(saved); }
        var paths = new List<string>();
        if (Directory.Exists(files))
        {
            if ((File.GetAttributes(files) & FileAttributes.ReparsePoint) != 0) throw new IOException("앱 파일 폴더의 연결 경로를 확인하세요.");
            void Walk(string directory)
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("앱 파일 폴더 안의 연결 경로를 확인하세요.");
                    if ((attributes & FileAttributes.Directory) != 0) Walk(path); else paths.Add(path);
                }
            }
            Walk(files);
            ZipFile.CreateFromDirectory(files, Path.Combine(backup, "external-files.zip"));
            using var archive = ZipFile.OpenRead(Path.Combine(backup, "external-files.zip"));
            foreach (var path in paths)
            {
                var entry = archive.GetEntry(Path.GetRelativePath(files, path).Replace('\\', '/')) ?? throw new IOException("앱 파일 백업을 확인하지 못했습니다.");
                using var input = File.OpenRead(path); using var copy = entry.Open();
                if (!SHA256.HashData(input).SequenceEqual(SHA256.HashData(copy))) throw new IOException("앱 파일 백업의 내용이 다릅니다.");
            }
        }
        var counts = retired.ToDictionary(t => t, t => Count("SELECT COUNT(*) FROM \"" + t + "\""));
        File.WriteAllText(Path.Combine(backup, "manifest.json"), JsonSerializer.Serialize(new { kind = "pc-only-retirement", tables = counts, staleTimetableNotices = staleNotices, retiredFileChecks = staleScans, externalFiles = paths.Count, backupRetained = true }));
        using (var tx = db.BeginTransaction())
        {
            foreach (var table in retired) { using var drop = db.CreateCommand(); drop.Transaction = tx; drop.CommandText = "DROP TABLE \"" + table + "\""; drop.ExecuteNonQuery(); }
            if (staleNotices > 0) { using var clean = db.CreateCommand(); clean.Transaction = tx; clean.CommandText = "DELETE FROM TimetableNotices WHERE UserId NOT IN (SELECT Id FROM Users)"; clean.ExecuteNonQuery(); }
            if (staleScans > 0) { using var clean = db.CreateCommand(); clean.Transaction = tx; clean.CommandText = "DELETE FROM FileVerification WHERE Kind='external'"; clean.ExecuteNonQuery(); }
            tx.Commit();
        }
        BackendDatabase.Check(db);
        // Delete only the app-owned directory after both backup verification and the database transaction succeed.
        foreach (var path in paths) File.Delete(path);
        if (Directory.Exists(files))
        {
            foreach (var path in Directory.EnumerateDirectories(files, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(path);
            Directory.Delete(files);
        }
    }
}

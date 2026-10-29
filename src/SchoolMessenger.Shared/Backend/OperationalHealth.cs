using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

public static class OperationalHealth
{
    public static void Map(WebApplication app, Func<SqliteConnection> open, string root, string prefix)
    {
        var key = app.Configuration["Operations:MonitorKey"];
        if (key is not null && (key.Length < 32 || key.Length > 256)) throw new InvalidOperationException("운영 감시 키는 32~256자여야 합니다.");
        app.MapGet("/health/operations", (HttpContext context) =>
        {
            // A tunnel also appears as localhost: loopback alone is not authentication.
            var supplied = context.Request.Headers["X-Operations-Key"].ToString();
            if (key is null || supplied.Length > 256 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)))) return Results.NotFound();
            var issues = new List<string>();
            try
            {
                using var db = open(); using var query = db.CreateCommand();
                query.CommandText = "SELECT Name,LastSuccess,LastFailure,ErrorCode FROM BackendJobs WHERE Name IN ('backup','cleanup','graduation')";
                using var reader = query.ExecuteReader(); var backupSeen = false;
                while (reader.Read())
                {
                    var name = reader.GetString(0); var success = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    if (!reader.IsDBNull(3)) issues.Add(name + "-failed");
                    if (name == "backup") { backupSeen = true; if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - success > 30 * 3_600_000L) issues.Add("backup-stale"); }
                }
                if (!backupSeen) issues.Add("backup-missing");
                var backupFolder=app.Configuration[prefix+":BackupDirectory"]??Path.Combine(root,"backups");
                if(!Directory.Exists(backupFolder)||!Directory.EnumerateFiles(backupFolder,prefix=="School"?"school-*.db":"announcements-*.db").Any(file=>File.GetLastWriteTimeUtc(file)>DateTime.UtcNow.AddHours(-30)))issues.Add("backup-copy-unavailable");
                var free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
                if (free < app.Configuration.GetValue<long>("Operations:MinimumFreeBytes", 2L * 1024 * 1024 * 1024)) issues.Add("disk-low");
            }
            catch { issues.Add("storage-unavailable"); }
            // No paths, addresses, credentials, user counts or message contents leave this endpoint.
            return Results.Json(new { status = issues.Count == 0 ? "ok" : "degraded", service = prefix == "School" ? "office" : "announcements", issues }, statusCode: issues.Count == 0 ? 200 : 503);
        });
    }
}

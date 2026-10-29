using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

// One managed copy per immutable attachment; snapshots must not extend its original expiry.
public static class AttachmentBackup
{
    public static readonly object Gate = new();
    sealed record Entry(string Id, long ExpiresAt, string Sha256);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static void Plain(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("백업 경로에 링크를 사용할 수 없습니다.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("백업 파일에 링크를 사용할 수 없습니다.");
    }
    static string FilePath(string folder, string id, string suffix = "")
    {
        if (!Guid.TryParse(id, out _) || id.Contains('/') || id.Contains('\\')) throw new IOException("잘못된 첨부 식별자입니다.");
        var path = Path.GetFullPath(Path.Combine(folder, id + suffix));
        if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("잘못된 백업 경로입니다.");
        Plain(path); return path;
    }
    static List<(string Id, long Expiry)> Active(SqliteConnection db, bool portal)
    {
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT Id,ExpiresAt FROM {(portal ? "Files" : "Attachments")} WHERE DeletedAt IS NULL AND ExpiresAt>$now";
        query.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var reader = query.ExecuteReader(); var rows = new List<(string, long)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }
    public static void Prune(SqliteConnection db, string backup, bool portal)
    {
        var folder = Path.Combine(Path.GetFullPath(backup), "attachments");
        Plain(Path.Combine(folder, "check")); if (!Directory.Exists(folder)) return;
        var active = Active(db, portal).ToDictionary(row => row.Id, row => row.Expiry);
        foreach (var metadata in Directory.EnumerateFiles(folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(metadata); var path = FilePath(folder, id);
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FilePath(folder, id, ".json")), Json) ?? throw new IOException("손상된 첨부 백업 기록입니다.");
            if (entry.Id != id) throw new IOException("첨부 백업 기록이 일치하지 않습니다.");
            if (entry.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || !active.ContainsKey(id))
            { File.Delete(path); File.Delete(metadata); }
        }
        // Interrupted copies have no usable expiry record and must not become unmanaged replicas.
        foreach (var path in Directory.EnumerateFiles(folder))
            if (Path.GetExtension(path) != ".json" && !File.Exists(path + ".json"))
            {
                var id = Path.GetFileName(path);
                if (id.EndsWith(".json.tmp", StringComparison.Ordinal)) id = id[..^9];
                else if (id.EndsWith(".tmp", StringComparison.Ordinal)) id = id[..^4];
                FilePath(folder, id); Plain(path); File.Delete(path);
            }
    }
    public static void Copy(SqliteConnection db, string files, string backup, bool portal)
    {
        var folder = Path.Combine(Path.GetFullPath(backup), "attachments");
        Plain(Path.Combine(folder, "check")); Directory.CreateDirectory(folder); Prune(db, backup, portal);
        foreach (var (id, expiry) in Active(db, portal))
        {
            var source = FilePath(files, id); var target = FilePath(folder, id); var temporary = FilePath(folder, id, ".tmp");
            var metadataTemporary = FilePath(folder, id, ".json.tmp");
            if (!StoredFiles.Ready(db, portal ? "portal" : "office", id, source)) throw new IOException("첨부 원본 무결성 확인 실패");
            var hash = StoredFiles.Hash(source);
            try
            {
                File.Copy(source, temporary, true);
                if (StoredFiles.Hash(temporary) != hash || expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new IOException("첨부 복사 무결성 또는 보관 기간 확인 실패");
                // Write expiry first: a crash must never leave a copy without a deletion deadline.
                File.WriteAllText(metadataTemporary, JsonSerializer.Serialize(new Entry(id, expiry, hash), Json));
                File.Move(metadataTemporary, FilePath(folder, id, ".json"), true);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(metadataTemporary)) File.Delete(metadataTemporary); }
        }
    }
    public static int Restore(SqliteConnection db, string files, string backup, bool portal)
    {
        var folder = Path.Combine(Path.GetFullPath(backup), "attachments"); var restored = 0;
        foreach (var (id, expiry) in Active(db, portal))
        {
            var metadata = FilePath(folder, id, ".json"); if (!File.Exists(metadata)) continue;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(metadata), Json) ?? throw new IOException("손상된 첨부 백업 기록입니다.");
            if (entry.Id != id || entry.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() || entry.ExpiresAt < expiry) continue;
            var source = FilePath(folder, id); var target = FilePath(files, id);
            if (StoredFiles.Hash(source) != entry.Sha256 || !StoredFiles.Ready(db, portal ? "portal" : "office", id, source)) throw new IOException("첨부 백업 무결성 확인 실패");
            if (File.Exists(target)) { if (StoredFiles.Hash(target) != entry.Sha256) throw new IOException("기존 첨부와 백업이 다릅니다."); continue; }
            var temporary = FilePath(files, id, ".tmp");
            try
            {
                File.Copy(source, temporary);
                if (StoredFiles.Hash(temporary) != entry.Sha256 || expiry <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new IOException("첨부 복원 확인 실패");
                File.Move(temporary, target); restored++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return restored;
    }
}

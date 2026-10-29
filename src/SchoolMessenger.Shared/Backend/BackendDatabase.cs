using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

public static class BackendDatabase
{
    public static FileStream Acquire(string root) => new(Path.Combine(root, ".backend.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public static void Migrate(SqliteConnection db, string root)
    {
        using var version = db.CreateCommand();
        version.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='BackendMigrations'";
        var applied = Convert.ToInt32(version.ExecuteScalar()) != 0;
        if (applied)
        {
            version.CommandText = "SELECT COUNT(*) FROM BackendMigrations WHERE Version=5";
            applied = Convert.ToInt32(version.ExecuteScalar()) != 0;
        }
        if (applied) return;
        using (var tables = db.CreateCommand())
        {
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','People')";
            if (Convert.ToInt32(tables.ExecuteScalar()) != 0)
            {
                var path = Path.Combine(root, "migration-backups"); Directory.CreateDirectory(path);
                Backup(db, Path.Combine(path, "before-backend-5-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".db"));
            }
        }
        using var tx = db.BeginTransaction(); using var change = db.CreateCommand(); change.Transaction = tx;
        change.CommandText = """
            CREATE TABLE IF NOT EXISTS BackendMigrations(Version INTEGER PRIMARY KEY,AppliedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS BackendRecoveryState(Id INTEGER PRIMARY KEY CHECK(Id=1),ReviewRequired INTEGER NOT NULL,RestoredAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS GuardianRequests(ParentId TEXT NOT NULL,StudentId TEXT NOT NULL,CodeHash TEXT NOT NULL,RequestedAt INTEGER NOT NULL,State TEXT NOT NULL,ReviewedBy TEXT,ReviewedAt INTEGER,PRIMARY KEY(ParentId,StudentId));
            CREATE TABLE IF NOT EXISTS BackendRoot(UserId TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS FileVerification(Kind TEXT NOT NULL,FileId TEXT NOT NULL,State TEXT NOT NULL,Sha256 TEXT,UpdatedAt INTEGER NOT NULL,PRIMARY KEY(Kind,FileId));
            CREATE TABLE IF NOT EXISTS DeviceSessions(Id TEXT PRIMARY KEY,TokenHash TEXT NOT NULL UNIQUE,UserId TEXT NOT NULL,
              UserVersion TEXT NOT NULL,CreatedAt INTEGER NOT NULL,ExpiresAt INTEGER NOT NULL,RevokedAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_DeviceSessions_User ON DeviceSessions(UserId,ExpiresAt);
            CREATE TABLE IF NOT EXISTS BridgeNonces(Service TEXT NOT NULL,Nonce TEXT NOT NULL,ExpiresAt INTEGER NOT NULL,PRIMARY KEY(Service,Nonce));
            CREATE INDEX IF NOT EXISTS IX_BridgeNonces_Expiry ON BridgeNonces(ExpiresAt);
            CREATE TABLE IF NOT EXISTS MessageEvents(Sequence INTEGER PRIMARY KEY AUTOINCREMENT,EventKey TEXT NOT NULL UNIQUE,
              RecipientId TEXT NOT NULL,Kind TEXT NOT NULL,ResourceId TEXT NOT NULL,CreatedAt INTEGER NOT NULL,
              DeliveredAt INTEGER,Attempts INTEGER NOT NULL DEFAULT 0,NextAttempt INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_MessageEvents_Recipient ON MessageEvents(RecipientId,Sequence);
            CREATE INDEX IF NOT EXISTS IX_MessageEvents_Pending ON MessageEvents(NextAttempt) WHERE DeliveredAt IS NULL;
            CREATE TABLE IF NOT EXISTS BackendJobs(Name TEXT PRIMARY KEY,StartedAt INTEGER,LastSuccess INTEGER,LastFailure INTEGER,
              Attempts INTEGER NOT NULL DEFAULT 0,ErrorCode TEXT);
            CREATE TABLE IF NOT EXISTS BackendAudit(Sequence INTEGER PRIMARY KEY AUTOINCREMENT,At INTEGER NOT NULL,
              ActorId TEXT,Event TEXT NOT NULL,Result TEXT NOT NULL,TraceId TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS BackendAudit_NoUpdate BEFORE UPDATE ON BackendAudit BEGIN SELECT RAISE(ABORT,'append only'); END;
            CREATE TRIGGER IF NOT EXISTS BackendAudit_NoDelete BEFORE DELETE ON BackendAudit BEGIN SELECT RAISE(ABORT,'append only'); END;
            INSERT INTO BackendMigrations(Version,AppliedAt) VALUES(5,CAST(strftime('%s','now') AS INTEGER)*1000);
            """;
        change.ExecuteNonQuery(); tx.Commit();
    }
    public static void Check(SqliteConnection db)
    {
        using var check = db.CreateCommand(); check.CommandText = "PRAGMA quick_check";
        if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) throw new InvalidOperationException("데이터베이스 무결성 확인 실패");
        check.CommandText = "PRAGMA foreign_key_check";
        using var reader = check.ExecuteReader(); if (reader.Read()) throw new InvalidOperationException("데이터베이스 참조 무결성 확인 실패");
    }
    public static void Backup(SqliteConnection source, string destination)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        // SQLite's native Windows path handling can reject long appended names.
        // Keep the atomic replacement on the same volume without nesting GUID suffixes.
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            { target.Open(); source.BackupDatabase(target); Check(target); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

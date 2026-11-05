using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Shared;

var fixture = Path.GetFullPath(Path.Combine(".test-data", "pc-only-retirement-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(fixture);
var checks = new List<string>();
void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); checks.Add(text); Console.WriteLine("PASS " + text); }
SqliteConnection Open(string root) { Directory.CreateDirectory(root); var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "school.db"), ForeignKeys = true }.ToString()); db.Open(); return db; }
void Execute(SqliteConnection db, string sql) { using var q = db.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }
long Count(SqliteConnection db, string sql) { using var q = db.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
using (var db = Open(fixture))
{
    Execute(db, """
        CREATE TABLE Users(Id TEXT PRIMARY KEY); INSERT INTO Users VALUES('teacher');
        CREATE TABLE Messages(Id TEXT PRIMARY KEY,SenderId TEXT REFERENCES Users(Id),Body TEXT); INSERT INTO Messages VALUES('message','teacher','keep-message');
        CREATE TABLE Attachments(Id TEXT PRIMARY KEY,MessageId TEXT REFERENCES Messages(Id)); INSERT INTO Attachments VALUES('attachment','message');
        CREATE TABLE OfficeEvents(Id TEXT PRIMARY KEY); INSERT INTO OfficeEvents VALUES('event');
        CREATE TABLE TimetableBatches(Id TEXT PRIMARY KEY); INSERT INTO TimetableBatches VALUES('base-timetable');
        CREATE TABLE TimetableNotices(UserId TEXT); INSERT INTO TimetableNotices VALUES('teacher'),('retired-student');
        CREATE TABLE ExternalOutbox(Id TEXT PRIMARY KEY); INSERT INTO ExternalOutbox VALUES('retired-notice');
        CREATE TABLE ExternalFiles(Id TEXT PRIMARY KEY,JobId TEXT REFERENCES ExternalOutbox(Id)); INSERT INTO ExternalFiles VALUES('retired-file','retired-notice');
        CREATE TABLE GuardianRequests(Id TEXT PRIMARY KEY); INSERT INTO GuardianRequests VALUES('retired-link');
        CREATE TABLE BridgeNonces(Id TEXT PRIMARY KEY); INSERT INTO BridgeNonces VALUES('retired-nonce');
        """);
    Directory.CreateDirectory(Path.Combine(fixture, "files")); File.WriteAllText(Path.Combine(fixture, "files", "attachment"), "keep-attachment");
    Directory.CreateDirectory(Path.Combine(fixture, "external-files")); File.WriteAllText(Path.Combine(fixture, "external-files", "retired-file"), "synthetic-retired-file");
    PcOnlyRetirement.Apply(db, fixture);
    Check(Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('ExternalFiles','ExternalOutbox','GuardianRequests','BridgeNonces')") == 0, "retired app tables are removed");
    Check(Count(db, "SELECT COUNT(*) FROM Users") == 1 && Count(db, "SELECT COUNT(*) FROM Messages WHERE Body='keep-message'") == 1 && Count(db, "SELECT COUNT(*) FROM Attachments") == 1 && Count(db, "SELECT COUNT(*) FROM OfficeEvents") == 1 && Count(db, "SELECT COUNT(*) FROM TimetableBatches") == 1, "staff accounts, messages, attachments, calendar and teacher timetables are retained");
    Check(Count(db, "SELECT COUNT(*) FROM TimetableNotices") == 1 && Count(db, "SELECT COUNT(*) FROM TimetableNotices WHERE UserId='teacher'") == 1, "only timetable notices with no retained staff account are removed");
    var backup = Directory.GetDirectories(Path.Combine(fixture, "retired-app-backups")).Single();
    using (var saved = new SqliteConnection("Data Source=" + Path.Combine(backup, "school.db"))) { saved.Open(); Check(Count(saved, "SELECT COUNT(*) FROM ExternalFiles") == 1 && Count(saved, "SELECT COUNT(*) FROM TimetableNotices") == 2, "the verified pre-cleanup database backup includes removed records"); }
    using (var zip = ZipFile.OpenRead(Path.Combine(backup, "external-files.zip"))) using (var reader = new StreamReader(zip.GetEntry("retired-file")!.Open())) Check(reader.ReadToEnd() == "synthetic-retired-file", "the app-owned file is backed up before removal");
    Check(!Directory.Exists(Path.Combine(fixture, "external-files")) && File.ReadAllText(Path.Combine(fixture, "files", "attachment")) == "keep-attachment", "physical cleanup leaves the retained attachment folder unchanged");
    BackendDatabase.Check(db); PcOnlyRetirement.Apply(db, fixture);
    Check(Directory.GetDirectories(Path.Combine(fixture, "retired-app-backups")).Length == 1, "restarting after migration does not repeat the backup or delete retained data");
}
var guarded = Path.Combine(fixture, "retained-reference");
using (var db = Open(guarded))
{
    Execute(db, "CREATE TABLE ExternalOutbox(Id TEXT PRIMARY KEY); CREATE TABLE Retained(Id TEXT REFERENCES ExternalOutbox(Id)); INSERT INTO ExternalOutbox VALUES('keep'); INSERT INTO Retained VALUES('keep');");
    var blocked = false; try { PcOnlyRetirement.Apply(db, guarded); } catch (InvalidOperationException) { blocked = true; }
    Check(blocked && Count(db, "SELECT COUNT(*) FROM Retained") == 1 && Count(db, "SELECT COUNT(*) FROM ExternalOutbox") == 1, "a retained reference blocks destructive cleanup");
}
var output = Path.GetFullPath("artifacts/pc-only-retirement-checks.json"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new { checks, realSchoolData = false, backupRetained = true }));
Console.WriteLine(checks.Count + " PC-only retirement checks passed.");

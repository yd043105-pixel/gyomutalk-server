using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

// 수강신청: students choose electives through a personal link served by the school's own Google Apps Script web app.
// The school server is the only side that talks to Google. Names leave encrypted with a key that travels only in each
// student's link (after #, which browsers never send), and answers come back keyed by a random token.
public static class CourseSelection
{
    public const int ScriptVersion = 1;
    public sealed record Group(string Name, int Pick, string[] Courses);
    public sealed record StudentRow(int Grade, int ClassNo, int Number, string Name);
    record Roster(int[]? Grades, StudentRow[]? Students);
    record RoundRequest(string? Title, int Grade, string? Closes, Group[]? Groups);
    record Connection(string? Url);
    record Paste(string? Text);
    sealed class CourseException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
    static readonly HttpClient Google = new() { Timeout = TimeSpan.FromSeconds(90) };
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly Regex ScriptUrl = new(@"^https://script\.google\.com/macros/s/[A-Za-z0-9_-]{20,200}/exec$", RegexOptions.CultureInvariant);
    static string User(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Random64(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] Bytes64(string value) { var s = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }

    public static void Initialize(Store store)
    {
        store.Execute("""
        CREATE TABLE IF NOT EXISTS CourseSettings(Id INTEGER PRIMARY KEY CHECK(Id=1),Secret TEXT NOT NULL,ScriptUrl TEXT NOT NULL DEFAULT '',ScriptVersion INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS CourseStudents(Id TEXT PRIMARY KEY,Grade INTEGER NOT NULL,ClassNo INTEGER NOT NULL,Number INTEGER NOT NULL,Name TEXT NOT NULL,UNIQUE(Grade,ClassNo,Number));
        CREATE TABLE IF NOT EXISTS CourseRounds(Id TEXT PRIMARY KEY,Title TEXT NOT NULL,Grade INTEGER NOT NULL,Closes INTEGER NOT NULL,Groups TEXT NOT NULL,CreatedAt INTEGER NOT NULL,PublishedAt INTEGER,PulledAt INTEGER);
        CREATE TABLE IF NOT EXISTS CourseLinks(RoundId TEXT NOT NULL REFERENCES CourseRounds(Id) ON DELETE CASCADE,StudentId TEXT NOT NULL REFERENCES CourseStudents(Id) ON DELETE CASCADE,Token TEXT NOT NULL UNIQUE,Key TEXT NOT NULL,PRIMARY KEY(RoundId,StudentId));
        CREATE TABLE IF NOT EXISTS CourseChoices(RoundId TEXT NOT NULL,StudentId TEXT NOT NULL,Picks TEXT NOT NULL,SubmittedAt INTEGER NOT NULL,PRIMARY KEY(RoundId,StudentId),FOREIGN KEY(RoundId,StudentId) REFERENCES CourseLinks(RoundId,StudentId) ON DELETE CASCADE);
        """);
        store.Execute("INSERT OR IGNORE INTO CourseSettings(Id,Secret) VALUES(1,$secret)", ("$secret", Random64(32)));
        if (!store.Query("PRAGMA table_info(OfficePermissions)", r => r.GetString(1)).Contains("CourseManager"))
            store.Execute("ALTER TABLE OfficePermissions ADD COLUMN CourseManager INTEGER NOT NULL DEFAULT 0");
    }

    // The curriculum duty alone opens this menu; being an administrator does not, since the results name students.
    public static bool Manager(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var c = Store.Command(db, "SELECT 1 FROM Users u JOIN OfficePermissions p ON p.UserId=u.Id WHERE u.Id=$id AND u.Active=1 AND p.CourseManager=1", tx, ("$id", id));
        return c.ExecuteScalar() is not null;
    }

    public static void Map(RouteGroupBuilder api, Store store, DeploymentReview review, IConfiguration config, IWebHostEnvironment environment)
    {
        bool Enabled() => store.Query("SELECT 1 FROM SchoolFeatures WHERE Name='courses' AND Enabled=0", r => 1).Count == 0;
        // Every route below checks the duty and the switch first and turns its own refusals into a readable answer.
        async Task<IResult> Guard(HttpContext c, Func<Task<object?>> action)
        {
            try
            {
                using (var db = store.Open()) if (!Manager(db, null, User(c))) return Results.Json(new { error = "교육과정 담당 권한이 필요합니다. 관리자에게 요청하세요." }, statusCode: 403);
                if (!Enabled()) return Results.Json(new { error = "관리자가 수강신청 기능을 꺼 두었습니다." }, statusCode: 403);
                var value = await action();
                return value is IResult result ? result : Results.Ok(value ?? new { ok = true });
            }
            catch (CourseException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
            catch (FilePolicyException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
        }
        // A handler taking only HttpContext would be read as a plain RequestDelegate and its result dropped, hence the unused token.
        api.MapGet("/courses/access", (HttpContext c) => { using var db = store.Open(); return Results.Ok(new { manager = Manager(db, null, User(c)) && Enabled() }); });
        api.MapGet("/courses/setup", (HttpContext c, CancellationToken _) => Guard(c, () =>
        {
            var (url, _, version) = Settings(store);
            var grades = store.Query("SELECT Grade,COUNT(*) FROM CourseStudents GROUP BY Grade ORDER BY Grade", r => new { grade = r.GetInt32(0), count = r.GetInt32(1) });
            return Task.FromResult<object?>(new { connected = url.Length > 0, scriptUrl = url, scriptCurrent = version == ScriptVersion, grades });
        }));
        api.MapGet("/courses/script", (HttpContext c, CancellationToken _) => Guard(c, () => Task.FromResult<object?>(new { code = Script(Settings(store).Secret), version = ScriptVersion })));
        api.MapPost("/courses/connect", (Connection request, HttpContext c) => Guard(c, async () =>
        {
            var url = (request.Url ?? "").Trim();
            // Development checks point at a local stand-in for Google; a school server only accepts the Apps Script address.
            if (!ScriptUrl.IsMatch(url) && !(environment.IsDevelopment() && url == config["Courses:TestScriptUrl"])) throw new CourseException("배포 후 나온 ‘웹 앱 URL’을 그대로 붙여넣으세요. https://script.google.com/macros/s/…/exec 모양입니다.");
            var answer = await Call(store, review, new { action = "ping" }, url);
            var version = answer.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
            store.Execute("UPDATE CourseSettings SET ScriptUrl=$url,ScriptVersion=$version WHERE Id=1", ("$url", url), ("$version", version));
            Audit(store, c, "course.connect", "google");
            return new { ok = true, scriptCurrent = version == ScriptVersion };
        }));

        api.MapGet("/courses/students", (HttpContext c, CancellationToken _) => Guard(c, () => Task.FromResult<object?>(Students(store))));
        // Reading only shows what a file holds; nothing is saved until the teacher confirms with /import.
        api.MapPost("/courses/students/read", (HttpContext c, CancellationToken _) => Guard(c, async () =>
        {
            if (c.Request.HasFormContentType)
            {
                var form = await c.Request.ReadFormAsync(c.RequestAborted); var file = form.Files.GetFile("file");
                if (file is null || form.Files.Count != 1 || file.Length is < 1 or > 10_485_760) throw new CourseException("명단 파일은 10MB 이하 하나를 올려 주세요.", 413);
                var name = FilePolicy.Name(file.FileName); var extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension is ".xls") throw new CourseException("옛 엑셀(.xls)은 읽지 못합니다. 엑셀에서 ‘다른 이름으로 저장 → Excel 통합 문서(.xlsx)’로 바꾸거나, 명단을 복사해 붙여넣으세요.");
                if (extension is not (".xlsx" or ".csv")) throw new CourseException("나이스에서 받은 .xlsx 또는 .csv 파일을 올려 주세요.");
                var path = Path.Combine(store.Root, "roster-" + Guid.NewGuid().ToString("N") + extension);
                try
                {
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await file.CopyToAsync(output, c.RequestAborted);
                    await FilePolicy.Scan(path, name, environment.IsDevelopment(), config["School:ScannerPath"], c.RequestAborted);
                    return RosterReader.Read(extension == ".csv" ? [RosterReader.Csv(await File.ReadAllBytesAsync(path))] : TimetableWorkbook.Sheets(path).Select(RosterReader.Rows));
                }
                finally { try { File.Delete(path); } catch (IOException) { } }
            }
            var paste = await c.Request.ReadFromJsonAsync<Paste>(Web, c.RequestAborted);
            if (paste?.Text is not { Length: > 0 and <= 500_000 } text) throw new CourseException("엑셀에서 명단을 머리글(학년·반·번호·성명)까지 복사해 붙여넣으세요.");
            return RosterReader.Read([RosterReader.Text(text)]);
        }));
        api.MapPost("/courses/students/import", (Roster request, HttpContext c) => Guard(c, async () =>
        {
            var rows = request.Students ?? []; var grades = (request.Grades ?? []).Distinct().ToArray();
            if (rows.Length is 0 or > 3000 || grades.Length == 0 || rows.Any(r => !grades.Contains(r.Grade) || !Valid(r)) || rows.DistinctBy(r => (r.Grade, r.ClassNo, r.Number)).Count() != rows.Length)
                throw new CourseException("명단의 학년·반·번호·이름을 확인하세요. 같은 번호가 두 번 있으면 넣을 수 없습니다.");
            using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false);
            var existing = new List<(string Id, StudentRow Row)>();
            foreach (var grade in grades)
            {
                using var q = Store.Command(db, "SELECT Id,Grade,ClassNo,Number,Name FROM CourseStudents WHERE Grade=$g", tx, ("$g", grade));
                using var r = q.ExecuteReader(); while (r.Read()) existing.Add((r.GetString(0), new(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4))));
            }
            var incoming = rows.Select(r => r with { Name = r.Name.Trim() }).ToHashSet();
            // A student found again with the same class, number and name keeps their links and answers.
            foreach (var gone in existing.Where(e => !incoming.Contains(e.Row))) { using var d = Store.Command(db, "DELETE FROM CourseStudents WHERE Id=$id", tx, ("$id", gone.Id)); d.ExecuteNonQuery(); }
            var kept = existing.Select(e => e.Row).ToHashSet();
            foreach (var row in incoming.Where(r => !kept.Contains(r))) Insert(db, tx, row);
            StaffAccounts.Audit(db, tx, User(c), "course.roster", string.Join(",", grades)); tx.Commit();
            var result = new { ok = true, added = incoming.Count(r => !kept.Contains(r)), removed = existing.Count(e => !incoming.Contains(e.Row)) };
            await Resync(store, review, grades);
            return result;
        }));
        api.MapPost("/courses/students", (StudentRow request, HttpContext c) => Guard(c, () =>
        {
            var row = request with { Name = (request.Name ?? "").Trim() };
            if (!Valid(row)) throw new CourseException("학년(1~3)·반·번호·이름을 확인하세요.");
            using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false);
            try { Insert(db, tx, row); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new CourseException($"{row.Grade}학년 {row.ClassNo}반 {row.Number}번 학생이 이미 있습니다.", 409); }
            StaffAccounts.Audit(db, tx, User(c), "course.student.add", $"{row.Grade}-{row.ClassNo}-{row.Number}"); tx.Commit();
            return Task.FromResult<object?>(null);
        }));
        api.MapPost("/courses/students/{id}/delete", (string id, HttpContext c) => Guard(c, async () =>
        {
            var grade = store.Query("SELECT Grade FROM CourseStudents WHERE Id=$id", r => r.GetInt32(0), ("$id", id));
            using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false);
            using (var d = Store.Command(db, "DELETE FROM CourseStudents WHERE Id=$id", tx, ("$id", id))) if (d.ExecuteNonQuery() != 1) throw new CourseException("이미 지워진 학생입니다.", 404);
            StaffAccounts.Audit(db, tx, User(c), "course.student.delete", id); tx.Commit();
            await Resync(store, review, [.. grade]);
            return null;
        }));

        api.MapGet("/courses/rounds", (HttpContext c, CancellationToken _) => Guard(c, () => Task.FromResult<object?>(store.Query("""
            SELECT r.Id,r.Title,r.Grade,r.Closes,r.PublishedAt,r.PulledAt,
              (SELECT COUNT(*) FROM CourseStudents s WHERE s.Grade=r.Grade),
              (SELECT COUNT(*) FROM CourseChoices h JOIN CourseStudents s ON s.Id=h.StudentId WHERE h.RoundId=r.Id AND s.Grade=r.Grade)
            FROM CourseRounds r ORDER BY r.CreatedAt DESC
            """, r => new { id = r.GetString(0), title = r.GetString(1), grade = r.GetInt32(2), closes = r.GetInt64(3), publishedAt = r.IsDBNull(4) ? (long?)null : r.GetInt64(4), pulledAt = r.IsDBNull(5) ? (long?)null : r.GetInt64(5), students = r.GetInt32(6), submitted = r.GetInt32(7) }))));
        api.MapPost("/courses/rounds", (RoundRequest request, HttpContext c) => Guard(c, () => SaveRound(store, review, c, request, null)));
        api.MapPost("/courses/rounds/{id}", (string id, RoundRequest request, HttpContext c) => Guard(c, () => SaveRound(store, review, c, request, id)));
        api.MapPost("/courses/rounds/{id}/delete", (string id, HttpContext c) => Guard(c, async () =>
        {
            var round = Round(store, id);
            // Taking the round off Google first means no link keeps working for a round that is gone here.
            if (round.PublishedAt is not null) await Call(store, review, new { action = "remove", round = id });
            store.Execute("DELETE FROM CourseRounds WHERE Id=$id", ("$id", id)); Audit(store, c, "course.round.delete", id);
            return null;
        }));
        api.MapPost("/courses/rounds/{id}/publish", (string id, HttpContext c) => Guard(c, async () =>
        {
            var count = await Publish(store, review, id); Audit(store, c, "course.round.publish", id);
            return new { ok = true, students = count };
        }));
        api.MapPost("/courses/rounds/{id}/refresh", (string id, HttpContext c) => Guard(c, async () => { await Pull(store, review, id); return Summary(store, id); }));
        api.MapGet("/courses/rounds/{id}/results", (string id, HttpContext c) => Guard(c, () => Task.FromResult<object?>(Summary(store, id))));
        api.MapGet("/courses/rounds/{id}/links", (string id, HttpContext c) => Guard(c, () =>
        {
            var url = Settings(store).Url;
            return Task.FromResult<object?>(store.Query("SELECT s.Grade,s.ClassNo,s.Number,s.Name,l.Token,l.Key FROM CourseLinks l JOIN CourseStudents s ON s.Id=l.StudentId JOIN CourseRounds r ON r.Id=l.RoundId WHERE l.RoundId=$id AND s.Grade=r.Grade ORDER BY s.ClassNo,s.Number",
                r => new { grade = r.GetInt32(0), classNo = r.GetInt32(1), number = r.GetInt32(2), name = r.GetString(3), link = url + "?t=" + r.GetString(4) + "#" + r.GetString(5) }, ("$id", id)));
        }));
    }

    static void Audit(Store store, HttpContext c, string action, string resource)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); StaffAccounts.Audit(db, tx, User(c), action, resource); tx.Commit();
    }
    static bool Valid(StudentRow r) => r.Grade is >= 1 and <= 3 && r.ClassNo is >= 1 and <= 30 && r.Number is >= 1 and <= 99 && !string.IsNullOrWhiteSpace(r.Name) && r.Name.Trim().Length <= 40 && !r.Name.Any(char.IsControl);
    static void Insert(SqliteConnection db, SqliteTransaction tx, StudentRow row)
    {
        using var add = Store.Command(db, "INSERT INTO CourseStudents VALUES($id,$g,$c,$n,$name)", tx, ("$id", Guid.NewGuid().ToString("N")), ("$g", row.Grade), ("$c", row.ClassNo), ("$n", row.Number), ("$name", row.Name));
        add.ExecuteNonQuery();
    }
    static object Students(Store store) => store.Query("SELECT Id,Grade,ClassNo,Number,Name FROM CourseStudents ORDER BY Grade,ClassNo,Number",
        r => new { id = r.GetString(0), grade = r.GetInt32(1), classNo = r.GetInt32(2), number = r.GetInt32(3), name = r.GetString(4) });
    static (string Url, string Secret, int Version) Settings(Store store) =>
        store.Query("SELECT ScriptUrl,Secret,ScriptVersion FROM CourseSettings WHERE Id=1", r => (r.GetString(0), r.GetString(1), r.GetInt32(2)))[0];
    public static string Script(string secret)
    {
        using var stream = typeof(CourseSelection).Assembly.GetManifestResourceStream("CourseScript.gs")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__SECRET__", secret).Replace("__VERSION__", ScriptVersion.ToString(CultureInfo.InvariantCulture));
    }

    sealed record RoundRow(string Id, string Title, int Grade, long Closes, Group[] Groups, long? PublishedAt);
    static RoundRow Round(Store store, string id) =>
        store.Query("SELECT Id,Title,Grade,Closes,Groups,PublishedAt FROM CourseRounds WHERE Id=$id", r => new RoundRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), JsonSerializer.Deserialize<Group[]>(r.GetString(4), Web)!, r.IsDBNull(5) ? null : r.GetInt64(5)), ("$id", id)).FirstOrDefault()
        ?? throw new CourseException("지워졌거나 없는 신청입니다.", 404);

    static async Task<object?> SaveRound(Store store, DeploymentReview review, HttpContext c, RoundRequest request, string? id)
    {
        var title = (request.Title ?? "").Trim();
        if (title.Length is 0 or > 80) throw new CourseException("신청 이름을 80자 안으로 적으세요.");
        if (request.Grade is < 1 or > 3) throw new CourseException("신청할 학년을 고르세요.");
        if (!DateOnly.TryParseExact(request.Closes, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) throw new CourseException("마감일을 고르세요.");
        // A round closes when that day ends in Korea.
        var closes = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
        var groups = (request.Groups ?? []).Select(g => new Group((g.Name ?? "").Trim(), g.Pick, (g.Courses ?? []).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToArray())).ToArray();
        if (groups.Length is 0 or > 20) throw new CourseException("선택군을 하나 이상 만드세요.");
        foreach (var g in groups)
        {
            if (g.Name.Length is 0 or > 40) throw new CourseException("선택군 이름을 40자 안으로 적으세요.");
            if (g.Courses.Length is 0 or > 40 || g.Courses.Any(x => x.Length > 60) || g.Courses.Distinct().Count() != g.Courses.Length) throw new CourseException($"{g.Name}: 과목을 한 줄에 하나씩, 겹치지 않게 적으세요.");
            if (g.Pick < 1 || g.Pick > g.Courses.Length) throw new CourseException($"{g.Name}: 고를 과목 수는 1부터 과목 수({g.Courses.Length})까지입니다.");
        }
        if (groups.Select(g => g.Name).Distinct().Count() != groups.Length) throw new CourseException("선택군 이름이 겹칩니다.");
        var json = JsonSerializer.Serialize(groups, Web);
        if (id is null)
        {
            id = Guid.NewGuid().ToString("N");
            store.Execute("INSERT INTO CourseRounds(Id,Title,Grade,Closes,Groups,CreatedAt) VALUES($id,$t,$g,$c,$groups,$now)", ("$id", id), ("$t", title), ("$g", request.Grade), ("$c", closes), ("$groups", json), ("$now", Now));
        }
        else
        {
            var before = Round(store, id);
            // Answers were checked against the old courses; a changed course list asks students again.
            if (before.PublishedAt is not null && (before.Grade != request.Grade || JsonSerializer.Serialize(before.Groups, Web) != json) && store.Query("SELECT 1 FROM CourseChoices WHERE RoundId=$id LIMIT 1", r => 1, ("$id", id)).Count > 0)
                throw new CourseException("이미 제출한 학생이 있어 학년·선택군·과목은 바꿀 수 없습니다. 신청 이름과 마감일만 바꾸거나, 새 신청을 만드세요.", 409);
            store.Execute("UPDATE CourseRounds SET Title=$t,Grade=$g,Closes=$c,Groups=$groups WHERE Id=$id", ("$id", id), ("$t", title), ("$g", request.Grade), ("$c", closes), ("$groups", json));
            if (before.PublishedAt is not null) await Publish(store, review, id);
        }
        Audit(store, c, "course.round.save", id);
        return new { id };
    }

    // Every student of the grade gets a link once; sending again keeps the links already handed out.
    // A roster change reaches the links already sent, so a student taken off loses their link and answer on Google too.
    static async Task Resync(Store store, DeploymentReview review, int[] grades)
    {
        var rounds = store.Query("SELECT Id,Grade FROM CourseRounds WHERE PublishedAt IS NOT NULL", r => (Id: r.GetString(0), Grade: r.GetInt32(1)));
        foreach (var round in rounds.Where(r => grades.Contains(r.Grade)))
        {
            if (store.Query("SELECT 1 FROM CourseStudents WHERE Grade=$g LIMIT 1", r => 1, ("$g", round.Grade)).Count > 0) { await Publish(store, review, round.Id); continue; }
            await Call(store, review, new { action = "remove", round = round.Id });
            store.Execute("UPDATE CourseRounds SET PublishedAt=NULL WHERE Id=$id", ("$id", round.Id));
        }
    }
    static async Task<int> Publish(Store store, DeploymentReview review, string id)
    {
        var round = Round(store, id);
        var students = store.Query("SELECT Id,Grade,ClassNo,Number,Name FROM CourseStudents WHERE Grade=$g ORDER BY ClassNo,Number", r => (Id: r.GetString(0), Row: new StudentRow(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4))), ("$g", round.Grade));
        if (students.Count == 0) throw new CourseException($"{round.Grade}학년 학생 명단이 없습니다. ‘학생 명단’에서 먼저 올리세요.");
        foreach (var s in students) store.Execute("INSERT OR IGNORE INTO CourseLinks VALUES($r,$s,$t,$k)", ("$r", id), ("$s", s.Id), ("$t", Random64(16)), ("$k", Random64(16)));
        var keys = store.Query("SELECT StudentId,Token,Key FROM CourseLinks WHERE RoundId=$r", r => (Student: r.GetString(0), Token: r.GetString(1), Key: r.GetString(2)), ("$r", id)).ToDictionary(k => k.Student);
        var sealedRows = students.Select(s =>
        {
            var link = keys[s.Id];
            var plain = JsonSerializer.Serialize(new { n = s.Row.Name, g = s.Row.Grade, c = s.Row.ClassNo, no = s.Row.Number });
            return new[] { link.Token, Seal(Bytes64(link.Key), plain) };
        }).ToArray();
        await Call(store, review, new { action = "publish", round = new { id, title = round.Title, closes = round.Closes, groups = round.Groups }, students = sealedRows });
        store.Execute("UPDATE CourseRounds SET PublishedAt=$now WHERE Id=$id", ("$now", Now), ("$id", id));
        return students.Count;
    }
    public static string Seal(byte[] key, string text)
    {
        var iv = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(text); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(iv, plain, cipher, tag);
        return Convert.ToBase64String([.. iv, .. cipher, .. tag]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Google returns every answer for the round; the server checks each one again before keeping it.
    static async Task Pull(Store store, DeploymentReview review, string id)
    {
        var round = Round(store, id);
        if (round.PublishedAt is null) throw new CourseException("아직 학생 링크를 보내지 않았습니다.");
        var answer = await Call(store, review, new { action = "results", round = id });
        var tokens = store.Query("SELECT Token,StudentId FROM CourseLinks WHERE RoundId=$r", r => (r.GetString(0), r.GetString(1)), ("$r", id)).ToDictionary(t => t.Item1, t => t.Item2);
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false);
        if (!answer.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array) throw new CourseException("구글 스크립트의 답을 읽지 못했습니다.", 502);
        foreach (var row in answers.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3 || row[0].ValueKind != JsonValueKind.String || row[1].ValueKind != JsonValueKind.String || row[2].ValueKind != JsonValueKind.Number) continue;
            var token = row[0].GetString()!; var text = row[1].GetString()!;
            if (!tokens.TryGetValue(token, out var student) || !row[2].TryGetInt64(out var at)) continue;
            string[][]? picks; try { picks = JsonSerializer.Deserialize<string[][]>(text); } catch (JsonException) { continue; }
            if (!Allowed(round.Groups, picks)) continue;
            using var save = Store.Command(db, "INSERT INTO CourseChoices VALUES($r,$s,$p,$at) ON CONFLICT(RoundId,StudentId) DO UPDATE SET Picks=excluded.Picks,SubmittedAt=excluded.SubmittedAt WHERE excluded.SubmittedAt>=SubmittedAt", tx,
                ("$r", id), ("$s", student), ("$p", JsonSerializer.Serialize(picks)), ("$at", at));
            save.ExecuteNonQuery();
        }
        using (var mark = Store.Command(db, "UPDATE CourseRounds SET PulledAt=$now WHERE Id=$id", tx, ("$now", Now), ("$id", id))) mark.ExecuteNonQuery();
        tx.Commit();
    }
    public static bool Allowed(Group[] groups, string[][]? picks) =>
        picks is not null && picks.Length == groups.Length && groups.Select((g, i) => picks[i] is { } p && p.Length == g.Pick && p.Distinct().Count() == p.Length && p.All(g.Courses.Contains)).All(ok => ok);

    static object Summary(Store store, string id)
    {
        var round = Round(store, id);
        var choices = store.Query("SELECT StudentId,Picks,SubmittedAt FROM CourseChoices WHERE RoundId=$r", r => (r.GetString(0), JsonSerializer.Deserialize<string[][]>(r.GetString(1))!, r.GetInt64(2)), ("$r", id)).ToDictionary(x => x.Item1);
        var students = store.Query("SELECT s.Id,s.ClassNo,s.Number,s.Name,l.Token IS NOT NULL FROM CourseStudents s LEFT JOIN CourseLinks l ON l.StudentId=s.Id AND l.RoundId=$r WHERE s.Grade=$g ORDER BY s.ClassNo,s.Number",
            r => (Id: r.GetString(0), ClassNo: r.GetInt32(1), Number: r.GetInt32(2), Name: r.GetString(3), Linked: r.GetBoolean(4)), ("$r", id), ("$g", round.Grade));
        var groups = round.Groups.Select((g, i) => new { name = g.Name, pick = g.Pick, courses = g.Courses.Select(course => new { name = course, count = students.Count(s => choices.TryGetValue(s.Id, out var c) && c.Item2[i].Contains(course)) }) });
        return new
        {
            round = new { id = round.Id, title = round.Title, grade = round.Grade, closes = round.Closes, groups = round.Groups, publishedAt = round.PublishedAt, pulledAt = store.Query("SELECT PulledAt FROM CourseRounds WHERE Id=$id", r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$id", id))[0] },
            groups,
            students = students.Select(s => new { id = s.Id, classNo = s.ClassNo, number = s.Number, name = s.Name, linked = s.Linked, picks = choices.TryGetValue(s.Id, out var c) ? c.Item2 : null, submittedAt = choices.TryGetValue(s.Id, out var d) ? d.Item3 : (long?)null })
        };
    }

    static async Task<JsonElement> Call(Store store, DeploymentReview review, object request, string? url = null)
    {
        var (saved, secret, _) = Settings(store); url ??= saved;
        if (url.Length == 0) throw new CourseException("먼저 ‘구글 연결’을 마치세요.", 409);
        try { review.Demand("course-selection", "script.google.com", 443); review.Demand("course-selection", "script.googleusercontent.com", 443); }
        catch (InvalidOperationException) { throw new CourseException("학교 보안 도입 검토 기록에 수강신청 연결(script.google.com, script.googleusercontent.com)이 없습니다. 기록에 추가한 뒤 다시 시도하세요.", 409); }
        var body = JsonSerializer.SerializeToNode(request, Web)!.AsObject(); body["secret"] = secret;
        string text;
        try
        {
            // Apps Script answers a POST with a redirect to the result, which the client follows as a GET.
            using var response = await Google.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "text/plain"));
            text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new CourseException($"구글이 요청을 받지 않았습니다({(int)response.StatusCode}). 배포를 다시 확인하세요.", 502);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new CourseException("구글에 연결하지 못했습니다. 학교 서버 PC의 인터넷 연결을 확인하고 다시 누르세요.", 502); }
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new CourseException("구글 스크립트가 답하지 않습니다. 배포할 때 ‘실행: 나’, ‘액세스 권한: 모든 사용자’를 골랐는지 확인하세요.", 502); }
        if (json.ValueKind != JsonValueKind.Object) throw new CourseException("구글 스크립트의 답을 읽지 못했습니다.", 502);
        if (json.TryGetProperty("error", out var error)) throw new CourseException("구글 스크립트: " + error.GetString(), 502);
        return json;
    }
}

// Reads a student list from NEIS exports or pasted rows: a header row naming 학년·반·번호·성명 (or 학번) and the rows under it.
public static class RosterReader
{
    public sealed record Result(CourseSelection.StudentRow[] Students, int Skipped, string Columns);
    static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", "");
    static int Digits(string s) { var m = Regex.Match(s ?? "", @"\d+"); return m.Success && m.Value.Length <= 3 ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0; }
    public static List<string[]> Rows(TimetableWorkbook.Sheet sheet)
    {
        if (sheet.Values.Count == 0) return [];
        var rows = sheet.Values.Keys.Max(k => k.Row); var cols = Math.Min(sheet.Values.Keys.Max(k => k.Col), 60);
        return Enumerable.Range(1, rows).Select(r => Enumerable.Range(1, cols).Select(c => sheet.Get(r, c)).ToArray()).ToList();
    }
    public static List<string[]> Text(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var tabs = lines.Any(l => l.Contains('\t'));
        return tabs ? lines.Select(l => l.Split('\t')).ToList() : Csv(Encoding.UTF8.GetBytes(text));
    }
    public static List<string[]> Csv(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(949).GetString(bytes); }
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted) { if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else if (ch == '"') quoted = false; else cell.Append(ch); continue; }
            if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\n' or '\r') { if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; row.Add(cell.ToString()); cell.Clear(); rows.Add([.. row]); row.Clear(); }
            else if (ch != '﻿') cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add([.. row]); }
        return rows;
    }
    public static Result Read(IEnumerable<List<string[]>> sheets)
    {
        foreach (var rows in sheets)
        {
            for (var h = 0; h < Math.Min(rows.Count, 40); h++)
            {
                var head = rows[h].Select(Clean).ToArray();
                int Find(params string[] names) => Array.FindIndex(head, c => names.Contains(c));
                var name = Find("성명", "이름", "학생명", "학생성명", "학생이름"); if (name < 0) continue;
                var grade = Find("학년"); var room = Find("반", "학급"); var number = Find("번호", "번", "출석번호"); var id = Find("학번");
                // Some NEIS lists print the grade only in the title above the table.
                var titleGrade = Enumerable.Range(0, h).SelectMany(r => rows[r]).Select(c => Regex.Match(c ?? "", @"([1-3])\s*학년")).FirstOrDefault(m => m.Success) is { } t ? int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                if (!(room >= 0 && number >= 0 && (grade >= 0 || titleGrade > 0)) && id < 0) continue;
                var found = new List<CourseSelection.StudentRow>(); var skipped = 0;
                foreach (var cells in rows.Skip(h + 1))
                {
                    string At(int i) => i >= 0 && i < cells.Length ? (cells[i] ?? "").Trim() : "";
                    if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;
                    var studentName = At(name);
                    int g, c, n;
                    if (room >= 0 && number >= 0 && (grade >= 0 || titleGrade > 0)) { g = grade >= 0 ? Digits(At(grade)) : titleGrade; c = Digits(At(room)); n = Digits(At(number)); }
                    else { var digits = Regex.Replace(At(id), @"\D", ""); if (digits.Length != 5) { skipped++; continue; } g = digits[0] - '0'; c = int.Parse(digits[1..3], CultureInfo.InvariantCulture); n = int.Parse(digits[3..], CultureInfo.InvariantCulture); }
                    var row = new CourseSelection.StudentRow(g, c, n, studentName);
                    if (g is < 1 or > 3 || c is < 1 or > 30 || n is < 1 or > 99 || studentName.Length is 0 or > 40) { skipped++; continue; }
                    found.Add(row);
                }
                if (found.Count == 0) continue;
                var twice = found.GroupBy(r => (r.Grade, r.ClassNo, r.Number)).FirstOrDefault(g => g.Count() > 1);
                if (twice is not null) throw new FilePolicyException($"{twice.Key.Grade}학년 {twice.Key.ClassNo}반 {twice.Key.Number}번이 두 번 있습니다. 명단을 확인하세요.");
                var columns = id >= 0 && !(room >= 0 && number >= 0) ? "학번 · 성명" : (grade >= 0 ? "학년 · " : "") + "반 · 번호 · 성명";
                return new([.. found.OrderBy(r => r.Grade).ThenBy(r => r.ClassNo).ThenBy(r => r.Number)], skipped, columns);
            }
        }
        throw new FilePolicyException("명단의 머리글(학년·반·번호·성명, 또는 학번·성명)을 찾지 못했습니다. 나이스에서 받은 명단을 머리글 줄까지 함께 올려 주세요.");
    }
}

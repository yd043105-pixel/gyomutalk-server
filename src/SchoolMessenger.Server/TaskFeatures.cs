using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

public record TodoEdit(string? Title, string? DueDate, string? Status, int Revision);
public sealed record TaskCandidate(string Title, string Evidence, string? DueDate);
public static class TaskFeatures
{
    static string UserId(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    const string Verbs = "제출|작성|회신|준비|신청|참석|송부|입력|기재|등록|업로드|확인|검토|수합|취합|점검|참여|보내|열어|올려";
    // A request is a known action with a polite ending, or anything asked for by a date ("금요일까지 알려 주세요").
    static readonly Regex Request = new($@"({Verbs}).*(주세요|바랍니다|부탁|하세요|해요|하기|요청)|까지.*(주세요|바랍니다|부탁)", RegexOptions.NonBacktracking);
    static readonly Regex Ended = new(@"취소|철회|제출\s*완료|제출했|제출하셨|제출하지\s*마|참석하지\s*마|참고\s*(하시기|바랍|해\s*주)|양해", RegexOptions.NonBacktracking);
    static readonly Regex Dates = new(@"(?:(\d{4})\s*년\s*)?(\d{1,2})\s*월\s*(\d{1,2})\s*일|\d{4}-\d{2}-\d{2}|오늘|내일|모레|(?:다음|이번)\s*주(?:\s*[월화수목금토일]요일)?|[월화수목금토일]요일", RegexOptions.NonBacktracking);
    // The date goes together with its weekday, time and particle, wherever it sits ("11월 7일(금) 오후 3시까지 제출" -> "제출").
    static readonly Regex DateWithSuffix = new($@"(?:{Dates})(?:\s*\([월화수목금토일]\))?(?:\s*(?:오전|오후)?\s*\d{{1,2}}\s*시(?:\s*\d{{1,2}}\s*분)?)?\s*(?:까지|에)?\s*", RegexOptions.NonBacktracking);
    // "담임 선생님께서는 …" names who should act; the task already belongs to that person.
    static readonly Regex Addressee = new(@"^.{0,30}?(께서는|께서|께선|에서는|선생님들?은)\s+", RegexOptions.NonBacktracking);
    static readonly Regex ObjectAction = new($@"(을|를)\s*({Verbs})", RegexOptions.NonBacktracking);
    static readonly Regex RequestEnding = new(@"(해\s*주세요|해\s*주시기\s*바랍니다|하시기\s*바랍니다|하세요|\s*주세요|\s*주시기\s*바랍니다|\s*바랍니다|\s*부탁드립니다)[.\s]*$", RegexOptions.NonBacktracking);
    static readonly Dictionary<string, string> Plain = new() { ["보내"] = "보내기", ["열어"] = "열기", ["올려"] = "올리기", ["알려"] = "알리기", ["가져와"] = "가져오기", ["챙겨"] = "챙기기" };
    static readonly Regex PlainVerb = new(@"(보내|열어|올려|알려|가져와|챙겨)$", RegexOptions.NonBacktracking);
    static readonly Regex Sentences = new(@"(?<=[.!?;])\s+|\n");
    public static DateOnly KoreanDate(long instant) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(instant).ToOffset(TimeSpan.FromHours(9)).DateTime);
    static DateOnly? Parse(Match m, DateOnly today)
    {
        var text = m.Value;
        if (text == "오늘") return today;
        if (text is "내일" or "모레") return today.AddDays(text == "내일" ? 1 : 2);
        if (text.Contains('주'))
        {
            // School weeks run Monday to Friday, so "다음 주까지" means next week's Friday.
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var weekday = text.EndsWith("요일") ? "월화수목금토일".IndexOf(text[^3]) : 4;
            return monday.AddDays((text.StartsWith("다음") ? 7 : 0) + weekday);
        }
        if (text.EndsWith("요일")) { var day = "일월화수목금토".IndexOf(text[0]); return today.AddDays((day - (int)today.DayOfWeek + 7) % 7); }
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso;
        if (int.TryParse(m.Groups[2].Value, out var month) && int.TryParse(m.Groups[3].Value, out var date))
        {
            var year = int.TryParse(m.Groups[1].Value, out var parsedYear) ? parsedYear : today.Year;
            if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && date >= 1 && date <= DateTime.DaysInMonth(year, month)) return new DateOnly(year, month, date);
        }
        return null;
    }
    public static List<TaskCandidate> Candidates(string body, long sentAt)
    {
        var today = KoreanDate(sentAt);
        var result = new List<TaskCandidate>();
        DateOnly? announced = null;
        // ponytail: explicit request rules; a locally hosted language model can replace this if school hardware and policy permit.
        foreach (var line in Sentences.Split(body[..Math.Min(20_000, body.Length)]))
        {
            var sentence = line.Trim();
            if (sentence.Length == 0) continue;
            var matches = Dates.Matches(sentence);
            var due = matches.Count == 1 ? Parse(matches[0], today) : null;
            // "내일 오후 3시 회의가 있습니다. 자료를 준비해 주세요." A request without a date takes the upcoming date announced before it.
            if (matches.Count == 0) due = announced;
            else if (due >= today) announced = due;
            if (!Request.IsMatch(sentence) || Ended.IsMatch(sentence)) continue;
            var title = RequestEnding.Replace(ObjectAction.Replace(DateWithSuffix.Replace(Addressee.Replace(sentence, ""), ""), " $2"), "").Trim(' ', '.', ',');
            title = PlainVerb.Replace(title, v => Plain[v.Value]);
            if (title.Length == 0) title = sentence;
            if (title.Length > 120) title = title[..120];
            result.Add(new(title, sentence[..Math.Min(sentence.Length, 2000)], due?.ToString("yyyy-MM-dd")));
            if (result.Count == 10) break;
        }
        return result.DistinctBy(c => c.Evidence).ToList();
    }
    public static void Extract(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> owners, string source, string body, long now)
    {
        var candidates = Candidates(body, now);
        foreach (var owner in owners)
        foreach (var candidate in candidates)
        {
            using var insert = Store.Command(connection, "INSERT OR IGNORE INTO Todos(Id,OwnerId,SourceId,Evidence,Title,DueDate,Status,CreatedAt,UpdatedAt) VALUES($id,$owner,$source,$evidence,$title,$due,'suggested',$now,$now)", transaction,
                ("$id", Guid.NewGuid().ToString("N")), ("$owner", owner), ("$source", source), ("$evidence", candidate.Evidence), ("$title", candidate.Title), ("$due", candidate.DueDate), ("$now", now)); insert.ExecuteNonQuery();
        }
    }
    public static void Map(RouteGroupBuilder api, Store store)
    {
        api.MapGet("/todos", (HttpContext c, int? offset, string? sourceId) => store.Query("""
            SELECT Id,Title,DueDate,Status,Revision,SourceId,Evidence,UpdatedAt FROM Todos t WHERE OwnerId=$user
            AND ($source IS NULL OR SourceId=$source)
            AND (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=t.SourceId AND r.UserId=$user))
            ORDER BY CASE Status WHEN 'open' THEN 0 WHEN 'suggested' THEN 1 WHEN 'done' THEN 2 ELSE 3 END,
              DueDate IS NULL,DueDate,UpdatedAt DESC,Id LIMIT 100 OFFSET $offset
            """, r => new { id = r.GetString(0), title = r.GetString(1), dueDate = r.IsDBNull(2) ? null : r.GetString(2), status = r.GetString(3), revision = r.GetInt32(4), sourceId = r.IsDBNull(5) ? null : r.GetString(5), evidence = r.GetString(6), updatedAt = r.GetInt64(7) }, ("$user", UserId(c)), ("$source", sourceId), ("$offset", Math.Clamp(offset ?? 0, 0, 1_000_000))));
        api.MapPost("/todos", IResult (TodoEdit edit, HttpContext c) =>
        {
            if (!Valid(edit) || edit.Status != "open") return Bad();
            var id = Guid.NewGuid().ToString("N");
            store.Execute("INSERT INTO Todos(Id,OwnerId,Evidence,Title,DueDate,Status,CreatedAt,UpdatedAt) VALUES($id,$user,'',$title,$due,'open',$now,$now)", ("$id", id), ("$user", UserId(c)), ("$title", edit.Title!.Trim()), ("$due", edit.DueDate), ("$now", Maintenance.Now));
            return Results.Ok(new { id });
        });
        api.MapPatch("/todos/{id}", IResult (string id, TodoEdit edit, HttpContext c) =>
        {
            if (!Valid(edit) || edit.Revision < 1) return Bad();
            var user = UserId(c);
            var changed = store.Execute("""
                UPDATE Todos SET Title=$title,DueDate=$due,Status=$status,Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$id AND OwnerId=$user AND Revision=$revision AND
                (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=Todos.SourceId AND r.UserId=$user))
                """, ("$title", edit.Title!.Trim()), ("$due", edit.DueDate), ("$status", edit.Status), ("$now", Maintenance.Now), ("$id", id), ("$user", user), ("$revision", edit.Revision));
            if (changed > 0) return Results.Ok();
            return store.Query("SELECT 1 FROM Todos t WHERE Id=$id AND OwnerId=$user AND (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=t.SourceId AND r.UserId=$user))", r => r.GetInt32(0), ("$id", id), ("$user", user)).Count == 0 ? Results.NotFound() : Results.Json(new { error = "다른 화면에서 변경했습니다. 새로고침 후 다시 수정하세요." }, statusCode: 409);
        });
    }
    static bool Valid(TodoEdit e) => !string.IsNullOrWhiteSpace(e.Title) && e.Title.Length <= 200 && e.Status is "suggested" or "open" or "done" or "dismissed" &&
        (e.DueDate is null || DateOnly.TryParseExact(e.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    static IResult Bad() => Results.Json(new { error = "제목은 200자 이내, 기한은 YYYY-MM-DD 형식입니다." }, statusCode: 400);
}

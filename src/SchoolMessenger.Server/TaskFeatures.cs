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
    static readonly Regex Request = new(@"(제출|작성|회신|준비|신청|참석).*(주세요|바랍니다|부탁|하세요|해요|하기|요청)", RegexOptions.NonBacktracking);
    static readonly Regex Ended = new(@"취소|철회|제출\s*완료|제출했|제출하셨|제출하지\s*마|참석하지\s*마", RegexOptions.NonBacktracking);
    static readonly Regex Dates = new(@"(?:(\d{4})\s*년\s*)?(\d{1,2})\s*월\s*(\d{1,2})\s*일|\d{4}-\d{2}-\d{2}|오늘|내일|모레|[월화수목금토일]요일", RegexOptions.NonBacktracking);
    static readonly Regex DateSuffix = new(@"^\s*(까지|에)\s*", RegexOptions.NonBacktracking);
    static readonly Regex ObjectAction = new(@"(을|를)\s*(제출|작성|회신|준비|신청|참석)", RegexOptions.NonBacktracking);
    static readonly Regex RequestEnding = new(@"(해\s*주세요|해\s*주시기\s*바랍니다|하시기\s*바랍니다|하세요|\s*바랍니다|\s*부탁드립니다)[.\s]*$", RegexOptions.NonBacktracking);
    public static DateOnly KoreanDate(long instant) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(instant).ToOffset(TimeSpan.FromHours(9)).DateTime);
    public static List<TaskCandidate> Candidates(string body, long sentAt)
    {
        var today = KoreanDate(sentAt);
        var result = new List<TaskCandidate>();
        // ponytail: explicit request rules; a locally hosted language model can replace this if school hardware and policy permit.
        foreach (var line in body[..Math.Min(20_000, body.Length)].Split(['\n', '!', '?', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var sentence = line.Trim();
            if (!Request.IsMatch(sentence) || Ended.IsMatch(sentence)) continue;
            var matches = Dates.Matches(sentence);
            string? due = null;
            if (matches.Count == 1)
            {
                var m = matches[0]; var text = m.Value;
                if (text == "오늘") due = today.ToString("yyyy-MM-dd");
                else if (text == "내일" || text == "모레") due = today.AddDays(text == "내일" ? 1 : 2).ToString("yyyy-MM-dd");
                else if (text.EndsWith("요일"))
                { var day = "일월화수목금토".IndexOf(text[0]); due = today.AddDays((day - (int)today.DayOfWeek + 7) % 7).ToString("yyyy-MM-dd"); }
                else if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) due = iso.ToString("yyyy-MM-dd");
                else if (int.TryParse(m.Groups[2].Value, out var month) && int.TryParse(m.Groups[3].Value, out var day))
                {
                    var year = int.TryParse(m.Groups[1].Value, out var parsedYear) ? parsedYear : today.Year;
                    if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)) due = new DateOnly(year, month, day).ToString("yyyy-MM-dd");
                }
            }
            var title = RequestEnding.Replace(ObjectAction.Replace(DateSuffix.Replace(Dates.Replace(sentence, ""), ""), " $2"), "").Trim(' ', '.');
            if (title.Length == 0) title = sentence;
            if (title.Length > 120) title = title[..120];
            result.Add(new(title, sentence[..Math.Min(sentence.Length, 2000)], due));
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

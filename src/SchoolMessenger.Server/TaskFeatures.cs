using System.Globalization;
using System.Security.Claims;

namespace SchoolMessenger.Server;

public record TodoEdit(string? Title, string? DueDate, string? Status, int Revision, string? SourceId = null);
public static class TaskFeatures
{
    static string UserId(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public static DateOnly KoreanDate(long instant) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(instant).ToOffset(TimeSpan.FromHours(9)).DateTime);
    public static void Map(RouteGroupBuilder api, Store store)
    {
        api.MapGet("/todos", (HttpContext c, int? offset, string? sourceId) => store.Query("""
            SELECT Id,Title,DueDate,Status,Revision,SourceId,Evidence,UpdatedAt FROM Todos t WHERE OwnerId=$user
            AND ($source IS NULL OR SourceId=$source)
            AND (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=t.SourceId AND r.UserId=$user))
            ORDER BY CASE Status WHEN 'open' THEN 0 WHEN 'suggested' THEN 1 WHEN 'done' THEN 2 ELSE 3 END,
              DueDate IS NULL,DueDate,UpdatedAt DESC,Id LIMIT 100 OFFSET $offset
            """, r => new { id = r.GetString(0), title = r.GetString(1), dueDate = r.IsDBNull(2) ? null : r.GetString(2), status = r.GetString(3), revision = r.GetInt32(4), sourceId = r.IsDBNull(5) ? null : r.GetString(5), evidence = "", updatedAt = r.GetInt64(7) }, ("$user", UserId(c)), ("$source", sourceId), ("$offset", Math.Clamp(offset ?? 0, 0, 1_000_000))));
        api.MapPost("/todos", IResult (TodoEdit edit, HttpContext c) =>
        {
            if (!Valid(edit) || edit.Status != "open") return Bad();
            if (edit.SourceId is not null && store.Query("SELECT 1 FROM Recipients WHERE MessageId=$source AND UserId=$user", r => r.GetInt32(0), ("$source", edit.SourceId), ("$user", UserId(c))).Count == 0) return Results.NotFound();
            var id = Guid.NewGuid().ToString("N");
            store.Execute("INSERT INTO Todos(Id,OwnerId,SourceId,Evidence,Title,DueDate,Status,CreatedAt,UpdatedAt) VALUES($id,$user,$source,$id,$title,$due,'open',$now,$now)", ("$id", id), ("$user", UserId(c)), ("$source", edit.SourceId), ("$title", edit.Title!.Trim()), ("$due", edit.DueDate), ("$now", Maintenance.Now));
            return Results.Ok(new { id });
        });
        api.MapPatch("/todos/{id}", IResult (string id, TodoEdit edit, HttpContext c) =>
        {
            if (!Valid(edit) || edit.Revision < 1) return Bad();
            var user = UserId(c);
            // A task set aside leaves the server rather than staying behind as text nobody needs.
            if (edit.Status == "dismissed" && store.Execute("DELETE FROM Todos WHERE Id=$id AND OwnerId=$user AND Revision=$revision", ("$id", id), ("$user", user), ("$revision", edit.Revision)) > 0) return Results.Ok();
            var changed = store.Execute("""
                UPDATE Todos SET Title=$title,DueDate=$due,Status=$status,Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$id AND OwnerId=$user AND Revision=$revision AND
                (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=Todos.SourceId AND r.UserId=$user))
                """, ("$title", edit.Title!.Trim()), ("$due", edit.DueDate), ("$status", edit.Status), ("$now", Maintenance.Now), ("$id", id), ("$user", user), ("$revision", edit.Revision));
            if (changed > 0) return Results.Ok();
            return store.Query("SELECT 1 FROM Todos t WHERE Id=$id AND OwnerId=$user AND (SourceId IS NULL OR EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=t.SourceId AND r.UserId=$user))", r => r.GetInt32(0), ("$id", id), ("$user", user)).Count == 0 ? Results.NotFound() : Results.Json(new { error = "다른 화면에서 변경했습니다. 새로고침 후 다시 수정하세요." }, statusCode: 409);
        });
    }
    static bool Valid(TodoEdit e) => !string.IsNullOrWhiteSpace(e.Title) && e.Title.Length <= 200 && e.Status is "open" or "done" or "dismissed" &&
        (e.DueDate is null || DateOnly.TryParseExact(e.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    static IResult Bad() => Results.Json(new { error = "제목은 200자 이내, 기한은 YYYY-MM-DD 형식입니다." }, statusCode: 400);
}

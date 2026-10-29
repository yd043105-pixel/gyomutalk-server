using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace SchoolMessenger.Server;

public record ChatSend(string? ClientId, string? Body, string[]? AttachmentIds);
public record ChatCreate(string? Name, string[]? MemberIds);
public record ChatRead(long Sequence);
public record SurveyQuestion(string? Text, string? Kind, bool Required, string[]? Options);
public record SurveyCreate(string? Title, string? Description, long Deadline, SurveyQuestion[]? Questions, string[]? TargetIds, bool All);
public record SurveyAnswer(string[]? Answers);

public static class CollaborationFeatures
{
    static string UserId(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static IResult Error(string text, int status = 400) => Results.Json(new { error = text }, statusCode: status);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void Map(RouteGroupBuilder api, Store store)
    {
        bool Member(string room, string user) => store.Query("SELECT 1 FROM ChatMembers WHERE RoomId=$room AND UserId=$user", r => r.GetInt32(0), ("$room", room), ("$user", user)).Count > 0;
        api.MapPost("/chats", async Task<IResult> (ChatCreate request, HttpContext c, IHubContext<MessageHub> hub) =>
        {
            if (request.MemberIds is null || request.MemberIds.Length > 1000 || (request.Name?.Length ?? 0) > 100) return Error("대화방 이름은 100자 이내입니다. 참여자를 선택하세요.");
            var ids = request.MemberIds.Append(UserId(c)).Distinct().Order().ToArray();
            if (ids.Length < 2 || ids.Any(id => store.GetUser(id) is not { Active: true })) return Error("활성 교직원을 한 명 이상 선택하세요.");
            var direct = ids.Length == 2 && string.IsNullOrWhiteSpace(request.Name) ? string.Join(":", ids) : null;
            var name = string.IsNullOrWhiteSpace(request.Name) ? "교직원 대화" : request.Name.Trim();
            string id;
            using (var connection = store.Open())
            using (var transaction = connection.BeginTransaction())
            {
                using var find = Store.Command(connection, "SELECT Id FROM ChatRooms WHERE DirectKey=$key", transaction, ("$key", direct));
                if (find.ExecuteScalar() is string existing) return Results.Ok(new { id = existing });
                id = Guid.NewGuid().ToString("N");
                using var insert = Store.Command(connection, "INSERT INTO ChatRooms VALUES($id,$name,$key,$now)", transaction, ("$id", id), ("$name", name), ("$key", direct), ("$now", Maintenance.Now)); insert.ExecuteNonQuery();
                foreach (var user in ids)
                { using var member = Store.Command(connection, "INSERT INTO ChatMembers(RoomId,UserId) VALUES($room,$user)", transaction, ("$room", id), ("$user", user)); member.ExecuteNonQuery(); }
                transaction.Commit();
            }
            await Notify(hub, ids, "ChatChanged", id);
            return Results.Ok(new { id });
        });
        api.MapGet("/chats", (HttpContext c) => store.Query("""
            SELECT r.Id,r.Name,r.DirectKey,
              (SELECT COUNT(*) FROM ChatEntries ce JOIN Messages m ON m.Id=ce.MessageId WHERE ce.RoomId=r.Id AND ce.Sequence>cm.LastRead AND m.SenderId<>$user),
              COALESCE((SELECT MAX(m.CreatedAt) FROM ChatEntries ce JOIN Messages m ON m.Id=ce.MessageId WHERE ce.RoomId=r.Id),r.CreatedAt)
            FROM ChatRooms r JOIN ChatMembers cm ON cm.RoomId=r.Id WHERE cm.UserId=$user ORDER BY 5 DESC,r.Id
            """, r => new
            {
                id = r.GetString(0), name = r.GetString(1), direct = !r.IsDBNull(2), unread = r.GetInt32(3),
                members = store.Query("SELECT u.Id,u.Name,u.Department FROM ChatMembers cm JOIN Users u ON u.Id=cm.UserId WHERE cm.RoomId=$room ORDER BY u.Name", u => new { id = u.GetString(0), name = u.GetString(1), department = u.GetString(2) }, ("$room", r.GetString(0)))
            }, ("$user", UserId(c))));
        api.MapGet("/chats/{id}/messages", IResult (string id, long? before, HttpContext c) =>
        {
            if (!Member(id, UserId(c))) return Results.NotFound();
            var rows = store.Query("""
                SELECT ce.Sequence,m.Id,m.SenderId,u.Name,m.Body,m.CreatedAt FROM ChatEntries ce JOIN Messages m ON m.Id=ce.MessageId JOIN Users u ON u.Id=m.SenderId
                WHERE ce.RoomId=$room AND ce.Sequence<$before ORDER BY ce.Sequence DESC LIMIT 100
                """, r => new
                {
                    sequence = r.GetInt64(0), id = r.GetString(1), senderId = r.GetString(2), senderName = r.GetString(3), body = r.GetString(4), createdAt = r.GetInt64(5),
                    attachments = store.Query("SELECT Id,Name,Size,ExpiresAt,DeletedAt FROM Attachments WHERE MessageId=$message", a => new { id = a.GetString(0), name = a.GetString(1), size = a.GetInt64(2), expiresAt = a.GetInt64(3), expired = a.GetInt64(3) <= Maintenance.Now || !a.IsDBNull(4) }, ("$message", r.GetString(1)))
                }, ("$room", id), ("$before", before ?? long.MaxValue));
            return Results.Ok(rows.AsEnumerable().Reverse());
        });
        api.MapPost("/chats/{id}/read", IResult (string id, ChatRead request, HttpContext c) =>
        {
            if (!Member(id, UserId(c))) return Results.NotFound();
            if (request.Sequence < 0 || store.Query("SELECT 1 FROM ChatEntries WHERE RoomId=$room AND Sequence=$sequence", r => r.GetInt32(0), ("$room", id), ("$sequence", request.Sequence)).Count == 0) return Error("확인할 대화 기록이 없습니다.");
            store.Execute("UPDATE ChatMembers SET LastRead=MAX(LastRead,$sequence) WHERE RoomId=$room AND UserId=$user", ("$room", id), ("$user", UserId(c)), ("$sequence", request.Sequence));
            return Results.Ok();
        });
        api.MapPost("/surveys", async Task<IResult> (SurveyCreate request, HttpContext c, IHubContext<MessageHub> hub) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || (request.Description?.Length ?? 0) > 5000 || request.Deadline <= Maintenance.Now || request.Deadline > Maintenance.Now + 366L * 86400000 ||
                request.Questions is not { Length: >= 1 and <= 20 } || request.TargetIds is null || request.TargetIds.Length > 1000) return Error("제목·문항·대상·마감일을 확인하세요. 문항은 최대 20개, 마감은 1년 이내입니다.");
            foreach (var question in request.Questions)
                if (question is null || string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > 500 || question.Kind is not ("choice" or "text") ||
                    (question.Kind == "text" && question.Options is { Length: > 0 }) ||
                    (question.Kind == "choice" && (question.Options is not { Length: >= 2 and <= 20 } || question.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 200) || question.Options.Distinct().Count() != question.Options.Length)))
                    return Error("객관식은 중복 없는 선택지 2~20개가 필요합니다.");
            var owner = store.GetUser(UserId(c))!;
            var active = store.Users().Where(u => u.Active).Select(u => u.Id).ToArray();
            var targets = (request.All ? active : request.TargetIds.Distinct().ToArray());
            if (targets.Length == 0 || targets.Except(active).Any()) return Error("활성 교직원을 대상으로 선택하세요.");
            if ((request.All || (active.Length > 1 && targets.Length == active.Length)) && !owner.IsAdmin && !owner.CanBroadcast) return Error("전체 발송 권한이 없습니다.", 403);
            var id = Guid.NewGuid().ToString("N");
            using (var connection = store.Open())
            using (var transaction = connection.BeginTransaction())
            {
                using var insert = Store.Command(connection, "INSERT INTO Surveys(Id,OwnerId,Title,Description,Questions,Deadline,CreatedAt) VALUES($id,$owner,$title,$description,$questions,$deadline,$now)", transaction,
                    ("$id", id), ("$owner", owner.Id), ("$title", request.Title.Trim()), ("$description", request.Description ?? ""), ("$questions", JsonSerializer.Serialize(request.Questions, Json)), ("$deadline", request.Deadline), ("$now", Maintenance.Now)); insert.ExecuteNonQuery();
                foreach (var user in targets) { using var target = Store.Command(connection, "INSERT INTO SurveyTargets VALUES($id,$user)", transaction, ("$id", id), ("$user", user)); target.ExecuteNonQuery(); }
                transaction.Commit();
            }
            await Notify(hub, targets, "SurveyChanged", id); return Results.Ok(new { id });
        });
        api.MapGet("/surveys", (HttpContext c) => store.Query("""
            SELECT s.Id,s.OwnerId,s.Title,u.Name,s.Deadline,s.Closed,
              (SELECT COUNT(*) FROM SurveyTargets WHERE SurveyId=s.Id), (SELECT COUNT(*) FROM SurveyResponses WHERE SurveyId=s.Id),
              EXISTS(SELECT 1 FROM SurveyResponses WHERE SurveyId=s.Id AND UserId=$user)
            FROM Surveys s JOIN Users u ON u.Id=s.OwnerId WHERE s.OwnerId=$user OR EXISTS(SELECT 1 FROM SurveyTargets WHERE SurveyId=s.Id AND UserId=$user) ORDER BY s.CreatedAt DESC,s.Id
            """, r => new { id = r.GetString(0), ownerId = r.GetString(1), title = r.GetString(2), ownerName = r.GetString(3), deadline = r.GetInt64(4), closed = r.GetBoolean(5) || r.GetInt64(4) <= Maintenance.Now, targetCount = r.GetInt32(6), responseCount = r.GetInt32(7), answered = r.GetBoolean(8) }, ("$user", UserId(c))));
        api.MapGet("/surveys/{id}", IResult (string id, HttpContext c) =>
        {
            var survey = GetSurvey(store, id, UserId(c));
            if (survey is null) return Results.NotFound();
            var answer = store.Query("SELECT Answers FROM SurveyResponses WHERE SurveyId=$id AND UserId=$user", r => JsonSerializer.Deserialize<string[]>(r.GetString(0), Json)!, ("$id", id), ("$user", UserId(c))).FirstOrDefault();
            var canAnswer = store.Query("SELECT 1 FROM SurveyTargets WHERE SurveyId=$id AND UserId=$user", r => r.GetInt32(0), ("$id", id), ("$user", UserId(c))).Count > 0;
            return Results.Ok(new { survey.Id, survey.OwnerId, survey.Title, survey.Description, survey.Questions, survey.Deadline, closed = survey.Closed || survey.Deadline <= Maintenance.Now, canAnswer, answers = answer });
        });
        api.MapPost("/surveys/{id}/answers", async Task<IResult> (string id, SurveyAnswer request, HttpContext c, IHubContext<MessageHub> hub) =>
        {
            string owner;
            using (var connection = store.Open())
            using (var transaction = connection.BeginTransaction())
            {
                // A write transaction serializes response updates against the owner's close action.
                var survey = GetSurvey(store, id, UserId(c));
                if (survey is null || store.Query("SELECT 1 FROM SurveyTargets WHERE SurveyId=$id AND UserId=$user", r => r.GetInt32(0), ("$id", id), ("$user", UserId(c))).Count == 0) return Results.NotFound();
                if (survey.Closed || survey.Deadline <= Maintenance.Now) return Error("종료된 설문입니다.", 409);
                if (request.Answers is null || request.Answers.Length != survey.Questions.Length) return Error("모든 문항을 확인하세요.");
                for (var i = 0; i < request.Answers.Length; i++)
                {
                    var q = survey.Questions[i]; var answer = request.Answers[i];
                    if (answer is null || answer.Length > 2000 || (q.Required && string.IsNullOrWhiteSpace(answer)) || (q.Kind == "choice" && answer.Length > 0 && !q.Options!.Contains(answer))) return Error("필수 문항과 선택지를 확인하세요. 주관식은 2,000자 이내입니다.");
                }
                using var save = Store.Command(connection, "INSERT INTO SurveyResponses VALUES($id,$user,$answers,$now) ON CONFLICT(SurveyId,UserId) DO UPDATE SET Answers=excluded.Answers,UpdatedAt=excluded.UpdatedAt", transaction,
                    ("$id", id), ("$user", UserId(c)), ("$answers", JsonSerializer.Serialize(request.Answers, Json)), ("$now", Maintenance.Now)); save.ExecuteNonQuery(); owner = survey.OwnerId; transaction.Commit();
            }
            await Notify(hub, new[] { owner, UserId(c) }, "SurveyChanged", id); return Results.Ok();
        });
        api.MapPost("/surveys/{id}/close", IResult (string id, HttpContext c) => store.Execute("UPDATE Surveys SET Closed=1 WHERE Id=$id AND OwnerId=$user", ("$id", id), ("$user", UserId(c))) > 0 ? Results.Ok() : Results.NotFound());
        api.MapGet("/surveys/{id}/results", IResult (string id, HttpContext c) =>
        {
            var survey = GetSurvey(store, id, UserId(c));
            if (survey is null || survey.OwnerId != UserId(c)) return Results.NotFound();
            var responses = store.Query("SELECT u.Id,u.Name,u.Department,r.Answers,r.UpdatedAt FROM SurveyResponses r JOIN Users u ON u.Id=r.UserId WHERE r.SurveyId=$id ORDER BY u.Name", r => new { id = r.GetString(0), name = r.GetString(1), department = r.GetString(2), answers = JsonSerializer.Deserialize<string[]>(r.GetString(3), Json), updatedAt = r.GetInt64(4) }, ("$id", id));
            return Results.Ok(new { survey.Questions, responses });
        });
    }
    sealed record Survey(string Id, string OwnerId, string Title, string Description, SurveyQuestion[] Questions, long Deadline, bool Closed);
    static Survey? GetSurvey(Store store, string id, string user) => store.Query("SELECT Id,OwnerId,Title,Description,Questions,Deadline,Closed FROM Surveys s WHERE Id=$id AND (OwnerId=$user OR EXISTS(SELECT 1 FROM SurveyTargets WHERE SurveyId=s.Id AND UserId=$user))",
        r => new Survey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), JsonSerializer.Deserialize<SurveyQuestion[]>(r.GetString(4), Json)!, r.GetInt64(5), r.GetBoolean(6)), ("$id", id), ("$user", user)).FirstOrDefault();
    static async Task Notify(IHubContext<MessageHub> hub, IEnumerable<string> users, string name, string id)
    { try { await hub.Clients.Groups(users.Distinct()).SendAsync(name, id); } catch { /* Durable rows are available after reconnection. */ } }
}

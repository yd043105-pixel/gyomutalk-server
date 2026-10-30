using SchoolMessenger.Shared;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace SchoolMessenger.Server;

public record SubmitFiles(string? ClientId, string[]? AttachmentIds);
public record Exemption(bool Exempt);
public record WithdrawSubmission(string? SubmissionId);
public sealed record SubmissionRequestInfo(string Id, string OwnerId, string Title, string Body, long Deadline, bool Closed);
public sealed record SubmissionFileInfo(string Id, string Name, long Size, long ExpiresAt, bool Expired);
public sealed record SubmissionTargetInfo(string Id, string Name, string Department, bool Exempt, string? SubmissionId, long? SubmittedAt, bool Late, List<SubmissionFileInfo> Files);
public static class SubmissionFeatures
{
    static string UserId(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static IResult Error(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static SubmissionRequestInfo? Find(Store store, string id, string user) => store.Query("""
        SELECT r.MessageId,m.SenderId,m.Title,m.Body,r.Deadline,r.Closed FROM SubmissionRequests r JOIN Messages m ON m.Id=r.MessageId
        WHERE r.MessageId=$id AND NOT EXISTS(SELECT 1 FROM MessageRecalls WHERE MessageId=m.Id) AND (m.SenderId=$user OR EXISTS(SELECT 1 FROM SubmissionTargets t WHERE t.RequestId=r.MessageId AND t.UserId=$user))
        """, r => new SubmissionRequestInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetBoolean(5)), ("$id", id), ("$user", user)).FirstOrDefault();
    public static List<SubmissionTargetInfo> Targets(Store store, SubmissionRequestInfo request, string user)
    {
        return store.Query("""
            SELECT u.Id,u.Name,u.Department,t.Exempt,
            (SELECT s.Id FROM Submissions s WHERE s.RequestId=t.RequestId AND s.UserId=t.UserId AND NOT EXISTS(SELECT 1 FROM SubmissionWithdrawals w WHERE w.SubmissionId=s.Id) ORDER BY s.CreatedAt DESC,s.rowid DESC LIMIT 1)
            FROM SubmissionTargets t JOIN Users u ON u.Id=t.UserId WHERE t.RequestId=$id AND ($owner=$user OR t.UserId=$user) ORDER BY u.Name,u.Id
            """, r =>
            {
                var submission = r.IsDBNull(4) ? null : r.GetString(4);
                var at = submission is null ? (long?)null : store.Query("SELECT CreatedAt FROM Submissions WHERE Id=$id", s => s.GetInt64(0), ("$id", submission))[0];
                var files = submission is null ? [] : store.Query("SELECT a.Id,a.Name,a.Size,a.ExpiresAt,a.DeletedAt FROM SubmissionFiles sf JOIN Attachments a ON a.Id=sf.AttachmentId WHERE sf.SubmissionId=$id", f => new SubmissionFileInfo(f.GetString(0), f.GetString(1), f.GetInt64(2), f.GetInt64(3), f.GetInt64(3) <= Maintenance.Now || !f.IsDBNull(4)), ("$id", submission));
                return new SubmissionTargetInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), submission, at, at > request.Deadline, files);
            }, ("$id", request.Id), ("$owner", request.OwnerId), ("$user", user));
    }
    public static void Map(RouteGroupBuilder api, Store store, IConfiguration config, IWebHostEnvironment environment)
    {
        api.MapGet("/submission-requests", (HttpContext c, int? offset) => store.Query("""
            SELECT r.MessageId,m.Title,u.Name,m.SenderId,r.Deadline,r.Closed,
              (SELECT COUNT(*) FROM SubmissionTargets t WHERE t.RequestId=r.MessageId),
              (SELECT COUNT(*) FROM SubmissionTargets t WHERE t.RequestId=r.MessageId AND EXISTS(SELECT 1 FROM Submissions s WHERE s.RequestId=t.RequestId AND s.UserId=t.UserId AND NOT EXISTS(SELECT 1 FROM SubmissionWithdrawals w WHERE w.SubmissionId=s.Id))),
              (SELECT COUNT(*) FROM SubmissionTargets t WHERE t.RequestId=r.MessageId AND t.Exempt=1),
              EXISTS(SELECT 1 FROM Submissions s WHERE s.RequestId=r.MessageId AND s.UserId=$user AND NOT EXISTS(SELECT 1 FROM SubmissionWithdrawals w WHERE w.SubmissionId=s.Id)),
              EXISTS(SELECT 1 FROM SubmissionTargets t WHERE t.RequestId=r.MessageId AND t.UserId=$user AND t.Exempt=1)
            FROM SubmissionRequests r JOIN Messages m ON m.Id=r.MessageId JOIN Users u ON u.Id=m.SenderId
            WHERE NOT EXISTS(SELECT 1 FROM MessageRecalls WHERE MessageId=m.Id) AND (m.SenderId=$user OR EXISTS(SELECT 1 FROM SubmissionTargets t WHERE t.RequestId=r.MessageId AND t.UserId=$user))
            ORDER BY m.CreatedAt DESC,m.Id LIMIT 100 OFFSET $offset
            """, r => new { id = r.GetString(0), title = r.GetString(1), ownerName = r.GetString(2), ownerId = r.GetString(3), deadline = r.GetInt64(4), closed = r.GetBoolean(5), targetCount = r.GetString(3) == UserId(c) ? r.GetInt32(6) : 1, submittedCount = r.GetString(3) == UserId(c) ? r.GetInt32(7) : r.GetBoolean(9) ? 1 : 0, exemptCount = r.GetString(3) == UserId(c) ? r.GetInt32(8) : r.GetBoolean(10) ? 1 : 0, submitted = r.GetBoolean(9), exempt = r.GetBoolean(10) }, ("$user", UserId(c)), ("$offset", Math.Clamp(offset ?? 0, 0, 1_000_000))));
        api.MapGet("/submission-requests/{id}", IResult (string id, HttpContext c) =>
        {
            var request = Find(store, id, UserId(c));
            if(request is null)return Results.NotFound();
            using(var connection=store.Open())using(var tx=connection.BeginTransaction()){if(!MessageRecall.Read(connection,tx,id,UserId(c)))return Results.NotFound();tx.Commit();}
            return Results.Ok(new { request, targets = Targets(store, request, UserId(c)) });
        });
        api.MapPost("/submission-requests/{id}/submit", async Task<IResult> (string id, SubmitFiles files, HttpContext c, IHubContext<MessageHub> hub) =>
        {
            if (string.IsNullOrWhiteSpace(files.ClientId) || files.ClientId.Length > 100 || files.AttachmentIds is not { Length: >= 1 and <= 10 } || files.AttachmentIds.Distinct().Count() != files.AttachmentIds.Length) return Error("제출 파일 1~10개와 요청 번호가 필요합니다.");
            var user = UserId(c); var request = Find(store, id, user);
            if (request is null || request.OwnerId == user) return Results.NotFound();
            var fingerprint = JsonSerializer.Serialize(files.AttachmentIds.Order(), Json);
            string submissionId;
            using (var connection = store.Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var previous = Store.Command(connection, "SELECT Id,Fingerprint FROM Submissions WHERE RequestId=$id AND UserId=$user AND ClientId=$client", transaction, ("$id", id), ("$user", user), ("$client", files.ClientId)))
                using (var reader = previous.ExecuteReader())
                    if (reader.Read())
                    {
                        var prior=reader.GetString(0); var same=reader.GetString(1)==fingerprint;
                        reader.Close();
                        using var withdrawn=Store.Command(connection,"SELECT 1 FROM SubmissionWithdrawals WHERE SubmissionId=$id",transaction,("$id",prior));
                        if(withdrawn.ExecuteScalar() is not null)return Error("회수한 제출입니다. 파일을 새로 선택하여 제출하세요.",409);
                        return same?Results.Ok(new {id=prior,duplicate=true}):Error("같은 요청 번호의 파일이 다릅니다.",409);
                    }
                using var state = Store.Command(connection, "SELECT r.Closed+t.Exempt FROM SubmissionRequests r JOIN SubmissionTargets t ON t.RequestId=r.MessageId WHERE r.MessageId=$id AND t.UserId=$user", transaction, ("$id", id), ("$user", user));
                var blocked = state.ExecuteScalar();
                if (blocked is null) return Results.NotFound();
                if (Convert.ToInt32(blocked) != 0) return Error("종료되거나 면제된 제출 요청입니다.", 409);
                long total = 0;
                foreach (var attachment in files.AttachmentIds)
                {
                    if (!Guid.TryParseExact(attachment, "N", out _) || !File.Exists(Path.Combine(store.Files, attachment))) return Error("제출 파일이 없습니다.");
                    using var check = Store.Command(connection, "SELECT Size FROM Attachments a WHERE Id=$file AND OwnerId=$user AND MessageId IS NULL AND DeletedAt IS NULL AND UploadedAt>$cutoff AND NOT EXISTS(SELECT 1 FROM SubmissionFiles sf WHERE sf.AttachmentId=a.Id) AND NOT EXISTS(SELECT 1 FROM ScheduledAttachments WHERE AttachmentId=a.Id)", transaction, ("$file", attachment), ("$user", user), ("$cutoff", Maintenance.Now - 86_400_000));
                    var size = check.ExecuteScalar(); if (size is null) return Error("자신이 새로 업로드한 파일만 제출할 수 있습니다."); total += Convert.ToInt64(size);
                }
                if (total > 209_715_200) return Error("제출 파일 합계는 200MB 이내입니다.", 413);
                submissionId = Guid.NewGuid().ToString("N"); var now = Maintenance.Now;
                using var insert = Store.Command(connection, "INSERT INTO Submissions VALUES($id,$request,$user,$client,$fingerprint,$now)", transaction, ("$id", submissionId), ("$request", id), ("$user", user), ("$client", files.ClientId), ("$fingerprint", fingerprint), ("$now", now)); insert.ExecuteNonQuery();
                var seconds = environment.IsDevelopment() ? Math.Max(1, config.GetValue("School:RetentionSeconds", 2_592_000)) : 2_592_000;
                foreach (var attachment in files.AttachmentIds)
                {
                    using var link = Store.Command(connection, "INSERT INTO SubmissionFiles VALUES($submission,$file)", transaction, ("$submission", submissionId), ("$file", attachment)); link.ExecuteNonQuery();
                    using var expiry = Store.Command(connection, "UPDATE Attachments SET ExpiresAt=MIN(COALESCE(ExpiresAt,UploadedAt+2592000000),$expires) WHERE Id=$file", transaction, ("$expires", now + seconds * 1000L), ("$file", attachment)); expiry.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            try { await hub.Clients.Groups(new[] { request.OwnerId, user }).SendAsync("SubmissionChanged", id); } catch { /* Polling reads committed submissions. */ }
            return Results.Ok(new { id = submissionId, duplicate = false });
        });
        api.MapPost("/submission-requests/{id}/withdraw", async Task<IResult> (string id, WithdrawSubmission edit, HttpContext c,IHubContext<MessageHub> hub) =>
        {
            var user=UserId(c);var request=Find(store,id,user);
            if(request is null||request.OwnerId==user)return Results.NotFound();
            using(var connection=store.Open())
            using(var transaction=connection.BeginTransaction())
            {
                using var state=Store.Command(connection,"SELECT r.Closed+t.Exempt FROM SubmissionRequests r JOIN SubmissionTargets t ON t.RequestId=r.MessageId WHERE r.MessageId=$id AND t.UserId=$user",transaction,("$id",id),("$user",user));
                if(state.ExecuteScalar() is not { } blocked)return Results.NotFound();
                if(Convert.ToInt32(blocked)!=0)return Error("종료되거나 면제된 요청은 회수할 수 없습니다.",409);
                using var latest=Store.Command(connection,"SELECT s.Id FROM Submissions s WHERE s.RequestId=$id AND s.UserId=$user ORDER BY s.CreatedAt DESC,s.rowid DESC LIMIT 1",transaction,("$id",id),("$user",user));
                if(latest.ExecuteScalar() is not string current||current!=edit.SubmissionId)return Error("제출 내용이 변경되었습니다. 새로고침 후 회수하세요.",409);
                // Mark every previous version, so withdrawing the latest never revives an older file.
                using var mark=Store.Command(connection,"INSERT OR IGNORE INTO SubmissionWithdrawals SELECT Id,$now FROM Submissions WHERE RequestId=$id AND UserId=$user",transaction,("$now",Maintenance.Now),("$id",id),("$user",user));mark.ExecuteNonQuery();
                using var erase=Store.Command(connection,"UPDATE Attachments SET ExpiresAt=MIN(ExpiresAt,$now) WHERE Id IN(SELECT sf.AttachmentId FROM SubmissionFiles sf JOIN Submissions s ON s.Id=sf.SubmissionId WHERE s.RequestId=$id AND s.UserId=$user)",transaction,("$now",Maintenance.Now),("$id",id),("$user",user));erase.ExecuteNonQuery();
                transaction.Commit();
            }
            try{await hub.Clients.Groups(new[]{request.OwnerId,user}).SendAsync("SubmissionChanged",id);}catch{/* Polling reads the committed state. */}
            return Results.Ok();
        });
        api.MapPost("/submission-requests/{id}/close", IResult (string id, HttpContext c) => store.Execute("UPDATE SubmissionRequests SET Closed=1 WHERE MessageId=$id AND EXISTS(SELECT 1 FROM Messages m WHERE m.Id=MessageId AND m.SenderId=$user)", ("$id", id), ("$user", UserId(c))) > 0 ? Results.Ok() : Results.NotFound());
        api.MapPatch("/submission-requests/{id}/targets/{target}", IResult (string id, string target, Exemption edit, HttpContext c) => store.Execute("UPDATE SubmissionTargets SET Exempt=$exempt WHERE RequestId=$id AND UserId=$target AND EXISTS(SELECT 1 FROM Messages m WHERE m.Id=RequestId AND m.SenderId=$user)", ("$exempt", edit.Exempt ? 1 : 0), ("$id", id), ("$target", target), ("$user", UserId(c))) > 0 ? Results.Ok() : Results.NotFound());
        api.MapGet("/submission-requests/{id}/zip", async (string id, HttpContext c) =>
        {
            var request = Find(store, id, UserId(c));
            if (request is null || request.OwnerId != UserId(c)) { c.Response.StatusCode = 404; return; }
            var targets = Targets(store, request, UserId(c));
            var notes = new StringBuilder("최신 제출본만 포함합니다. 파일은 제출일부터 30일간 보관됩니다.\r\n");
                c.Response.ContentType = "application/zip";
                c.Response.Headers.ContentDisposition = "attachment; filename=school-submissions.zip";
                // ZipArchive finalizes synchronously; this endpoint alone allows that final footer on an unbuffered response.
                var bodyControl = c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;
                using var archive = new ZipArchive(c.Response.Body, ZipArchiveMode.Create, true, Encoding.UTF8);
                foreach (var target in targets)
                foreach (var file in target.Files)
                {
                    if (file.Expired || file.ExpiresAt <= Maintenance.Now || !Guid.TryParseExact(file.Id, "N", out _)) { notes.AppendLine($"{target.Name}: {file.Name} — 만료 또는 삭제"); continue; }
                    using(var verification=store.Open())if(!StoredFiles.Ready(verification,"office",file.Id,Path.Combine(store.Files,file.Id))){notes.AppendLine($"{target.Name}: {file.Name} — 파일 누락 또는 무결성 실패");continue;}
                    FileStream stream;
                    try { stream = new FileStream(Path.Combine(store.Files, file.Id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
                    catch (FileNotFoundException) { notes.AppendLine($"{target.Name}: {file.Name} — 파일 없음"); continue; }
                    await using (stream)
                    {
                        var entryName = $"{SafeName(target.Name)}-{target.Id}/{file.Id}-{SafeName(file.Name)}";
                        await using var output = archive.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                        await stream.CopyToAsync(output, c.RequestAborted);
                    }
                }
                await using var manifest = archive.CreateEntry("안내.txt").Open(); await manifest.WriteAsync(Encoding.UTF8.GetBytes(notes.ToString()), c.RequestAborted);
        }).RequireRateLimiting("zip");
    }
    static string SafeName(string name) => string.Concat(name.Take(100).Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c)).Trim(' ', '.');
}

public sealed class SubmissionReminders(Store store, IWebHostEnvironment environment, IConfiguration config, ILogger<SubmissionReminders> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A fixed clock is available only in Development for isolated date-boundary/restart tests.
                var now = environment.IsDevelopment() ? config.GetValue<long?>("School:ReminderNow") ?? Maintenance.Now : Maintenance.Now;
                Process(now);
            }
            catch (Exception error) { logger.LogError("Submission reminder processing failed ({ErrorCode})",error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    public List<(string User, string Message)> Process(long now)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(now).ToOffset(TimeSpan.FromHours(9));
        var delivered = new List<(string User, string Message)>();
        if (local.Hour < 9) return delivered;
        var day = TaskFeatures.KoreanDate(now);
        // ponytail: one SQLite write transaction for a school's roster; use batches if measured lock times grow.
        using var connection = store.Open(); using var transaction = connection.BeginTransaction();
        var targets = new List<(string Request, string User, string Owner, string Title, long Deadline)>();
        using (var find = Store.Command(connection, """
            SELECT r.MessageId,t.UserId,m.SenderId,m.Title,r.Deadline FROM SubmissionRequests r JOIN Messages m ON m.Id=r.MessageId
            JOIN SubmissionTargets t ON t.RequestId=r.MessageId JOIN Users u ON u.Id=t.UserId JOIN Users owner ON owner.Id=m.SenderId
            WHERE r.Closed=0 AND t.Exempt=0 AND u.Active=1 AND owner.Active=1
            AND NOT EXISTS(SELECT 1 FROM Submissions s WHERE s.RequestId=r.MessageId AND s.UserId=t.UserId AND NOT EXISTS(SELECT 1 FROM SubmissionWithdrawals w WHERE w.SubmissionId=s.Id))
            AND NOT EXISTS(SELECT 1 FROM SubmissionReminders n WHERE n.RequestId=r.MessageId AND n.UserId=t.UserId AND n.Day=$day)
            """, transaction, ("$day", day.ToString("yyyy-MM-dd"))))
        using (var reader = find.ExecuteReader())
            while (reader.Read()) targets.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        foreach (var target in targets)
        {
            if (day < TaskFeatures.KoreanDate(target.Deadline).AddDays(-2)) continue;
            var id = Guid.NewGuid().ToString("N");
            var body = $"[시스템 자동 알림]\n‘{target.Title}’ 파일이 아직 제출되지 않았습니다.\n마감: {TaskFeatures.KoreanDate(target.Deadline):yyyy-MM-dd}\n‘파일 제출 · 현황 보기’에서 원본 요청을 확인하고 파일을 올려 주세요.";
            using var insert = Store.Command(connection, "INSERT INTO Messages(Id,SenderId,ClientId,Fingerprint,Title,Body,CreatedAt) VALUES($id,$owner,$client,'system-reminder',$title,$body,$now)", transaction, ("$id", id), ("$owner", target.Owner), ("$client", "reminder:" + id), ("$title", "[자동 알림] " + target.Title), ("$body", body), ("$now", now)); insert.ExecuteNonQuery();
            using var recipient = Store.Command(connection, "INSERT INTO Recipients(MessageId,UserId) VALUES($id,$user)", transaction, ("$id", id), ("$user", target.User)); recipient.ExecuteNonQuery();
            using var reminder = Store.Command(connection, "INSERT INTO SubmissionReminders VALUES($request,$user,$day,$message)", transaction, ("$request", target.Request), ("$user", target.User), ("$day", day.ToString("yyyy-MM-dd")), ("$message", id)); reminder.ExecuteNonQuery();
            MessageDelivery.Enqueue(connection,transaction,"message:"+id+":"+target.User,target.User,"NewMessage",id,now);
            delivered.Add((target.User, id));
        }
        transaction.Commit(); return delivered;
    }
}

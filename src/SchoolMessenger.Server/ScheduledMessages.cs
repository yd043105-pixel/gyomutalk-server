using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchoolMessenger.Shared;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

public sealed class ScheduledMessages(Store store,IHostEnvironment environment,ILogger<ScheduledMessages> log):BackgroundService
{
    readonly SemaphoreSlim gate=new(1);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal Func<SendMessage,string,Task<(int Status,string? Id,string? Error)>>? Deliver{get;set;}
    public void Initialize()=>store.Execute("""
        CREATE TABLE IF NOT EXISTS ScheduledMessages(Id TEXT PRIMARY KEY,SenderId TEXT NOT NULL REFERENCES Users(Id),ClientId TEXT NOT NULL,
          Payload TEXT NOT NULL,Fingerprint TEXT NOT NULL,DeliverAt INTEGER NOT NULL,RequiresBroadcast INTEGER NOT NULL,
          State TEXT NOT NULL DEFAULT 'pending',MessageId TEXT,Error TEXT,NextAttempt INTEGER NOT NULL DEFAULT 0,UNIQUE(SenderId,ClientId));
        CREATE INDEX IF NOT EXISTS IX_ScheduledMessages_Due ON ScheduledMessages(State,DeliverAt,NextAttempt);
        CREATE TABLE IF NOT EXISTS ScheduledAttachments(ScheduleId TEXT NOT NULL REFERENCES ScheduledMessages(Id),AttachmentId TEXT NOT NULL UNIQUE REFERENCES Attachments(Id),PRIMARY KEY(ScheduleId,AttachmentId));
        """);
    public bool Exists(string sender,string client)=>store.Query("SELECT 1 FROM ScheduledMessages WHERE SenderId=$sender AND ClientId=$client",r=>r.GetInt32(0),("$sender",sender),("$client",client)).Count>0;
    internal IResult Add(SendMessage request,HttpContext context)
    {
        var sender=store.GetUser(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if(sender is not{Active:true}||StaffAccounts.Pending(store,sender.Id))return Error("사용할 수 없는 계정입니다.",403);
        var at=request.ScheduledAt!.Value;var now=Maintenance.Now;
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request with{RecipientIds=request.RecipientIds!.Distinct().Order().ToArray(),AttachmentIds=request.AttachmentIds!.Distinct().Order().ToArray()},Json))));
        gate.Wait();
        try
        {
            using var db=store.Open();using var tx=db.BeginTransaction();
            using(var check=Store.Command(db,"SELECT 1 FROM Users WHERE Id=$id AND Active=1 AND SessionVersion=$version",tx,("$id",sender.Id),("$version",context.User.FindFirstValue("version"))))if(check.ExecuteScalar() is null)return Error("다시 로그인하세요.",401);
            using(var existing=Store.Command(db,"SELECT Id,Fingerprint,State FROM ScheduledMessages WHERE SenderId=$sender AND ClientId=$client",tx,("$sender",sender.Id),("$client",request.ClientId)))
            using(var r=existing.ExecuteReader())if(r.Read())return r.GetString(1)==fingerprint?Results.Ok(new{id=r.GetString(0),scheduled=true,state=r.GetString(2),duplicate=true}):Error("같은 요청 번호로 다른 예약을 등록할 수 없습니다.",409);
            using(var existing=Store.Command(db,"SELECT 1 FROM Messages WHERE SenderId=$sender AND ClientId=$client",tx,("$sender",sender.Id),("$client",request.ClientId)))if(existing.ExecuteScalar() is not null)return Error("이미 사용한 전송 요청 번호입니다.",409);
        if(at<=now||at>now+366*86_400_000L)return Error("예약 시각은 현재 이후 1년 이내로 지정하세요.");
        if(request.SubmissionDeadline is{} due&&(due<=at||TaskFeatures.KoreanDate(due)>TaskFeatures.KoreanDate(now).AddDays(366)))return Error("제출 마감은 예약 발송 이후 1년 이내로 지정하세요.");
            var active=store.Users().Where(u=>u.Active).ToArray();var ids=request.RecipientIds!.Distinct().ToList();
            if(ids.Any(id=>!active.Any(u=>u.Id==id)))return Error("수신자를 확인하세요.");
            if(!string.IsNullOrEmpty(request.Department))ids.AddRange(active.Where(u=>u.Department==request.Department).Select(u=>u.Id));
            if(request.All)ids.AddRange(active.Select(u=>u.Id));ids=ids.Distinct().ToList();
            if(ids.Count==0||request.SubmissionDeadline is not null&&ids.All(id=>id==sender.Id))return Error("수신자와 제출 대상을 확인하세요.");
            var broadcast=request.All||active.Length>1&&ids.Count==active.Length;
            if(broadcast&&!sender.CanBroadcast&&!sender.IsAdmin)return Error("전체 발송 권한이 없습니다.",403);
            long total=0;
            foreach(var file in request.AttachmentIds!.Distinct())
            {
                using var c=Store.Command(db,"SELECT Size FROM Attachments WHERE Id=$id AND OwnerId=$owner AND MessageId IS NULL AND DeletedAt IS NULL AND UploadedAt>$cutoff AND ExpiresAt>$at AND NOT EXISTS(SELECT 1 FROM SubmissionFiles WHERE AttachmentId=$id) AND NOT EXISTS(SELECT 1 FROM ScheduledAttachments WHERE AttachmentId=$id)",tx,("$id",file),("$owner",sender.Id),("$cutoff",now-86_400_000),("$at",at));
                var size=c.ExecuteScalar();if(size is null||!Guid.TryParseExact(file,"N",out _)||!File.Exists(Path.Combine(store.Files,file)))return Error("첨부파일의 상태·소유자·예약 시각 이전 만료 여부를 확인하세요.");total+=Convert.ToInt64(size);
            }
            if(total>209_715_200)return Error("첨부 합계는 200MB 이하여야 합니다.",413);
            var id=Guid.NewGuid().ToString("N");var payload=request with{All=false,Department=null,RecipientIds=ids.ToArray(),ScheduledAt=null};
            using(var c=Store.Command(db,"INSERT INTO ScheduledMessages(Id,SenderId,ClientId,Payload,Fingerprint,DeliverAt,RequiresBroadcast) VALUES($id,$sender,$client,$payload,$hash,$at,$broadcast)",tx,("$id",id),("$sender",sender.Id),("$client",request.ClientId),("$payload",JsonSerializer.Serialize(payload,Json)),("$hash",fingerprint),("$at",at),("$broadcast",broadcast)))c.ExecuteNonQuery();
            foreach(var file in request.AttachmentIds!.Distinct()){using var c=Store.Command(db,"INSERT INTO ScheduledAttachments VALUES($id,$file)",tx,("$id",id),("$file",file));c.ExecuteNonQuery();}
            tx.Commit();return Results.Ok(new{id,scheduled=true,state="pending"});
        }
        catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("첨부파일이 이미 사용 중이거나 예약이 변경되었습니다.",409);}
        finally{gate.Release();}
    }
    public void Map(RouteGroupBuilder api)
    {
        api.MapGet("/messages/scheduled",(HttpContext c)=>store.Query("SELECT Id,Payload,DeliverAt,State,MessageId,Error FROM ScheduledMessages WHERE SenderId=$sender ORDER BY CASE State WHEN 'pending' THEN 0 ELSE 1 END,DeliverAt DESC LIMIT 100",r=>new{id=r.GetString(0),title=JsonSerializer.Deserialize<SendMessage>(r.GetString(1),Json)!.Title,deliverAt=r.GetInt64(2),state=r.GetString(3),messageId=r.IsDBNull(4)?null:r.GetString(4),error=r.IsDBNull(5)?null:r.GetString(5)},("$sender",c.User.FindFirstValue(ClaimTypes.NameIdentifier))));
        api.MapDelete("/messages/scheduled/{id}",async(string id,HttpContext c)=>
        {
            await gate.WaitAsync(c.RequestAborted);try
            {
                using var db=store.Open();using var tx=db.BeginTransaction();
                using var change=Store.Command(db,"UPDATE ScheduledMessages SET State='cancelled' WHERE Id=$id AND SenderId=$sender AND State='pending'",tx,("$id",id),("$sender",c.User.FindFirstValue(ClaimTypes.NameIdentifier)));
                if(change.ExecuteNonQuery()!=1)return Error("취소할 수 있는 본인의 예약이 없습니다.",409);
                using var release=Store.Command(db,"DELETE FROM ScheduledAttachments WHERE ScheduleId=$id",tx,("$id",id));release.ExecuteNonQuery();tx.Commit();return Results.Ok();
            }finally{gate.Release();}
        });
    }
    static IResult Error(string text,int code=400)=>Results.Json(new{error=text},statusCode:code);
    async Task Tick(CancellationToken token)
    {
        await gate.WaitAsync(token);try
        {
            using(var db=store.Open()){using var review=db.CreateCommand();review.CommandText="SELECT ReviewRequired FROM BackendRecoveryState WHERE Id=1";if(Convert.ToInt32(review.ExecuteScalar())==1)return;}
            var due=store.Query("SELECT Id,SenderId,ClientId,Payload,RequiresBroadcast FROM ScheduledMessages WHERE State='pending' AND DeliverAt<=$now AND NextAttempt<=$now ORDER BY DeliverAt,Id LIMIT 20",r=>new{Id=r.GetString(0),Sender=r.GetString(1),Client=r.GetString(2),Payload=r.GetString(3),Broadcast=r.GetBoolean(4)},("$now",Maintenance.Now));
            foreach(var item in due)
            {
                var existing=store.Query("SELECT Id FROM Messages WHERE SenderId=$sender AND ClientId=$client",r=>r.GetString(0),("$sender",item.Sender),("$client",item.Client)).FirstOrDefault();
                var user=store.GetUser(item.Sender);var result=(Status:200,Id:existing,Error:(string?)null);
                if(existing is null)
                {
                    if(user is not{Active:true}||StaffAccounts.Pending(store,item.Sender)||item.Broadcast&&!user.CanBroadcast&&!user.IsAdmin)result=(403,null,"발신 계정 또는 발송 권한이 변경되었습니다.");
                    else result=await Deliver!(JsonSerializer.Deserialize<SendMessage>(item.Payload,Json)!,item.Sender);
                }
                if(result.Status>=500){store.Execute("UPDATE ScheduledMessages SET NextAttempt=$next,Error='서버 재시도 대기' WHERE Id=$id",("$next",Maintenance.Now+15_000),("$id",item.Id));continue;}
                using var db=store.Open();using var tx=db.BeginTransaction();
                using(var update=Store.Command(db,"UPDATE ScheduledMessages SET State=$state,MessageId=$message,Error=$error WHERE Id=$id",tx,("$state",result.Status==200?"delivered":"failed"),("$message",result.Id),("$error",result.Error),("$id",item.Id)))update.ExecuteNonQuery();
                using(var release=Store.Command(db,"DELETE FROM ScheduledAttachments WHERE ScheduleId=$id",tx,("$id",item.Id)))release.ExecuteNonQuery();tx.Commit();
            }
        }finally{gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await Tick(stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}catch(Exception e){log.LogError("Scheduled message retry ({Type})",e.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(environment.IsDevelopment()?1:5),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed partial class ExternalAnnouncements : BackgroundService
{
    readonly Store store;
    readonly IConfiguration config;
    readonly IHostEnvironment environment;
    readonly HttpClient? http;
    readonly SemaphoreSlim gate = new(1);
    readonly ILogger<ExternalAnnouncements> log;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    PortalDirectory? directory;
    long synced;
    string? lastError;
    string Files => Path.Combine(store.Root,"external-files");
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    record Job(string Id,string Owner,string Teacher,string Payload,string Targets,string State,string? Remote,int Attempts,long Next,bool Withdraw,string? Error);
    record LocalFile(string Id,string Name,long Size,string? Remote,long Uploaded,long? Expires);
    public ExternalAnnouncements(Store store,IConfiguration config,IHostEnvironment environment,ILogger<ExternalAnnouncements> log,DeploymentReview review)
    {
        this.store=store;this.config=config;this.environment=environment;this.log=log;
        Directory.CreateDirectory(Files);
        store.Execute("""
            CREATE TABLE IF NOT EXISTS ExternalOutbox(Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES Users(Id),TeacherId TEXT NOT NULL,
              ClientId TEXT NOT NULL,Payload TEXT NOT NULL,Targets TEXT NOT NULL,State TEXT NOT NULL,RemoteId TEXT,Attempts INTEGER NOT NULL DEFAULT 0,
              NextAttempt INTEGER NOT NULL DEFAULT 0,Withdraw INTEGER NOT NULL DEFAULT 0,Error TEXT,CreatedAt INTEGER NOT NULL,Detail TEXT,UNIQUE(OwnerId,ClientId));
            CREATE TABLE IF NOT EXISTS ExternalFiles(Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES Users(Id),Name TEXT NOT NULL,Size INTEGER NOT NULL,
              UploadedAt INTEGER NOT NULL,JobId TEXT REFERENCES ExternalOutbox(Id),RemoteId TEXT,ExpiresAt INTEGER,DeletedAt INTEGER);
            """);
        if(string.IsNullOrWhiteSpace(config["Announcements:Address"]))return;
        if(!Uri.TryCreate(config["Announcements:Address"]!.TrimEnd('/')+"/",UriKind.Absolute,out var address)||address.UserInfo.Length!=0||address.Query.Length!=0||address.Fragment.Length!=0||address.AbsolutePath!="/"||
            address.Scheme!="https"&&!(environment.IsDevelopment()&&address.Scheme=="http"&&address.IsLoopback))throw new InvalidOperationException("외부 공지 주소는 HTTPS 서버의 기본 주소여야 합니다.");
        var key=config["Announcements:BridgeKey"];
        if(key is not{Length:>=32})throw new InvalidOperationException("외부 공지 연결 키가 필요합니다.");
        http=new HttpClient(new BridgeSigningHandler(config,review)){BaseAddress=address,Timeout=TimeSpan.FromSeconds(120)};

    }
    public override void Dispose(){http?.Dispose();base.Dispose();}
    static string Owner(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static IResult Error(string text,int status=400)=>Results.Json(new{error=text},statusCode:status);
    DirectoryPerson? Publisher(string owner)=>directory?.People.FirstOrDefault(p=>p.Role=="teacher"&&p.InternalUserId==owner);
    public PortalDirectory? TimetableDirectory=>synced>Now-60000?directory:null;
    PortalNoticeDetail? SafeDetail(string? value,string owner)
    {
        if(value is null||synced<Now-60000)return null;
        var teacher=Publisher(owner);var user=store.GetUser(owner);if(teacher is null||user is not{Active:true})return null;
        var detail=JsonSerializer.Deserialize<PortalNoticeDetail>(value,Json);
        return detail is null?null:detail with{Receipts=detail.Receipts?.Where(r=>teacher.Classes.Contains(r.ClassId)||teacher.CanBroadcast&&user.CanBroadcast||(directory?.Sections??[]).Any(s=>s.TeacherIds.Contains(teacher.Id)&&s.Members.Any(m=>m.StudentId==r.StudentId))).ToArray()};
    }
    Job? GetJob(string id,string? owner=null)=>store.Query("SELECT Id,OwnerId,TeacherId,Payload,Targets,State,RemoteId,Attempts,NextAttempt,Withdraw,Error FROM ExternalOutbox WHERE Id=$id AND ($owner IS NULL OR OwnerId=$owner)",r=>new Job(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetInt32(7),r.GetInt64(8),r.GetBoolean(9),r.IsDBNull(10)?null:r.GetString(10)),("$id",id),("$owner",owner)).FirstOrDefault();
    List<LocalFile> JobFiles(string id)=>store.Query("SELECT Id,Name,Size,RemoteId,UploadedAt,ExpiresAt FROM ExternalFiles WHERE JobId=$job AND DeletedAt IS NULL",r=>new LocalFile(r.GetString(0),r.GetString(1),r.GetInt64(2),r.IsDBNull(3)?null:r.GetString(3),r.GetInt64(4),r.IsDBNull(5)?null:r.GetInt64(5)),("$job",id));
    public static void Map(WebApplication app,ExternalAnnouncements service)
    {
        MapCounseling(app,service);
        MapElectives(app,service);
        var api=app.MapGroup("/api/external-announcements").RequireAuthorization();
        api.MapGet("/setup",(HttpContext c)=>
        {
            var teacher=service.Publisher(Owner(c));var user=service.store.GetUser(Owner(c))!;
            return Results.Ok(new{configured=service.http is not null,connected=service.synced>Now-60000,internalUserId=user.Id,
                teacherId=teacher?.Id,canBroadcast=teacher?.CanBroadcast==true&&user.CanBroadcast,
                classes=service.directory?.Classes.Where(room=>teacher is not null&&(teacher.Classes.Contains(room.Id)||teacher.CanBroadcast&&user.CanBroadcast)).ToArray()??[],
                sections=(service.directory?.Sections??[]).Where(s=>teacher is not null&&(s.TeacherIds.Contains(teacher.Id)||teacher.CanBroadcast&&user.CanBroadcast)).Select(s=>new{s.Id,s.Name,s.Term,s.Revision,memberCount=s.Members.Length}).ToArray(),
                lastConnectedAt=service.synced,error=service.lastError});
        });
        api.MapGet("",(HttpContext c)=>service.store.Query("SELECT Id,Payload,State,RemoteId,Error,CreatedAt,Detail,Withdraw FROM ExternalOutbox WHERE OwnerId=$owner ORDER BY CreatedAt DESC LIMIT 100",r=>new{id=r.GetString(0),request=JsonSerializer.Deserialize<AnnouncementRequest>(r.GetString(1),Json),state=r.GetString(2),remoteId=r.IsDBNull(3)?null:r.GetString(3),error=r.IsDBNull(4)?null:r.GetString(4),createdAt=r.GetInt64(5),detail=r.IsDBNull(6)?null:service.SafeDetail(r.GetString(6),Owner(c)),withdrawRequested=r.GetBoolean(7)},("$owner",Owner(c))));
        api.MapPost("",async Task<IResult>(AnnouncementRequest request,HttpContext c)=>
        {
            await service.gate.WaitAsync(c.RequestAborted);
            try{return service.Enqueue(request,Owner(c));}finally{service.gate.Release();}
        });
        api.MapPost("/{id}/retract",async Task<IResult>(string id,HttpContext c)=>
        {
            await service.gate.WaitAsync(c.RequestAborted);
            try
            {
                if(service.GetJob(id,Owner(c)) is null)return Results.NotFound();
                service.store.Execute("UPDATE ExternalOutbox SET Withdraw=1,State='withdraw-pending',NextAttempt=0,Error=NULL WHERE Id=$id AND State!='withdrawn'",("$id",id));return Results.Ok();
            }finally{service.gate.Release();}
        });
        api.MapPost("/attachments",async Task<IResult>(HttpContext c)=>await service.Upload(c)).RequireRateLimiting("upload");
        app.MapGet("/api/admin/announcement-status",()=>new{configured=service.http is not null,connected=service.synced>Now-60000,lastConnectedAt=service.synced,error=service.lastError,
            pending=service.store.Query("SELECT COUNT(*) FROM ExternalOutbox WHERE State IN ('pending','withdraw-pending')",r=>r.GetInt32(0))[0],needsReview=service.store.Query("SELECT COUNT(*) FROM ExternalOutbox WHERE State='needs-review'",r=>r.GetInt32(0))[0]}).RequireAuthorization("Admin");
        app.MapGet("/api/admin/sections/setup",()=>Results.Ok(new{connected=service.synced>Now-60000,sections=service.directory?.Sections??[],people=service.directory?.People.Where(p=>p.Role is "student" or "teacher").Select(p=>new{p.Id,p.Role,p.ClassId,p.InternalUserId,name=p.Name})??[]})).RequireAuthorization("Admin");
        app.MapPost("/api/admin/sections",(SectionRequest request,HttpContext c)=>service.SaveSection(request,null,c.RequestAborted)).RequireAuthorization("Admin");
        app.MapPost("/api/admin/sections/{id}",(string id,SectionRequest request,HttpContext c)=>service.SaveSection(request,id,c.RequestAborted)).RequireAuthorization("Admin");
    }
    async Task<IResult> SaveSection(SectionRequest request,string? id,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if(http is null)return Error("외부 학교 소식 서버 연결을 먼저 설정하세요.",503);
            using var response=await http.PostAsJsonAsync("bridge/sections"+(id is null?"":"/"+Uri.EscapeDataString(id)),request,Json,cancellation);
            var result=await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
            if(response.IsSuccessStatusCode){directory=await http.GetFromJsonAsync<PortalDirectory>("bridge/directory",Json,cancellation);synced=Now;}
            return Results.Json(result,statusCode:(int)response.StatusCode);
        }
        finally{gate.Release();}
    }
    IResult Enqueue(AnnouncementRequest request,string owner)
    {
        if(http is null||directory is null||synced<Now-60000)return Error("외부 공지 서버 연결을 확인하세요.",503);
        var teacher=Publisher(owner);var user=store.GetUser(owner);
        if(teacher is null||user is not{Active:true})return Error("관리자가 외부 공지 교사 계정을 연결해야 합니다.",403);
        if(request is null||!Guid.TryParseExact(request.ClientId,"N",out _)||string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200||string.IsNullOrWhiteSpace(request.Body)||request.Body.Length>10000||
            request.Audience is not("students"or"parents"or"both")||request.Kind is not("notice"or"home-letter")||request.ClassIds is null||request.ClassIds.Length>100||request.AttachmentIds is null||request.AttachmentIds.Length>5||request.AttachmentIds.Distinct().Count()!=request.AttachmentIds.Length||
            request.SectionIds?.Length>100||request.SectionIds?.Any(id=>!Guid.TryParseExact(id,"N",out _))==true||request.All&&(request.ClassIds.Length!=0||(request.SectionIds?.Length??0)>0)||!request.All&&request.ClassIds.Length==0&&(request.SectionIds?.Length??0)==0)return Error("제목·내용·학급·분반·첨부를 확인하세요.");
        if(request.All&&(!teacher.CanBroadcast||!user.CanBroadcast)||!request.All&&request.ClassIds.Any(id=>!teacher.Classes.Contains(id)))return Error("담당 학급 또는 전체 발송 권한을 확인하세요.",403);
        var payload=JsonSerializer.Serialize(request,Json);
        var existing=store.Query("SELECT Id,Payload FROM ExternalOutbox WHERE OwnerId=$owner AND ClientId=$client",r=>(id:r.GetString(0),payload:r.GetString(1)),("$owner",owner),("$client",request.ClientId)).FirstOrDefault();
        if(existing.id is not null)return JsonSerializer.Serialize(JsonSerializer.Deserialize<AnnouncementRequest>(existing.payload,Json),Json)==payload?Results.Ok(new{id=existing.id,duplicate=true}):Error("같은 발송 번호의 내용을 변경할 수 없습니다.",409);
        AnnouncementTarget[] targets;try{targets=AnnouncementAudience.Build(directory,teacher with{CanBroadcast=teacher.CanBroadcast&&user.CanBroadcast},request);}catch(InvalidOperationException e){return Error(e.Message,403);}
        if(targets.Length is 0 or >10000)return Error("확정된 학생·보호자 수신 대상이 없습니다.");
        using var db=store.Open();using var tx=db.BeginTransaction();long total=0;
        foreach(var file in request.AttachmentIds)
        {
            using var check=Store.Command(db,"SELECT Size FROM ExternalFiles WHERE Id=$id AND OwnerId=$owner AND JobId IS NULL AND DeletedAt IS NULL AND UploadedAt>$cutoff",tx,("$id",file),("$owner",owner),("$cutoff",Now-86400000));
            var size=check.ExecuteScalar();if(size is null||!File.Exists(Path.Combine(Files,file)))return Error("공지 전용 첨부를 다시 올려주세요.",409);total+=Convert.ToInt64(size);
            {
                using var filename=Store.Command(db,"SELECT Name FROM ExternalFiles WHERE Id=$id",tx,("$id",file));var name=(string)filename.ExecuteScalar()!;
                try{FilePolicy.NoticeImageName(name,Convert.ToInt64(size));FilePolicy.NoticeImage(Path.Combine(Files,file),name);}catch(FilePolicyException e){return Error(e.Message,e.Status);}
            }
        }
        if(total>FilePolicy.MaxTotal)return Error("첨부 합계는 200MB까지입니다.",413);
        var id=Guid.NewGuid().ToString("N");
        using(var insert=Store.Command(db,"INSERT INTO ExternalOutbox(Id,OwnerId,TeacherId,ClientId,Payload,Targets,State,CreatedAt) VALUES($id,$owner,$teacher,$client,$payload,$targets,'pending',$now)",tx,("$id",id),("$owner",owner),("$teacher",teacher.Id),("$client",request.ClientId),("$payload",payload),("$targets",JsonSerializer.Serialize(targets,Json)),("$now",Now)))insert.ExecuteNonQuery();
        foreach(var file in request.AttachmentIds){using var bind=Store.Command(db,"UPDATE ExternalFiles SET JobId=$job WHERE Id=$id",tx,("$job",id),("$id",file));bind.ExecuteNonQuery();}
        tx.Commit();return Results.Ok(new{id,duplicate=false});
    }
    async Task<IResult> Upload(HttpContext c)
    {
        if(http is null||synced<Now-60000)return Error("외부 서버 연결을 확인하세요.",503);
        if(Publisher(Owner(c)) is null)return Error("외부 공지 교사 연결이 필요합니다.",403);
        if(!c.Request.HasFormContentType)return Error("파일을 선택하세요.");var form=await c.Request.ReadFormAsync(c.RequestAborted);
        if(form.Files.Count!=1)return Error("파일 하나를 선택하세요.");var file=form.Files[0];
        string name;
        var kind=form["kind"].ToString();if(kind is not(""or"notice"or"home-letter"))return Error("공지 유형을 확인하세요.");
        try{name=FilePolicy.NoticeImageName(file.FileName,file.Length);}catch(FilePolicyException e){return Error(e.Message,e.Status);}
        if(new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace<file.Length+FilePolicy.MaxStaged)return Error("저장 공간이 부족합니다.",507);
        var id=Guid.NewGuid().ToString("N");var path=Path.Combine(Files,id);var kept=false;
        try
        {
            await using(var stream=File.Create(path))await file.CopyToAsync(stream,c.RequestAborted);
            FilePolicy.NoticeImage(path,name);
            await FilePolicy.Scan(path,name,environment.IsDevelopment(),config["School:ScannerPath"],c.RequestAborted);
            if(store.GetUser(Owner(c)) is not{Active:true})return Error("교사 계정이 비활성화되었습니다.",403);
            using var db=store.Open();using var tx=db.BeginTransaction();
            using var insert=Store.Command(db,"INSERT INTO ExternalFiles(Id,OwnerId,Name,Size,UploadedAt,ExpiresAt) SELECT $id,$owner,$name,$size,$now,$expires WHERE (SELECT COALESCE(SUM(Size),0) FROM ExternalFiles WHERE OwnerId=$owner AND JobId IS NULL AND DeletedAt IS NULL)+$size<=$quota",tx,("$id",id),("$owner",Owner(c)),("$name",name),("$size",file.Length),("$now",Now),("$expires",Now+2_592_000_000L),("$quota",FilePolicy.MaxStaged));
            if(insert.ExecuteNonQuery()==0)return Error("미게시 첨부는 500MB까지입니다.",413);StoredFiles.State(db,"external",id,"ready",StoredFiles.Hash(path),tx);tx.Commit();kept=true;return Results.Ok(new PortalFile(id,name,file.Length));
        }catch(FilePolicyException e){return Error(e.Message,e.Status);}finally{if(!kept)File.Delete(path);}
    }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var verification=Synchronize(token);
        try{while(!token.IsCancellationRequested)
        {
            try
            {
                Cleanup();
                if(http is not null&&synced>Now-60000)
                {
                    var ids=store.Query("SELECT Id FROM ExternalOutbox WHERE State IN ('pending','withdraw-pending','published') AND NextAttempt<=$now ORDER BY CASE WHEN State='published' THEN 1 ELSE 0 END,NextAttempt,CreatedAt LIMIT 30",r=>r.GetString(0),("$now",Now));
                    foreach(var id in ids){token.ThrowIfCancellationRequested();await gate.WaitAsync(token);try{await Dispatch(GetJob(id)!,token);}finally{gate.Release();}}
                }
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception e){log.LogWarning("Announcement outbox unavailable ({Type})",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(environment.IsDevelopment()?2:15),token);
        }}finally{try{await verification;}catch(OperationCanceledException)when(token.IsCancellationRequested){}}
    }
    async Task Synchronize(CancellationToken token)
    {
        if(http is null)return;
        while(!token.IsCancellationRequested)
        {
            try
            {
                await gate.WaitAsync(token);
                try
                {
                var fresh=await http.GetFromJsonAsync<PortalDirectory>("bridge/directory",Json,token)??throw new IOException("학급 정보를 받지 못했습니다.");
                var erased=await http.GetFromJsonAsync<string[]>("bridge/privacy-erasure",Json,token)??[];
                if(erased.Length>0)
                {
                    lock(AttachmentBackup.Gate)
                    {
                    using(var db=store.Open()){GraduationErasure.Office(db,erased);GraduationErasure.Compact(db);}
                    GraduationErasure.Backups(store.Root,erased,false,store.BackupDirectory);
                    }
                    using var acknowledgment=await http.PostAsJsonAsync("bridge/privacy-erasure/ack",new{ids=erased},Json,token);await Check(acknowledgment);
                }
                var teachers=fresh.People.Where(p=>p.Role=="teacher").Select(p=>{var user=p.InternalUserId is null?null:store.GetUser(p.InternalUserId);return new TeacherProof(p.Id,user?.Active==true&&!StaffAccounts.Pending(store,user.Id),user?.CanBroadcast==true);}).ToArray();
                using var response=await http.PostAsJsonAsync("bridge/heartbeat",new BridgeHeartbeat(teachers),Json,token);await Check(response);
                directory=fresh;synced=Now;lastError=null;
                }
                finally{gate.Release();}
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception e){lastError="외부 공지 연결 실패: 연결·인증 설정을 확인하세요.";log.LogWarning("Announcement bridge unavailable ({Type})",e.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(environment.IsDevelopment()?2:15),token);
        }
    }
    async Task Dispatch(Job job,CancellationToken token)
    {
        try
        {
            var request=JsonSerializer.Deserialize<AnnouncementRequest>(job.Payload,Json)!;
            // Probe before uploading: a lost response must not republish or extend file retention.
            PortalNoticeDetail? detail=null;
            using(var probe=await http!.GetAsync("bridge/announcements/by-client/"+request.ClientId+"?teacherId="+job.Teacher,token))
            {if(probe.StatusCode!=HttpStatusCode.NotFound){await Check(probe);detail=await probe.Content.ReadFromJsonAsync<PortalNoticeDetail>(Json,token);}}
            if(job.Withdraw)
            {
                if(detail is not null){using var retract=await http.PostAsJsonAsync("bridge/announcements/"+detail.Notice.Id+"/retract",new{teacherId=job.Teacher},Json,token);await Check(retract);}
                store.Execute("UPDATE ExternalOutbox SET State='withdrawn',Error=NULL WHERE Id=$id",("$id",job.Id));return;
            }
            if(detail is null)
            {
                var user=store.GetUser(job.Owner);var teacher=Publisher(job.Owner);
                if(user is not{Active:true}||teacher?.Id!=job.Teacher||request.All&&(!user.CanBroadcast||!teacher.CanBroadcast))throw new BridgeFailure("교사 발송 권한이 변경되었습니다.",403);
                var files=JobFiles(job.Id);if(files.Count!=request.AttachmentIds.Length)throw new BridgeFailure("첨부 보관 기간이 만료되었습니다.",409);
                var remoteFiles=new List<string>();
                foreach(var file in files)
                {
                    if(file.Remote is null)
                    {
                        using var verification=store.Open();if(file.Expires<=Now||!StoredFiles.Ready(verification,"external",file.Id,Path.Combine(Files,file.Id)))throw new BridgeFailure("첨부가 만료되었거나 무결성 확인에 실패했습니다.",409);
                        using var form=new MultipartFormDataContent();form.Add(new StringContent(file.Id),"clientId");form.Add(new StreamContent(File.OpenRead(Path.Combine(Files,file.Id))),"file",file.Name);
                        form.Add(new StringContent(request.Kind),"kind");
                        using var upload=await http.PostAsync("bridge/attachments?teacherId="+job.Teacher+"&expiresAt="+(file.Expires??file.Uploaded+2_592_000_000L),form,token);await Check(upload);
                        var remote=await upload.Content.ReadFromJsonAsync<PortalFile>(Json,token)??throw new IOException();
                        store.Execute("UPDATE ExternalFiles SET RemoteId=$remote WHERE Id=$id",("$remote",remote.Id),("$id",file.Id));remoteFiles.Add(remote.Id);
                    }else remoteFiles.Add(file.Remote);
                }
                var current=store.GetUser(job.Owner);
                if(current is not{Active:true}||request.All&&!current.CanBroadcast)throw new BridgeFailure("교사 발송 권한이 변경되었습니다.",403);
                var cancelled=GetJob(job.Id)!;if(cancelled.Withdraw){store.Execute("UPDATE ExternalOutbox SET State='withdrawn',Error=NULL WHERE Id=$id",("$id",job.Id));return;}
                using var publish=await http.PostAsJsonAsync("bridge/announcements",new BridgeAnnouncement(job.Teacher,request with{AttachmentIds=remoteFiles.ToArray()},JsonSerializer.Deserialize<AnnouncementTarget[]>(job.Targets,Json)!),Json,token);await Check(publish);
                var result=await publish.Content.ReadFromJsonAsync<JsonElement>(Json,token);
                detail=await http.GetFromJsonAsync<PortalNoticeDetail>("bridge/announcements/"+result.GetProperty("id").GetString()+"?teacherId="+job.Teacher,Json,token)??throw new IOException();
            }
            store.Execute("UPDATE ExternalOutbox SET State=CASE WHEN Withdraw=1 AND $state!='withdrawn' THEN 'withdraw-pending' ELSE $state END,RemoteId=$remote,Detail=$detail,Error=NULL,Attempts=0,NextAttempt=$next WHERE Id=$id",("$state",detail.Notice.Withdrawn?"withdrawn":"published"),("$remote",detail.Notice.Id),("$detail",JsonSerializer.Serialize(detail,Json)),("$next",Now+(environment.IsDevelopment()?2000:30000)),("$id",job.Id));
            foreach(var file in detail.Attachments)store.Execute("UPDATE ExternalFiles SET ExpiresAt=MIN(COALESCE(ExpiresAt,UploadedAt+2592000000),$expires) WHERE JobId=$job AND RemoteId=$remote",("$expires",file.ExpiresAt),("$job",job.Id),("$remote",file.Id));
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception e)
        {
            var permanent=e is BridgeFailure failure&&failure.Status is >=400 and <500&&failure.Status!=429;
            var message=e is BridgeFailure known?known.Message:"서버 연결 실패. 자동으로 다시 시도합니다.";
            store.Execute("UPDATE ExternalOutbox SET State=$state,Attempts=Attempts+1,NextAttempt=$next,Error=$error WHERE Id=$id",("$state",permanent?"needs-review":job.Withdraw?"withdraw-pending":job.State=="published"?"published":"pending"),("$next",Now+Math.Min(300000,2000L*(1L<<Math.Min(job.Attempts,7)))),("$error",message),("$id",job.Id));
        }
    }
    void Cleanup()
    {
        var expired=store.Query("SELECT Id FROM ExternalFiles WHERE DeletedAt IS NULL AND (ExpiresAt<=$now OR ExpiresAt IS NULL AND UploadedAt<=$cutoff)",r=>r.GetString(0),("$now",Now),("$cutoff",Now-30L*86400000));
        foreach(var id in expired){File.Delete(Path.Combine(Files,id));store.Execute("UPDATE ExternalFiles SET DeletedAt=$now WHERE Id=$id",("$now",Now),("$id",id));}
        var staged=store.Query("SELECT Id FROM ExternalFiles WHERE DeletedAt IS NULL AND JobId IS NULL AND UploadedAt<=$cutoff",r=>r.GetString(0),("$cutoff",Now-86400000));
        foreach(var id in staged){File.Delete(Path.Combine(Files,id));store.Execute("UPDATE ExternalFiles SET DeletedAt=$now WHERE Id=$id",("$now",Now),("$id",id));}
    }
    static async Task Check(HttpResponseMessage response)
    {
        if(response.IsSuccessStatusCode)return;var text="외부 공지 권한·대상·첨부 설정을 확인하세요.";
        try{var value=await response.Content.ReadFromJsonAsync<JsonElement>();if(value.TryGetProperty("error",out var error))text=error.GetString()??text;}catch(JsonException){}
        throw new BridgeFailure(text,(int)response.StatusCode);
    }
    sealed class BridgeFailure(string message,int status):Exception(message){public int Status=>status;}
}

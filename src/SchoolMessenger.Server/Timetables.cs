using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed class Timetables:BackgroundService
{
    readonly Store school;
    readonly ExternalAnnouncements announcements;
    readonly IConfiguration config;
    readonly IHostEnvironment environment;
    readonly ILogger<Timetables> logger;
    readonly IHubContext<MessageHub> hub;
    readonly SemaphoreSlim gate=new(1);
    readonly DeploymentReview review;
    public TimetableData Data{get;}
    string? bridgeError;
    static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    record Draft(string Owner,string Name,TimetableWorkbookData Workbook);
    record Candidate(string Owner,long Revision,TimetableBatch Batch);
    record ManagerChange(string UserId,bool Enabled);
    record HomeroomChange(string ClassId,string TeacherId);
    public Timetables(Store school,ExternalAnnouncements announcements,IConfiguration config,IHostEnvironment environment,ILogger<Timetables> logger,IHubContext<MessageHub> hub,DeploymentReview review)
    {
        this.review=review;
        this.school=school;this.announcements=announcements;this.config=config;this.environment=environment;this.logger=logger;this.hub=hub;
        Data=new(school.Open);Data.Initialize();
        Data.Execute("""
            CREATE TABLE IF NOT EXISTS TimetableManagers(UserId TEXT PRIMARY KEY REFERENCES Users(Id));
            CREATE TABLE IF NOT EXISTS TimetableDrafts(Id TEXT PRIMARY KEY,Owner TEXT NOT NULL,Payload TEXT NOT NULL,ExpiresAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS TimetableCandidates(Id TEXT PRIMARY KEY,Owner TEXT NOT NULL,Payload TEXT NOT NULL,ExpiresAt INTEGER NOT NULL);
            """);
    }
    bool Manager(string id)=>school.GetUser(id) is{Active:true} user&&(user.IsAdmin||Data.Query("SELECT 1 FROM TimetableManagers WHERE UserId=$id",r=>r.GetInt32(0),("$id",id)).Count>0);
    static string User(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    PortalDirectory Directory()=>announcements.TimetableDirectory??throw new FilePolicyException("학교 소식 서버의 학급 명부 연결을 확인하세요.",503);
    static IResult Error(FilePolicyException e)=>Results.Json(new{error=e.Message},statusCode:e.Status);
    static IResult Safe(Func<IResult> action){try{return action();}catch(FilePolicyException e){return Error(e);}}
    public static void Map(WebApplication app,Timetables service)
    {
        var api=app.MapGroup("/api/timetable").RequireAuthorization();
        api.MapGet("/setup",(HttpContext c)=>Safe(()=>Results.Ok(new{canManage=service.Manager(User(c)),isAdmin=service.school.GetUser(User(c))!.IsAdmin,
            classes=service.Directory().Classes,teachers=service.school.Users().Where(u=>u.Active).Select(u=>new{u.Id,u.Name,u.Department,u.Username}),homerooms=service.Data.Homerooms(),
            revision=service.Data.Revision,pending=service.Data.Query("SELECT COUNT(*) FROM TimetableBatches WHERE Delivered=0",r=>r.GetInt32(0))[0],error=service.bridgeError})));
        api.MapGet("/day",(string? date,string? classId,bool? mine,HttpContext c)=>Safe(()=>
        {
            var rooms=service.Directory().Classes;if(classId is not null&&!rooms.Any(r=>r.Id==classId))return Results.NotFound();
            var day=service.Data.Day(date??TimetableData.Today,classId is null?rooms.Select(r=>r.Id).ToArray():[classId]);
            if(mine==true)day=day with{Slots=day.Slots.Where(s=>s.TeacherId==User(c)).ToArray()};return Results.Ok(day);
        }));
        api.MapGet("/notices",(HttpContext c)=>Results.Ok(service.Data.Notices(User(c))));
        api.MapPost("/notices/{revision:long}/read",(long revision,HttpContext c)=>{service.Data.Execute("UPDATE TimetableNotices SET ReadAt=COALESCE(ReadAt,$now) WHERE Revision=$rev AND UserId=$user",("$now",Now),("$rev",revision),("$user",User(c)));return Results.Ok();});
        api.MapGet("/history",(HttpContext c)=>Safe(()=>{service.RequireManager(User(c));return Results.Ok(service.Data.Batches().OrderByDescending(b=>b.Revision).Take(200).Select(b=>new{b.Id,b.Revision,b.Kind,b.Start,b.End,b.Date,b.FileName,b.PublishedAt,b.Withdrawn,classes=b.Cells.Select(s=>s.ClassName).Distinct()}));}));
        api.MapPost("/import",async Task<IResult>(HttpContext c)=>
        {
            try{return await service.Import(c);}catch(FilePolicyException e){return Error(e);}
        }).RequireRateLimiting("upload");
        api.MapPost("/preview",async Task<IResult>(TimetablePreviewRequest request,HttpContext c)=>
        {await service.gate.WaitAsync(c.RequestAborted);try{return Safe(()=>service.Preview(request,User(c)));}finally{service.gate.Release();}});
        api.MapPost("/publish",async Task<IResult>(TimetableCommit request,HttpContext c)=>
        {await service.gate.WaitAsync(c.RequestAborted);try{return await service.Publish(request,User(c));}catch(FilePolicyException e){return Error(e);}finally{service.gate.Release();}});
        api.MapPost("/{id}/cancel",async Task<IResult>(string id,TimetableCommit request,HttpContext c)=>
        {await service.gate.WaitAsync(c.RequestAborted);try{return await service.Cancel(id,request.ClientId,User(c));}catch(FilePolicyException e){return Error(e);}finally{service.gate.Release();}});
        app.MapGet("/api/admin/timetable-managers",()=>service.school.Users().Where(u=>u.Active).Select(u=>new{u.Id,u.Name,u.Username,u.Department,enabled=service.Manager(u.Id),u.IsAdmin})).RequireAuthorization("Admin");
        app.MapPost("/api/admin/timetable-managers",(ManagerChange request)=>Safe(()=>
        {
            if(service.school.GetUser(request.UserId) is not{Active:true})throw new FilePolicyException("활성 교직원을 선택하세요.");
            service.Data.Execute(request.Enabled?"INSERT OR IGNORE INTO TimetableManagers VALUES($id)":"DELETE FROM TimetableManagers WHERE UserId=$id",("$id",request.UserId));return Results.Ok();
        })).RequireAuthorization("Admin");
        app.MapPost("/api/admin/timetable-homerooms",(HomeroomChange request)=>Safe(()=>
        {
            if(!service.Directory().Classes.Any(c=>c.Id==request.ClassId)||service.school.GetUser(request.TeacherId) is not{Active:true})throw new FilePolicyException("학급과 활성 담임 계정을 확인하세요.");
            service.Data.Execute("INSERT INTO TimetableHomerooms VALUES($class,$teacher) ON CONFLICT(ClassId) DO UPDATE SET TeacherId=excluded.TeacherId",("$class",request.ClassId),("$teacher",request.TeacherId));return Results.Ok();
        })).RequireAuthorization("Admin");
    }
    void RequireManager(string user){if(!Manager(user))throw new FilePolicyException("수업 담당자 권한이 필요합니다.",403);}
    async Task<IResult> Import(HttpContext c)
    {
        var owner=User(c);RequireManager(owner);Directory();
        if(!c.Request.HasFormContentType)throw new FilePolicyException("엑셀 파일을 선택하세요.");
        var form=await c.Request.ReadFormAsync(c.RequestAborted);var file=form.Files.GetFile("file");
        if(file is null||form.Files.Count!=1||file.Length is <1 or >10_485_760)throw new FilePolicyException("엑셀은 10MB 이하의 파일 하나를 올려주세요.",413);
        var name=FilePolicy.Name(file.FileName);if(Path.GetExtension(name).ToLowerInvariant()!=".xlsx")throw new FilePolicyException("제공한 양식의 .xlsx 파일을 선택하세요.");
        var path=Path.Combine(school.Root,"timetable-"+Guid.NewGuid().ToString("N")+".xlsx");
        try
        {
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))await file.CopyToAsync(output,c.RequestAborted);
            await FilePolicy.Scan(path,name,environment.IsDevelopment(),config["School:ScannerPath"],c.RequestAborted);
            var parsed=TimetableWorkbook.Read(path,form["kind"].ToString());RequireManager(owner);
            var id=Guid.NewGuid().ToString("N");Data.Execute("DELETE FROM TimetableDrafts WHERE ExpiresAt<=$now",("$now",Now));Data.Execute("DELETE FROM TimetableCandidates WHERE ExpiresAt<=$now",("$now",Now));
            if(Data.Query("SELECT COUNT(*) FROM TimetableDrafts WHERE Owner=$owner",r=>r.GetInt32(0),("$owner",owner))[0]>=20)throw new FilePolicyException("미게시 시간표가 많습니다. 기존 미리보기를 게시하거나 만료 후 다시 시도하세요.",429);
            Data.Execute("INSERT INTO TimetableDrafts VALUES($id,$owner,$payload,$expires)",("$id",id),("$owner",owner),("$payload",JsonSerializer.Serialize(new Draft(owner,name,parsed),TimetableData.Json)),("$expires",Now+86_400_000));
            var rooms=Directory().Classes;var teachers=school.Users().Where(u=>u.Active).ToArray();
            return Results.Ok(new{draftId=id,workbook=parsed,classes=parsed.Cells.Select(s=>s.ClassKey).Distinct().Select(s=>new{source=s,candidates=rooms.Where(c=>Canonical(c.Name)==Canonical(s)).Select(c=>c.Id)}),teachers=parsed.Cells.Where(s=>s.Teacher.Length>0).Select(s=>s.Teacher).Distinct().Select(s=>new{source=s,candidates=teachers.Where(t=>t.Name==s).Select(t=>t.Id)})});
        }
        finally{File.Delete(path);}
    }
    static string Canonical(string text)=>string.Concat(text.Where(c=>!char.IsWhiteSpace(c)));
    T Load<T>(string table,string id,string user) where T:class=>Data.Query("SELECT Payload FROM "+table+" WHERE Id=$id AND Owner=$owner AND ExpiresAt>$now",r=>JsonSerializer.Deserialize<T>(r.GetString(0),TimetableData.Json)!, ("$id",id),("$owner",user),("$now",Now)).FirstOrDefault()??throw new FilePolicyException("미리보기가 만료되었습니다. 파일을 다시 선택하세요.",409);
    IResult Preview(TimetablePreviewRequest request,string owner)
    {
        RequireManager(owner);if(request.Classes is null||request.Teachers is null||request.Classes.Length>100||request.Teachers.Length>500||request.Classes.Any(c=>c is null||string.IsNullOrEmpty(c.Source))||request.Teachers.Any(c=>c is null||string.IsNullOrEmpty(c.Source))||request.Classes.GroupBy(c=>c.Source).Any(g=>g.Count()>1)||request.Teachers.GroupBy(t=>t.Source).Any(g=>g.Count()>1))throw new FilePolicyException("학급·교사 연결을 확인하세요.");
        var draft=Load<Draft>("TimetableDrafts",request.DraftId,owner);var workbook=draft.Workbook;var directory=Directory();var users=school.Users().Where(u=>u.Active).ToArray();
        var classMap=request.Classes.ToDictionary(c=>c.Source,c=>c.Id);var teacherMap=request.Teachers.ToDictionary(t=>t.Source,t=>t.Id);
        var cells=workbook.Cells.Select(c=>
        {
            var room=directory.Classes.FirstOrDefault(r=>r.Id==classMap.GetValueOrDefault(c.ClassKey));if(room is null||room.Grade!=c.Grade)throw new FilePolicyException("학급 연결을 확인하세요: "+c.ClassKey);
            var teacher=c.Teacher.Length==0?null:users.FirstOrDefault(u=>u.Id==teacherMap.GetValueOrDefault(c.Teacher));if(c.Teacher.Length>0&&teacher is null)throw new FilePolicyException("교직원 계정을 연결하세요: "+c.Teacher);
            return new TimetableCell(room.Id,room.Name,c.Day,c.Period,c.Subject,teacher?.Id??"",teacher?.Name??"");
        }).ToArray();
        var start=workbook.Kind=="daily"?workbook.Date!:request.Start;var end=workbook.Kind=="daily"?workbook.Date!:request.End;
        var batch=new TimetableBatch(Guid.NewGuid().ToString("N"),Data.Revision+1,owner,"00000000000000000000000000000000",workbook.Kind,workbook.Year,workbook.Semester,start,end,workbook.Date,draft.Name,Now,false,cells);TimetableData.Validate(batch);ValidateLinks(batch);
        if(batch.Kind=="base")ValidatePeriod(batch);
        var changes=Changes(batch);var candidateId=Guid.NewGuid().ToString("N");
        Data.Execute("INSERT INTO TimetableCandidates VALUES($id,$owner,$payload,$expires)",("$id",candidateId),("$owner",owner),("$payload",JsonSerializer.Serialize(new Candidate(owner,Data.Revision,batch),TimetableData.Json)),("$expires",Now+900_000));
        return Results.Ok(new{candidateId,batch,changes,changedCount=changes.Length,homerooms=Data.Homerooms().Where(h=>cells.Any(s=>s.ClassId==h.ClassId))});
    }
    void ValidateLinks(TimetableBatch batch)
    {
        var rooms=Directory().Classes;var users=school.Users();
        if(batch.Cells.Any(c=>!rooms.Any(r=>r.Id==c.ClassId)||c.TeacherId.Length>0&&!users.Any(u=>u.Id==c.TeacherId&&u.Active)))throw new FilePolicyException("학급·교사 연결이 변경되었습니다. 다시 미리보기하세요.",409);
        var homes=Data.Homerooms();if(batch.Cells.Select(c=>c.ClassId).Distinct().Any(id=>!homes.Any(h=>h.ClassId==id&&users.Any(u=>u.Id==h.TeacherId&&u.Active))))throw new FilePolicyException("관리자가 해당 학급의 담임 계정을 먼저 연결해야 합니다.",409);
    }
    void ValidatePeriod(TimetableBatch batch)
    {
        var previous=Data.Batches();foreach(var room in batch.Cells.Select(c=>c.ClassId).Distinct())
        {
            var active=previous.Where(b=>b.Kind=="base"&&b.Cells.Any(c=>c.ClassId==room)).GroupBy(b=>(b.Start,b.End)).Select(g=>g.MaxBy(b=>b.Revision)!).Where(b=>!b.Withdrawn);
            if(active.Any(b=>string.CompareOrdinal(b.Start,batch.End)<=0&&string.CompareOrdinal(b.End,batch.Start)>=0&&(b.Start!=batch.Start||b.End!=batch.End)))throw new FilePolicyException("기초시간표 적용 기간이 겹칩니다. 기존 기간을 취소하거나 같은 기간으로 교체하세요.",409);
        }
    }
    record Change(string Date,string ClassId,string ClassName,int Period,string BeforeSubject,string BeforeTeacher,string BeforeTeacherId,string Subject,string Teacher,string TeacherId);
    Change[] Changes(TimetableBatch batch)
    {
        var rows=Data.Batches();var rooms=batch.Cells.Select(c=>c.ClassId).Distinct().ToArray();var result=new List<Change>();var first=TimetableData.Date(batch.Start);var days=batch.Kind=="daily"?1:Math.Min(7,TimetableData.Date(batch.End).DayNumber-first.DayNumber+1);
        for(var n=0;n<days;n++)
        {
            var day=first.AddDays(n).ToString("yyyy-MM-dd");var before=TimetableData.Resolve(rows,day,rooms).Slots;var after=TimetableData.Resolve(rows.Append(batch),day,rooms).Slots;
            foreach(var room in rooms)for(var p=1;p<=12;p++)
            {
                var a=before.SingleOrDefault(c=>c.ClassId==room&&c.Period==p);var b=after.SingleOrDefault(c=>c.ClassId==room&&c.Period==p);
                if((a?.Subject??"")== (b?.Subject??"")&&(a?.TeacherId??"")==(b?.TeacherId??""))continue;
                result.Add(new(day,room,(b??a)!.ClassName,p,a?.Subject??"",a?.TeacherName??"",a?.TeacherId??"",b?.Subject??"",b?.TeacherName??"",b?.TeacherId??""));
            }
        }
        return result.ToArray();
    }
    TimetableNotice[] Notifications(TimetableBatch batch,Change[] changes)
    {
        var directory=Directory();var classes=changes.Select(c=>c.ClassId).Distinct().ToArray();var recipients=new Dictionary<string,HashSet<string>>();
        void Add(string user,string room){if(user.Length==0)return;if(!recipients.TryGetValue(user,out var scope))recipients[user]=scope=[];scope.Add(room);}
        foreach(var change in changes){Add(change.BeforeTeacherId,change.ClassId);Add(change.TeacherId,change.ClassId);}
        foreach(var home in Data.Homerooms().Where(h=>classes.Contains(h.ClassId)))if(school.GetUser(home.TeacherId) is{Active:true})Add(home.TeacherId,home.ClassId);
        foreach(var (user,scope) in recipients.ToArray())foreach(var teacher in directory.People.Where(p=>p.Role=="teacher"&&p.InternalUserId==user))foreach(var room in scope)Add(teacher.Id,room);
        foreach(var student in directory.People.Where(p=>p.Role=="student"&&p.ClassId is not null&&classes.Contains(p.ClassId)))
        {Add(student.Id,student.ClassId!);foreach(var parent in directory.People.Where(p=>p.Role=="parent"&&p.Children.Contains(student.Id)))Add(parent.Id,student.ClassId!);}
        return recipients.Select(r=>new TimetableNotice(batch.Revision,r.Key,batch.Start,r.Value.ToArray(),batch.Withdrawn?"시간표 등록이 취소되었습니다":"학교 시간표가 변경되었습니다",batch.PublishedAt)).ToArray();
    }
    async Task<IResult> Publish(TimetableCommit request,string owner)
    {
        RequireManager(owner);if(!Guid.TryParseExact(request.ClientId,"N",out _))throw new FilePolicyException("게시 요청 번호를 확인하세요.");
        var candidate=Load<Candidate>("TimetableCandidates",request.CandidateId,owner);
        if(Data.ByClient(owner,request.ClientId) is{} existing){if(existing.Id!=candidate.Batch.Id)throw new FilePolicyException("같은 요청 번호로 다른 시간표를 게시할 수 없습니다.",409);return Results.Ok(existing);}
        if(candidate.Revision!=Data.Revision)throw new FilePolicyException("다른 시간표가 게시되었습니다. 변경 전후를 다시 미리보기하세요.",409);
        var batch=candidate.Batch with{ClientId=request.ClientId,PublishedAt=Now};ValidateLinks(batch);if(batch.Kind=="base")ValidatePeriod(batch);
        return await Commit(batch);
    }
    async Task<IResult> Cancel(string id,string client,string owner)
    {
        RequireManager(owner);if(!Guid.TryParseExact(client,"N",out _))throw new FilePolicyException("취소 요청 번호를 확인하세요.");
        if(Data.ByClient(owner,client) is{} existing){if(existing.FileName!="취소:"+id)throw new FilePolicyException("취소 요청 번호가 중복입니다.",409);return Results.Ok(existing);}
        var rows=Data.Batches();var original=rows.FirstOrDefault(b=>b.Id==id);if(original is null||original.Withdrawn)throw new FilePolicyException("취소할 게시본이 없습니다.",404);
        if(original.Cells.Select(c=>c.ClassId).Distinct().Any(room=>rows.Where(b=>b.Kind==original.Kind&&(b.Kind=="daily"?b.Date==original.Date:b.Start==original.Start&&b.End==original.End)&&b.Cells.Any(c=>c.ClassId==room)).MaxBy(b=>b.Revision)?.Id!=id))throw new FilePolicyException("일부 학급의 새 게시본이 있습니다. 현재 게시본을 확인하세요.",409);
        var batch=original with{Id=Guid.NewGuid().ToString("N"),Revision=Data.Revision+1,Owner=owner,ClientId=client,PublishedAt=Now,Withdrawn=true,FileName="취소:"+id};return await Commit(batch);
    }
    async Task<IResult> Commit(TimetableBatch batch)
    {
        ValidateLinks(batch);
        var notices=Notifications(batch,Changes(batch));var saved=Data.Insert(batch,notices);
        // Persistence succeeds independently of connected desktops; their next refresh also reads the durable notices.
        foreach(var target in notices.Where(n=>school.GetUser(n.UserId) is{Active:true}))
            try{await hub.Clients.Group(target.UserId).SendAsync("TimetableChanged",saved.Start,saved.Revision);}catch{ /* The durable notice remains available after reconnection. */ }
        return Results.Ok(saved);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HttpClient? http=null;
        if(Uri.TryCreate(config["Announcements:Address"],UriKind.Absolute,out var address))
        {http=new(new BridgeSigningHandler(config,review)){BaseAddress=new Uri(address.AbsoluteUri.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(60)};}
        try{while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(http is not null&&announcements.TimetableDirectory is{} directory)
                foreach(var batch in Data.Query("SELECT Payload FROM TimetableBatches WHERE Delivered=0 ORDER BY Revision LIMIT 10",r=>JsonSerializer.Deserialize<TimetableBatch>(r.GetString(0),TimetableData.Json)!))
                {
                    var notices=Data.BatchNotices(batch.Revision).Where(n=>directory.People.Any(p=>p.Id==n.UserId)).ToArray();
                    using var response=await http.PostAsJsonAsync("bridge/timetable",new TimetableDelivery(batch,Data.Homerooms(),notices),TimetableData.Json,stoppingToken);
                    if(!response.IsSuccessStatusCode){bridgeError="앱 시간표 전달 대기: 외부 서버 연결·명부·버전을 확인하세요.";break;}
                    Data.Execute("UPDATE TimetableBatches SET Delivered=1 WHERE Id=$id",("$id",batch.Id));bridgeError=null;
                }
            }
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){bridgeError="앱 시간표 전달 대기: 외부 연결을 확인하세요.";logger.LogWarning("Timetable delivery unavailable ({Type})",e.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(environment.IsDevelopment()?2:15),stoppingToken);}catch(OperationCanceledException){break;}
        }}finally{http?.Dispose();}
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed partial class Timetables
{
    readonly Store school;
    readonly IConfiguration config;
    readonly IHostEnvironment environment;
    readonly IHubContext<MessageHub> hub;
    readonly SemaphoreSlim gate=new(1);
    readonly DeploymentReview review;
    public TimetableData Data{get;}
    static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    record Draft(string Owner,string Name,TimetableWorkbookData Workbook);
    record Candidate(string Owner,long Revision,TimetableBatch[] Batches);
    record ManagerChange(string UserId,bool Enabled);
    record HomeroomChange(string ClassId,string TeacherId);
    record FeatureChange(bool? Enabled);
    bool enabled;
    bool Enabled => Volatile.Read(ref enabled);
    internal bool FeatureEnabled => Enabled;
    internal Task<IResult> SetFeatureEnabled(bool value, HttpContext c) => SetFeature(new(value), c);
    public Timetables(Store school,IConfiguration config,IHostEnvironment environment,IHubContext<MessageHub> hub,DeploymentReview review)
    {
        this.review=review;
        this.school=school;this.config=config;this.environment=environment;this.hub=hub;
        Data=new(school.Open);Data.Initialize();InitializeSwaps();
        Data.Execute("""
            CREATE TABLE IF NOT EXISTS TimetableManagers(UserId TEXT PRIMARY KEY REFERENCES Users(Id));
            CREATE TABLE IF NOT EXISTS TimetableDrafts(Id TEXT PRIMARY KEY,Owner TEXT NOT NULL,Payload TEXT NOT NULL,ExpiresAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS TimetableCandidates(Id TEXT PRIMARY KEY,Owner TEXT NOT NULL,Payload TEXT NOT NULL,ExpiresAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS TimetableClasses(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Grade INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS TimetableFeature(Id INTEGER PRIMARY KEY CHECK(Id=1),Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)));
            """);
        enabled = Data.Query("SELECT Enabled FROM TimetableFeature WHERE Id=1", r=>r.GetInt32(0)!=0).SingleOrDefault(true);
    }
    bool Manager(string id)=>school.GetUser(id) is{Active:true} user&&(user.IsAdmin||Data.Query("SELECT 1 FROM TimetableManagers WHERE UserId=$id",r=>r.GetInt32(0),("$id",id)).Count>0);
    static string User(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    record SchoolClasses(SchoolClass[] Classes);
    SchoolClasses Directory()=>new(Data.Query("SELECT Id,Name,Grade FROM TimetableClasses ORDER BY Grade,Name",r=>new SchoolClass(r.GetString(0),r.GetString(1),r.GetInt32(2))).ToArray());
    static IResult Error(FilePolicyException e)=>Results.Json(new{error=e.Message},statusCode:e.Status);
    static IResult Safe(Func<IResult> action){try{return action();}catch(FilePolicyException e){return Error(e);}}
    public static void Map(WebApplication app,Timetables service)
    {
        app.MapGet("/api/timetable/status", () => Results.Ok(new { enabled=service.Enabled })).RequireAuthorization();
        // Older clients poll this at sign-in. An empty list lets them connect during a staged upgrade.
        app.MapGet("/api/timetable/notices", (HttpContext c) => Results.Ok(service.Enabled ? service.Data.Notices(User(c)) : [])).RequireAuthorization();
        app.MapPost("/api/admin/timetable-feature", (FeatureChange change, HttpContext c) => service.SetFeature(change,c)).RequireAuthorization("Admin");
        var api=app.MapGroup("/api/timetable").RequireAuthorization();
        api.AddEndpointFilter((context,next) => service.Enabled ? next(context) : ValueTask.FromResult<object?>(Error(Disabled())));
        service.MapSwaps(api);
        api.MapGet("/setup",(HttpContext c)=>Safe(()=>Results.Ok(new{canManage=service.Manager(User(c)),isAdmin=service.school.GetUser(User(c))!.IsAdmin,
            classes=service.Directory().Classes,teachers=service.school.Users().Where(u=>u.Active).Select(u=>new{u.Id,u.Name,u.Department,u.Username}),homerooms=service.Homerooms(),myClasses=service.Homerooms().Where(h=>h.TeacherId==User(c)).Select(h=>h.ClassId),
            revision=service.Data.Revision,pending=0,error=(string?)null})));
        api.MapGet("/day",(string? date,string? classId,bool? mine,HttpContext c)=>Safe(()=>
        {
            var rooms=service.Directory().Classes;if(classId is not null&&!rooms.Any(r=>r.Id==classId))return Results.NotFound();
            var day=service.Data.Day(date??TimetableData.Today,classId is null?rooms.Select(r=>r.Id).ToArray():[classId]);
            if(mine==true){var me=service.UniqueName(User(c));day=day with{Slots=day.Slots.Where(s=>s.TeacherId==User(c)||s.TeacherId.Length==0&&s.TeacherName==me).ToArray()};}return Results.Ok(day);
        }));
        api.MapPost("/notices/{revision:long}/read",(long revision,HttpContext c)=>service.WithSchedule(c,()=>{service.Data.Execute("UPDATE TimetableNotices SET ReadAt=COALESCE(ReadAt,$now) WHERE Revision=$rev AND UserId=$user",("$now",Now),("$rev",revision),("$user",User(c)));return Results.Ok();}));
        api.MapGet("/history",(HttpContext c)=>Safe(()=>{service.RequireManager(User(c));return Results.Ok(service.Data.Batches().OrderByDescending(b=>b.Revision).Take(200).Select(b=>new{b.Id,b.Revision,b.Kind,b.Start,b.End,b.Date,b.FileName,b.PublishedAt,b.Withdrawn,approvedSwapCount=b.ApprovedSwapIds?.Length??0,classes=b.Cells.Select(s=>s.ClassName).Distinct()}));}));
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
    static FilePolicyException Disabled()=>new("이 학교에서는 교무톡 시간표를 사용하지 않습니다.",403);
    void RequireEnabled(){if(!Enabled)throw Disabled();}
    void RequireManager(string user){RequireEnabled();if(!Manager(user))throw new FilePolicyException("시간표 담당자 권한이 필요합니다.",403);}
    async Task<IResult> SetFeature(FeatureChange change,HttpContext c)
    {
        if(change.Enabled is not bool value)return Results.BadRequest(new { error="시간표 사용 여부를 선택하세요." });
        await gate.WaitAsync(c.RequestAborted);
        try
        {
            if(Enabled==value)return Results.Ok(new { enabled=value });
            using var db=school.Open();using var tx=db.BeginTransaction();
            using var command=Store.Command(db,"INSERT INTO TimetableFeature VALUES(1,$enabled) ON CONFLICT(Id) DO UPDATE SET Enabled=excluded.Enabled",tx,("$enabled",value?1:0));
            command.ExecuteNonQuery();StaffAccounts.Audit(db,tx,User(c),value?"timetable.enable":"timetable.disable","timetable");tx.Commit();
            Volatile.Write(ref enabled,value);
        }
        finally{gate.Release();}
        try{await hub.Clients.All.SendAsync("TimetableAvailabilityChanged");}catch{ /* Clients also check the saved setting. */ }
        return Results.Ok(new { enabled=value });
    }
    // A name the file can stand for: with two active people of that name it cannot say which one is meant.
    string? UniqueName(string user)
    {
        var name=school.GetUser(user)?.Name;return name is not null&&school.Users().Count(u=>u.Active&&u.Name==name)==1?name:null;
    }
    // Set by an administrator, or else read from the timetable: NEIS gives a class's self-governance hours to its homeroom teacher.
    TimetableHomeroom[] Homerooms()
    {
        var set=Data.Homerooms().ToDictionary(h=>h.ClassId);var users=school.Users().Where(u=>u.Active).ToArray();var batches=Data.Batches().Where(b=>b.Kind=="base").ToArray();
        foreach(var room in batches.SelectMany(b=>b.Cells).Select(c=>c.ClassId).Distinct().Where(id=>!set.ContainsKey(id)).ToArray())
        {
            var latest=batches.Where(b=>b.Cells.Any(c=>c.ClassId==room)).MaxBy(b=>b.Revision)!;if(latest.Withdrawn)continue;
            var top=latest.Cells.Where(c=>c.ClassId==room&&c.Subject.StartsWith("자율",StringComparison.Ordinal)).GroupBy(c=>(c.TeacherId,c.TeacherName)).MaxBy(g=>g.Count())?.Key;if(top is null)continue;
            var id=top.Value.TeacherId.Length>0?top.Value.TeacherId:users.Where(u=>u.Name==top.Value.TeacherName).Take(2).ToArray() is{Length:1} found?found[0].Id:null;
            if(id is not null)set[room]=new(room,id);
        }
        return set.Values.ToArray();
    }
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
            var kind=form["kind"].ToString();var parsed=TimetableWorkbook.Read(path,kind.Length==0?"auto":kind);
            return await WithSchedule(c,()=>
            {
                RequireManager(owner);
                foreach(var room in parsed.Cells.DistinctBy(c=>Canonical(c.ClassKey)))Data.Execute("INSERT OR IGNORE INTO TimetableClasses VALUES($id,$name,$grade)",("$id",Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Canonical(room.ClassKey))))[..32]),("$name",room.ClassKey),("$grade",room.Grade));
                var id=Guid.NewGuid().ToString("N");Data.Execute("DELETE FROM TimetableDrafts WHERE ExpiresAt<=$now",("$now",Now));Data.Execute("DELETE FROM TimetableCandidates WHERE ExpiresAt<=$now",("$now",Now));
                if(Data.Query("SELECT COUNT(*) FROM TimetableDrafts WHERE Owner=$owner",r=>r.GetInt32(0),("$owner",owner))[0]>=20)throw new FilePolicyException("미게시 시간표가 많습니다. 기존 미리보기를 게시하거나 만료 후 다시 시도하세요.",429);
                Data.Execute("INSERT INTO TimetableDrafts VALUES($id,$owner,$payload,$expires)",("$id",id),("$owner",owner),("$payload",JsonSerializer.Serialize(new Draft(owner,name,parsed),TimetableData.Json)),("$expires",Now+86_400_000));
                var rooms=Directory().Classes;var teachers=school.Users().Where(u=>u.Active).ToArray();
                return Results.Ok(new{draftId=id,workbook=parsed,classes=parsed.Cells.Select(s=>s.ClassKey).Distinct().Select(s=>new{source=s,candidates=rooms.Where(c=>Canonical(c.Name)==Canonical(s)).Select(c=>c.Id)}),teachers=parsed.Cells.Where(s=>s.Teacher.Length>0).Select(s=>s.Teacher).Distinct().Select(s=>new{source=s,candidates=teachers.Where(t=>t.Name==s).Select(t=>t.Id)})});
            });
        }
        finally{File.Delete(path);}
    }
    static string Canonical(string text)=>string.Concat(text.Where(c=>!char.IsWhiteSpace(c)));
    T Load<T>(string table,string id,string user) where T:class=>Data.Query("SELECT Payload FROM "+table+" WHERE Id=$id AND Owner=$owner AND ExpiresAt>$now",r=>JsonSerializer.Deserialize<T>(r.GetString(0),TimetableData.Json)!, ("$id",id),("$owner",user),("$now",Now)).FirstOrDefault()??throw new FilePolicyException("미리보기가 만료되었습니다. 파일을 다시 선택하세요.",409);
    IResult Preview(TimetablePreviewRequest request,string owner)
    {
        RequireManager(owner);if(request.Classes is null||request.Teachers is null||request.Classes.Length>100||request.Teachers.Length>500||request.Classes.Any(c=>c is null||string.IsNullOrEmpty(c.Source))||request.Teachers.Any(c=>c is null||string.IsNullOrEmpty(c.Source))||request.Classes.GroupBy(c=>c.Source).Any(g=>g.Count()>1)||request.Teachers.GroupBy(t=>t.Source).Any(g=>g.Count()>1))throw new FilePolicyException("학급·교사 연결을 확인하세요.");
        var ids=(request.DraftIds is{Length:>0} many?many:[request.DraftId]).Distinct().ToArray();if(ids.Length>30)throw new FilePolicyException("한 번에 30개 파일까지 올릴 수 있습니다.");
        var drafts=ids.Select(id=>Load<Draft>("TimetableDrafts",id,owner)).ToArray();var directory=Directory();var users=school.Users().Where(u=>u.Active).ToArray();
        var classMap=request.Classes.ToDictionary(c=>c.Source,c=>c.Id);var teacherMap=request.Teachers.ToDictionary(t=>t.Source,t=>t.Id);
        SchoolClass Room(TimetableRawCell c)
        {
            var room=classMap.TryGetValue(c.ClassKey,out var linked)?directory.Classes.FirstOrDefault(r=>r.Id==linked):directory.Classes.Where(r=>Canonical(r.Name)==Canonical(c.ClassKey)).Take(2).ToArray() is{Length:1} found?found[0]:null;
            return room is not null&&room.Grade==c.Grade?room:throw new FilePolicyException("학급 연결을 확인하세요: "+c.ClassKey);
        }
        // A teacher without an account still shows by name; an account made later is matched by that name.
        TimetableCell Cell(TimetableRawCell c)
        {
            var room=Room(c);if(c.Teacher.Length==0)return new(room.Id,room.Name,c.Day,c.Period,c.Subject,"","");
            var teacher=teacherMap.TryGetValue(c.Teacher,out var linked)?users.FirstOrDefault(u=>u.Id==linked):users.Where(u=>u.Name==c.Teacher).Take(2).ToArray() is{Length:1} found?found[0]:null;
            return new(room.Id,room.Name,c.Day,c.Period,c.Subject,teacher?.Id??"",teacher?.Name??c.Teacher);
        }
        var seen=Data.Batches().ToList();var planned=new List<TimetableBatch>();var changes=new List<Change>();
        void Plan(string kind,int year,int semester,string start,string end,string? date,string file,TimetableCell[] cells)
        {
            var batch=new TimetableBatch(Guid.NewGuid().ToString("N"),Data.Revision+1+planned.Count,owner,"00000000000000000000000000000000",kind,year,semester,start,end,date,file[..Math.Min(file.Length,200)],Now,false,cells);
            TimetableData.Validate(batch);ValidateLinks(batch);if(kind=="base")ValidatePeriod(batch,seen);
            changes.AddRange(Changes(batch,seen));planned.Add(batch);seen.Add(batch);
        }
        foreach(var draft in drafts.Where(d=>d.Workbook.Kind=="base"))
        {
            var book=draft.Workbook;var first=book.Semester==1?$"{book.Year}-03-01":$"{book.Year}-08-01";var last=book.Semester==1?$"{book.Year}-07-31":$"{book.Year+1}-02-28";
            Plan("base",book.Year,book.Semester,request.Start is{Length:>0} from?from:first,request.End is{Length:>0} to?to:last,null,draft.Name,book.Cells.Select(Cell).ToArray());
        }
        // One batch per school day, whichever files and grades it came from. A later file for the same class and day wins.
        var named=drafts.SelectMany(d=>d.Workbook.Cells.Select(c=>(Draft:d,Raw:c with{Date=c.Date??d.Workbook.Date}))).ToArray();
        var covered=new HashSet<(string,string)>();
        foreach(var day in named.Where(x=>x.Draft.Workbook.Kind=="daily").GroupBy(x=>x.Raw.Date!).OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            var book=day.First().Draft.Workbook;var cells=day.Reverse().DistinctBy(x=>(x.Raw.ClassKey,x.Raw.Period)).Select(x=>Cell(x.Raw)).ToArray();foreach(var c in cells)covered.Add((c.ClassId,day.Key));
            Plan("daily",book.Year,book.Semester,day.Key,day.Key,day.Key,string.Join(", ",day.Select(x=>x.Draft.Name).Distinct()),cells);
        }
        // A changed-lessons file lists only what moved: it lays over what that day already is.
        foreach(var day in named.Where(x=>x.Draft.Workbook.Kind=="changes").GroupBy(x=>x.Raw.Date!).OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            var overlay=day.Select(x=>Cell(x.Raw)).Where(c=>!covered.Contains((c.ClassId,day.Key))).ToArray();if(overlay.Length==0)continue;
            var rooms=overlay.Select(c=>c.ClassId).Distinct().ToArray();var current=TimetableData.Resolve(seen,day.Key,rooms);
            if(current.MissingClasses.Length>0)throw new FilePolicyException("변경 수업을 반영할 기초시간표가 없습니다. 기초시간표를 함께 올려주세요.",409);
            var weekday=overlay[0].Day;var cells=current.Slots.Where(s=>s.Subject.Length>0&&!overlay.Any(c=>c.ClassId==s.ClassId&&c.Period==s.Period)).Select(s=>new TimetableCell(s.ClassId,s.ClassName,weekday,s.Period,s.Subject,s.TeacherId,s.TeacherName)).Concat(overlay).ToArray();
            var book=day.First().Draft.Workbook;Plan("daily",book.Year,book.Semester,day.Key,day.Key,day.Key,string.Join(", ",day.Select(x=>x.Draft.Name).Distinct()),cells);
        }
        if(planned.Count==0)throw new FilePolicyException("반영할 시간표가 없습니다. 변경 수업이 없는 파일입니다.");
        var candidateId=Guid.NewGuid().ToString("N");
        Data.Execute("INSERT INTO TimetableCandidates VALUES($id,$owner,$payload,$expires)",("$id",candidateId),("$owner",owner),("$payload",JsonSerializer.Serialize(new Candidate(owner,Data.Revision,planned.ToArray()),TimetableData.Json)),("$expires",Now+900_000));
        var unlinked=planned.SelectMany(b=>b.Cells).Where(c=>c.TeacherId.Length==0&&c.TeacherName.Length>0).Select(c=>c.TeacherName).Distinct().Order().ToArray();
        var classes=planned.SelectMany(b=>b.Cells).Select(c=>c.ClassId).ToHashSet();
        return Results.Ok(new{candidateId,batch=planned[0],batches=planned.Select(b=>new{b.Kind,b.Start,b.End,b.FileName}),changes=changes.OrderBy(c=>c.Date,StringComparer.Ordinal).ThenBy(c=>c.ClassName,StringComparer.Ordinal).ThenBy(c=>c.Period),changedCount=changes.Count,unlinked,homerooms=Data.Homerooms().Where(h=>classes.Contains(h.ClassId))});
    }
    void ValidateLinks(TimetableBatch batch)
    {
        var rooms=Directory().Classes;var users=school.Users();
        if(batch.Cells.Any(c=>!rooms.Any(r=>r.Id==c.ClassId)||c.TeacherId.Length>0&&!users.Any(u=>u.Id==c.TeacherId&&u.Active)))throw new FilePolicyException("학급·교사 연결이 변경되었습니다. 다시 미리보기하세요.",409);
        // Homeroom teachers pass changes on to families, which needs the news server.
    }
    void ValidatePeriod(TimetableBatch batch,IEnumerable<TimetableBatch> previous)
    {
        foreach(var room in batch.Cells.Select(c=>c.ClassId).Distinct())
        {
            var active=previous.Where(b=>b.Kind=="base"&&b.Cells.Any(c=>c.ClassId==room)).GroupBy(b=>(b.Start,b.End)).Select(g=>g.MaxBy(b=>b.Revision)!).Where(b=>!b.Withdrawn);
            if(active.Any(b=>string.CompareOrdinal(b.Start,batch.End)<=0&&string.CompareOrdinal(b.End,batch.Start)>=0&&(b.Start!=batch.Start||b.End!=batch.End)))throw new FilePolicyException("기초시간표 적용 기간이 겹칩니다. 기존 기간을 취소하거나 같은 기간으로 교체하세요.",409);
        }
    }
    record Change(string Date,string ClassId,string ClassName,int Period,string BeforeSubject,string BeforeTeacher,string BeforeTeacherId,string Subject,string Teacher,string TeacherId);
    static Change[] Changes(TimetableBatch batch,IReadOnlyCollection<TimetableBatch> rows)
    {
        // The first base timetable of a class replaces nothing, so nobody is told it changed.
        var rooms=batch.Cells.Select(c=>c.ClassId).Distinct().Where(room=>batch.Kind!="base"||rows.Any(b=>b.Kind=="base"&&!b.Withdrawn&&b.Cells.Any(c=>c.ClassId==room))).ToArray();var result=new List<Change>();var first=TimetableData.Date(batch.Start);var days=batch.Kind=="daily"?1:Math.Min(7,TimetableData.Date(batch.End).DayNumber-first.DayNumber+1);
        for(var n=0;n<days;n++)
        {
            var day=first.AddDays(n).ToString("yyyy-MM-dd");var before=TimetableData.Resolve(rows,day,rooms).Slots;var after=TimetableData.Resolve(rows.Append(batch),day,rooms).Slots;
            foreach(var room in rooms)for(var p=1;p<=12;p++)
            {
                var a=before.SingleOrDefault(c=>c.ClassId==room&&c.Period==p);var b=after.SingleOrDefault(c=>c.ClassId==room&&c.Period==p);
                if((a?.Subject??"")==(b?.Subject??"")&&(a?.TeacherName??"")==(b?.TeacherName??""))continue;
                result.Add(new(day,room,(b??a)!.ClassName,p,a?.Subject??"",a?.TeacherName??"",a?.TeacherId??"",b?.Subject??"",b?.TeacherName??"",b?.TeacherId??""));
            }
        }
        return result.ToArray();
    }
    TimetableNotice[] Notifications(TimetableBatch batch,Change[] changes,TimetableSwap[]? approvals=null)
    {
        var directory=Directory();var classes=changes.Select(c=>c.ClassId).Distinct().ToArray();var recipients=new Dictionary<string,HashSet<string>>();
        void Add(string user,string room){if(user.Length==0)return;if(!recipients.TryGetValue(user,out var scope))recipients[user]=scope=[];scope.Add(room);}
        foreach(var change in changes){Add(change.BeforeTeacherId,change.ClassId);Add(change.TeacherId,change.ClassId);}
        foreach (var request in approvals ?? [])
            foreach (var change in request.Changes.Where(c => c.Position.Date == batch.Date))
            {
                Add(request.Owner, change.Position.ClassId);
                Add(change.Before.TeacherId, change.Position.ClassId);
                Add(change.After.TeacherId, change.Position.ClassId);
            }
        var homes=Homerooms();foreach(var home in homes.Where(h=>classes.Contains(h.ClassId)))if(school.GetUser(home.TeacherId) is{Active:true})Add(home.TeacherId,home.ClassId);
        // Each teacher reads their own change; the rest of the school reads the general line.
        string Title(string user)
        {
            if(batch.Withdrawn)return "시간표 등록이 취소되었습니다";
            var mine=changes.Where(c=>c.BeforeTeacherId==user||c.TeacherId==user||homes.Any(h=>h.TeacherId==user&&h.ClassId==c.ClassId)).ToArray();if(mine.Length==0)return approvals is { Length: >0 } ? "수업 교체 신청을 승인·게시했습니다" : "학교 시간표가 변경되었습니다";
            string Lesson(string subject,string teacher)=>subject.Length==0?"수업 없음":subject+"("+teacher+")";
            var day=TimetableData.Date(mine[0].Date);var line=$"{day.Month}.{day.Day}({"일월화수목금토"[(int)day.DayOfWeek]}) {mine[0].ClassName} {mine[0].Period}교시 {Lesson(mine[0].BeforeSubject,mine[0].BeforeTeacher)} → {Lesson(mine[0].Subject,mine[0].Teacher)}";
            return mine.Length==1?line:line+$" 외 {mine.Length-1}건";
        }
        return recipients.Select(r=>new TimetableNotice(batch.Revision,r.Key,batch.Start,r.Value.ToArray(),Title(r.Key),batch.PublishedAt)).ToArray();
    }
    async Task<IResult> Publish(TimetableCommit request, string owner)
    {
        RequireManager(owner);
        if (!ValidId(request.ClientId) || !ValidId(request.CandidateId))
            throw new FilePolicyException("게시 요청 번호를 확인하세요.");
        var ids = SwapIds(request.SwapIds).Order(StringComparer.Ordinal).ToArray();
        if (Data.ByClient(owner, request.ClientId) is { PublicationCandidateId: not null } prior)
        {
            if (prior.PublicationCandidateId != request.CandidateId || !(prior.ApprovedSwapIds ?? []).SequenceEqual(ids))
                throw new FilePolicyException("같은 요청 번호로 다른 시간표를 게시할 수 없습니다.", 409);
            return Results.Ok(prior);
        }

        var candidate = Load<Candidate>("TimetableCandidates", request.CandidateId, owner);
        var official = Data.Batches();
        var remaining = new List<TimetableBatch>();
        TimetableBatch? first = null;
        for (var i = 0; i < candidate.Batches.Length; i++)
        {
            var client = i == 0 ? request.ClientId : Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.ClientId + "|" + i)))[..32].ToLowerInvariant();
            if (Data.ByClient(owner, client) is { } existing)
            {
                if (existing.Id != candidate.Batches[i].Id || ids.Length > 0)
                    throw new FilePolicyException("같은 요청 번호로 다른 시간표를 게시할 수 없습니다.", 409);
                first ??= existing;
                continue;
            }

            var batch = candidate.Batches[i] with { ClientId = client, PublishedAt = Now };
            ValidateLinks(batch);
            if (batch.Kind == "base") ValidatePeriod(batch, official.Concat(remaining));
            batch = batch with { PublicationCandidateId = i == 0 ? request.CandidateId : null, ApprovedSwapIds = ids };
            remaining.Add(batch);
        }
        if (remaining.Count == 0) return Results.Ok(first);
        if (first is null && candidate.Revision != Data.Revision)
            throw new FilePolicyException("다른 시간표가 게시되었습니다. 변경 전후를 다시 미리보기하세요.", 409);
        var (approvals, bundles) = VerifySwapPublication(ids, remaining.ToArray(), official, owner);
        var timeline = official.ToList();
        var entries = remaining.Select(batch =>
        {
            var notices = Notifications(batch, Changes(batch, timeline), approvals);
            timeline.Add(batch);
            return (Batch: batch, Notices: notices);
        }).ToArray();
        var saved = Data.Publish(entries, approvals, bundles);
        for (var i = 0; i < saved.Length; i++) await SendNotices(saved[i], entries[i].Notices);
        return Results.Ok(first ?? saved[0]);
    }

    async Task<IResult> Cancel(string id,string client,string owner)
    {
        RequireManager(owner);if(!Guid.TryParseExact(client,"N",out _))throw new FilePolicyException("취소 요청 번호를 확인하세요.");
        if(Data.ByClient(owner,client) is{} existing){if(existing.FileName!="취소:"+id)throw new FilePolicyException("취소 요청 번호가 중복입니다.",409);return Results.Ok(existing);}
        var rows=Data.Batches();var original=rows.FirstOrDefault(b=>b.Id==id);if(original is null||original.Withdrawn)throw new FilePolicyException("취소할 게시본이 없습니다.",404);
        if(original.ApprovedSwapIds is { Length: > 0 }) throw new FilePolicyException("교체 승인과 함께 게시한 시간표입니다. 나이스에서 다시 수정한 파일을 올려주세요.",409);
        if(original.Cells.Select(c=>c.ClassId).Distinct().Any(room=>rows.Where(b=>b.Kind==original.Kind&&(b.Kind=="daily"?b.Date==original.Date:b.Start==original.Start&&b.End==original.End)&&b.Cells.Any(c=>c.ClassId==room)).MaxBy(b=>b.Revision)?.Id!=id))throw new FilePolicyException("일부 학급의 새 게시본이 있습니다. 현재 게시본을 확인하세요.",409);
        var batch=original with{Id=Guid.NewGuid().ToString("N"),Revision=Data.Revision+1,Owner=owner,ClientId=client,PublishedAt=Now,Withdrawn=true,FileName="취소:"+id,PublicationCandidateId=null,ApprovedSwapIds=null};return Results.Ok(await Commit(batch));
    }
    async Task<TimetableBatch> Commit(TimetableBatch batch)
    {
        ValidateLinks(batch);
        var notices=Notifications(batch,Changes(batch,Data.Batches()));var saved=Data.Insert(batch,notices);
        await SendNotices(saved, notices);
        return saved;
    }
    async Task SendNotices(TimetableBatch saved, TimetableNotice[] notices)
    {
        // Persistence succeeds independently of connected desktops; their next refresh also reads the durable notices.
        foreach(var target in notices.Where(n=>school.GetUser(n.UserId) is{Active:true}))
            try{await hub.Clients.Group(target.UserId).SendAsync("TimetableChanged",saved.Start,saved.Revision);}catch{ /* The durable notice remains available after reconnection. */ }
    }
}

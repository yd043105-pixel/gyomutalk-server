using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalNotices
{
    public static AnnouncementTarget[] Audience(PortalDirectory directory,DirectoryPerson teacher,AnnouncementRequest request)
    {
        try{return AnnouncementAudience.Build(directory,teacher,request);}catch(InvalidOperationException e){throw new FilePolicyException(e.Message,403);}
    }
    public static string? Invalid(AnnouncementRequest r)
    {
        if(!Guid.TryParseExact(r.ClientId,"N",out _)||string.IsNullOrWhiteSpace(r.Title)||r.Title.Length>200||string.IsNullOrWhiteSpace(r.Body)||r.Body.Length>10000||r.Audience is not("students"or"parents"or"both")||r.Kind is not("notice"or"home-letter"))return "제목·내용·수신 대상·공지 유형을 확인하세요.";
        if(r.ClassIds is null||r.ClassIds.Length>100||r.ClassIds.Any(id=>!Guid.TryParseExact(id,"N",out _))||r.SectionIds?.Length>100||r.SectionIds?.Any(id=>!Guid.TryParseExact(id,"N",out _))==true||!r.All&&r.ClassIds.Length==0&&(r.SectionIds?.Length??0)==0||r.All&&(r.ClassIds.Length>0||(r.SectionIds?.Length??0)>0))return "학급·분반 또는 전체 학교 중 하나를 선택하세요.";
        if(r.AttachmentIds is null||r.AttachmentIds.Length>5||r.AttachmentIds.Any(id=>!Guid.TryParseExact(id,"N",out _))||r.AttachmentIds.Distinct().Count()!=r.AttachmentIds.Length)return "공지 이미지는 최대 5장이며 같은 파일을 중복할 수 없습니다.";
        return null;
    }
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder bridge,PortalStore store,IConfiguration config,IHostEnvironment environment)
    {
        bridge.MapGet("/directory",()=>store.DirectorySnapshot());
        bridge.MapPost("/heartbeat",(BridgeHeartbeat request)=>
        {
            if(request.Teachers is null||request.Teachers.Length>5000||request.Teachers.Any(t=>!Guid.TryParseExact(t.Id,"N",out _))||request.Teachers.Select(t=>t.Id).Distinct().Count()!=request.Teachers.Length)return Error("교직원 연결 확인 형식을 확인하세요.");
            using var db=store.Open();using var tx=db.BeginTransaction();
            var teachers=store.Query("SELECT * FROM People WHERE Role='teacher'",PortalStore.ReadAccount);
            foreach(var teacher in teachers)
            {
                var proof=request.Teachers.FirstOrDefault(t=>t.Id==teacher.Id);var active=proof?.Active==true&&teacher.Active;
                using(var revoke=PortalStore.Command(db,"UPDATE People SET Version=Version+1 WHERE Id=$id AND EXISTS(SELECT 1 FROM TeacherProofs WHERE PersonId=$id AND Active=1) AND $active=0",tx,("$id",teacher.Id),("$active",active)))revoke.ExecuteNonQuery();
                using var update=PortalStore.Command(db,"INSERT INTO TeacherProofs VALUES($id,$active,$all,$now) ON CONFLICT(PersonId) DO UPDATE SET Active=excluded.Active,CanBroadcast=excluded.CanBroadcast,VerifiedAt=excluded.VerifiedAt",tx,("$id",teacher.Id),("$active",active),("$all",proof?.CanBroadcast==true),("$now",Now));update.ExecuteNonQuery();
            }
            tx.Commit();return Results.Ok(new{verifiedAt=Now});
        });
        api.MapGet("/children",(HttpContext c)=>store.Query("SELECT s.Id,s.Name,s.ClassId,c.Name FROM Families f JOIN People s ON s.Id=f.StudentId JOIN Classes c ON c.Id=s.ClassId WHERE f.ParentId=$id AND s.Active=1",r=>new{id=r.GetString(0),name=r.GetString(1),classId=r.GetString(2),className=r.GetString(3)},("$id",Id(c))));
        api.MapGet("/classes",(HttpContext c)=>
        {var person=store.Person(Id(c))!;return store.DirectorySnapshot().Classes.Where(room=>person.Role=="teacher"&&(VerifiedTeacher(store,person,true)||store.Classes(person.Id).Contains(room.Id)));});
        api.MapGet("/announcements",(HttpContext c,string? childId,int? offset)=>
        {
            var person=store.Person(Id(c))!;
            if(person.Role=="teacher"&&!VerifiedTeacher(store,person))return Error("교내 교직원 연결 확인이 만료되었습니다.",403);
            if(childId is not null&&!Guid.TryParseExact(childId,"N",out _))return Error("자녀 선택을 확인하세요.");
            return Results.Ok(store.Query(Select+" WHERE (n.SenderId=$user AND $teacher=1 OR n.Withdrawn=0 AND "+Accessible+") ORDER BY n.PublishedAt DESC,n.Id DESC LIMIT 100 OFFSET $offset",
                ReadNotice,("$user",person.Id),("$teacher",person.Role=="teacher"),("$child",childId??""),("$offset",Math.Clamp(offset??0,0,100000))));
        });
        api.MapGet("/announcements/{id}",(string id,HttpContext c)=>Detail(store,id,Id(c)));
        api.MapPost("/announcements/{id}/read",(string id,HttpContext c)=>
        {
            if(!CanRead(store,id,Id(c),false))return Results.NotFound();
            store.Execute("UPDATE Targets SET ReadAt=COALESCE(ReadAt,$now) WHERE NoticeId=$id AND UserId=$user AND Revoked=0",("$now",Now),("$id",id),("$user",Id(c)));return Results.Ok();
        });
        api.MapPost("/announcements",(AnnouncementRequest request,HttpContext c)=>Publish(store,Id(c),request,null,config,environment));
        bridge.MapPost("/announcements",(BridgeAnnouncement request)=>request is null||request.Request is null||request.Targets is null?Error("확정한 발송 대상이 필요합니다."):Publish(store,request.TeacherId,request.Request,request.Targets,config,environment));
        api.MapPost("/announcements/{id}/retract",(string id,HttpContext c)=>Retract(store,id,Id(c)));
        bridge.MapPost("/announcements/{id}/retract",(string id,BridgeWithdrawal request)=>Retract(store,id,request.TeacherId));
        bridge.MapGet("/announcements/by-client/{clientId}",(string clientId,string teacherId)=>
        {
            var row=store.Query("SELECT Id FROM Notices WHERE SenderId=$teacher AND ClientId=$client",r=>r.GetString(0),("$teacher",teacherId),("$client",clientId)).FirstOrDefault();return row is null?Results.NotFound():Detail(store,row,teacherId);
        });
        bridge.MapGet("/announcements/{id}",(string id,string teacherId)=>Detail(store,id,teacherId));
        api.MapPost("/attachments",async Task<IResult>(HttpContext c)=>await Upload(store,Id(c),c,config,environment)).RequireAuthorization("Teacher").RequireRateLimiting("upload");
        bridge.MapPost("/attachments",(string teacherId,long? expiresAt,HttpContext c)=>Upload(store,teacherId,c,config,environment,expiresAt)).RequireRateLimiting("upload");
        IResult FileContent(string id,HttpContext c)
        {
            if(!Guid.TryParseExact(id,"N",out _))return Results.NotFound();
            var file=store.Query("SELECT f.Name,f.NoticeId,f.ExpiresAt,f.DeletedAt,n.Kind FROM Files f LEFT JOIN Notices n ON n.Id=f.NoticeId WHERE f.Id=$id",r=>new{name=r.GetString(0),notice=PortalStore.Text(r,1),expires=r.IsDBNull(2)?0:r.GetInt64(2),deleted=!r.IsDBNull(3),kind=PortalStore.Text(r,4)},("$id",id)).FirstOrDefault();
            if(file?.notice is null||!CanRead(store,file.notice,Id(c),false))return Results.NotFound();
            using var verified=store.Open();
            if(file.deleted||file.expires<=Now||!StoredFiles.Ready(verified,"portal",id,Path.Combine(store.Files,id)))return Error("공지 첨부의 30일 보관 기간이 만료되었거나 파일이 없습니다.",410);
            try{FilePolicy.NoticeImage(Path.Combine(store.Files,id),file.name);}catch(FilePolicyException){return Error("공지 이미지를 표시할 수 없습니다. 이전 문서 첨부는 JPG·PNG로 다시 게시해 주세요.",422);}
            c.Response.Headers.ContentDisposition="inline";c.Response.Headers.CacheControl="no-store";c.Response.Headers.XContentTypeOptions="nosniff";
            return Results.File(Path.Combine(store.Files,id),FilePolicy.NoticeImageType(file.name),enableRangeProcessing:false);
        }
        api.MapGet("/attachments/{id}/download",()=>Results.NotFound());
        api.MapGet("/attachments/{id}/view",(string id,HttpContext c)=>FileContent(id,c));
    }
    public static IResult Publish(PortalStore store,string teacherId,AnnouncementRequest request,AnnouncementTarget[]? captured,IConfiguration config,IHostEnvironment environment)
    {
        if(request is null)return Error("공지를 입력하세요.");
        if(Invalid(request) is{} invalid)return Error(invalid);
        if(captured?.Length>10000)return Error("발송 대상이 너무 많습니다.");
        using var db=store.Open();using var tx=db.BeginTransaction();
        var teacher=store.Person(teacherId);if(teacher is null||!VerifiedTeacher(store,teacher,request.All))return Error("활성 교직원 연결 확인과 발송 권한이 필요합니다.",403);
        var directory=store.DirectorySnapshot();var publisher=directory.People.First(p=>p.Id==teacherId) with{CanBroadcast=VerifiedTeacher(store,teacher,true)};
        AnnouncementTarget[] valid;
        try{valid=Audience(directory,publisher,request);}catch(FilePolicyException error){return Error(error.Message,error.Status);}
        var targets=(captured??valid).Distinct().OrderBy(t=>t.UserId,StringComparer.Ordinal).ThenBy(t=>t.StudentId,StringComparer.Ordinal).ToArray();
        if(targets.Length==0||targets.Any(t=>!valid.Contains(t)))return Error("발송 대상 또는 보호자 관계가 변경되었습니다. 대상을 확인하고 새 공지로 보내세요.",409);
        var fingerprint=Hash((request.Kind=="home-letter"?"home-letter:":"")+JsonSerializer.Serialize(new{title=request.Title.Trim(),request.Body,request.Audience,classes=request.ClassIds.Order(StringComparer.Ordinal).ToArray(),sections=(request.SectionIds??[]).Order(StringComparer.Ordinal).ToArray(),request.All,files=request.AttachmentIds.Order(StringComparer.Ordinal).ToArray(),targets}));
        using(var previous=PortalStore.Command(db,"SELECT Id,Fingerprint FROM Notices WHERE SenderId=$sender AND ClientId=$client",tx,("$sender",teacherId),("$client",request.ClientId)))
        using(var r=previous.ExecuteReader()){if(r.Read())return r.GetString(1)==fingerprint?Results.Ok(new{id=r.GetString(0),duplicate=true}):Error("같은 요청 번호의 내용을 변경할 수 없습니다.",409);}
        long total=0;
        foreach(var fileId in request.AttachmentIds)
        {
            using var file=PortalStore.Command(db,"SELECT Size FROM Files WHERE Id=$id AND OwnerId=$owner AND NoticeId IS NULL AND DeletedAt IS NULL AND UploadedAt>$cutoff",tx,("$id",fileId),("$owner",teacherId),("$cutoff",Now-86_400_000));
            var size=file.ExecuteScalar();if(size is null||!File.Exists(Path.Combine(store.Files,fileId)))return Error("공지 전용 첨부파일을 다시 확인하세요.",409);total+=Convert.ToInt64(size);
            {
                using var filename=PortalStore.Command(db,"SELECT Name FROM Files WHERE Id=$id",tx,("$id",fileId));var name=(string)filename.ExecuteScalar()!;
                try{FilePolicy.NoticeImageName(name,Convert.ToInt64(size));FilePolicy.NoticeImage(Path.Combine(store.Files,fileId),name);}catch(FilePolicyException e){return Error(e.Message,e.Status);}
            }
        }
        if(total>FilePolicy.MaxTotal)return Error("공지 첨부 합계는 200MB까지입니다.",413);
        var id=Guid.NewGuid().ToString("N");var now=Now;
        using(var add=PortalStore.Command(db,"INSERT INTO Notices(Id,SenderId,ClientId,Fingerprint,Title,Body,Audience,PublishedAt,Withdrawn,Kind) VALUES($id,$sender,$client,$hash,$title,$body,$audience,$now,0,$kind)",tx,("$id",id),("$sender",teacherId),("$client",request.ClientId),("$hash",fingerprint),("$title",request.Title.Trim()),("$body",request.Body),("$audience",request.Audience),("$now",now),("$kind",request.Kind)))add.ExecuteNonQuery();
        foreach(var target in targets){using var add=PortalStore.Command(db,"INSERT INTO Targets(NoticeId,UserId,StudentId,ClassId) VALUES($id,$user,$student,$class)",tx,("$id",id),("$user",target.UserId),("$student",target.StudentId),("$class",target.ClassId));add.ExecuteNonQuery();}
        foreach(var section in (directory.Sections??[]).Where(s=>(request.SectionIds??[]).Contains(s.Id)))foreach(var member in section.Members.Where(m=>targets.Any(t=>t.StudentId==m.StudentId))){using var add=PortalStore.Command(db,"INSERT INTO NoticeSectionMembers VALUES($notice,$section,$student,$stamp)",tx,("$notice",id),("$section",section.Id),("$student",member.StudentId),("$stamp",member.Stamp));add.ExecuteNonQuery();}
        var seconds=environment.IsDevelopment()?Math.Max(1,config.GetValue("Portal:RetentionSeconds",2_592_000)):2_592_000;
        foreach(var file in request.AttachmentIds){using var bind=PortalStore.Command(db,"UPDATE Files SET NoticeId=$notice,ExpiresAt=MIN(COALESCE(ExpiresAt,UploadedAt+2592000000),$expires) WHERE Id=$id",tx,("$notice",id),("$expires",now+seconds*1000L),("$id",file));bind.ExecuteNonQuery();}
        tx.Commit();return Results.Ok(new{id,duplicate=false});
    }
    static async Task<IResult> Upload(PortalStore store,string teacherId,HttpContext c,IConfiguration config,IHostEnvironment environment,long? sourceExpiresAt=null)
    {
        if(sourceExpiresAt<=Now)return Error("원본 공지 첨부의 보관 기간이 만료되었습니다.",410);
        var teacher=store.Person(teacherId);if(teacher is null||!VerifiedTeacher(store,teacher))return Error("승인된 교사만 공지 파일을 올릴 수 있습니다.",403);
        if(!c.Request.HasFormContentType)return Error("파일을 선택하세요.");var form=await c.Request.ReadFormAsync(c.RequestAborted);
        if(form.Files.Count!=1||!Guid.TryParseExact(form["clientId"],"N",out _))return Error("파일과 요청 번호를 확인하세요.");
        var file=form.Files[0];
        string name;
        var kind=form["kind"].ToString();if(kind is not(""or"notice"or"home-letter"))return Error("공지 유형을 확인하세요.");
        try{name=FilePolicy.NoticeImageName(file.FileName,file.Length);}catch(FilePolicyException e){return Error(e.Message,e.Status);}
        if(new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace<file.Length+FilePolicy.MaxStaged)return Error("서버 저장 공간이 부족합니다.",507);
        var id=Guid.NewGuid().ToString("N");var path=Path.Combine(store.Files,id);var keep=false;
        using(var state=store.Open())StoredFiles.State(state,"portal",id,"uploading");
        try
        {
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))await file.CopyToAsync(output,c.RequestAborted);
            FilePolicy.NoticeImage(path,name);
            using(var state=store.Open())StoredFiles.State(state,"portal",id,"scanning");
            await FilePolicy.Scan(path,name,environment.IsDevelopment(),config["Portal:ScannerPath"],c.RequestAborted);
            var current=store.Person(teacherId);if(current is null||!VerifiedTeacher(store,current))return Error("교사 권한이 변경되었습니다.",403);
            using var input=File.OpenRead(path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(input,c.RequestAborted));
            using var db=store.Open();using var tx=db.BeginTransaction();
            var previous=store.Query("SELECT Id,Name,Hash,Size,NoticeId,DeletedAt,UploadedAt FROM Files WHERE OwnerId=$owner AND ClientId=$client",r=>new{id=r.GetString(0),name=r.GetString(1),hash=r.GetString(2),size=r.GetInt64(3),notice=PortalStore.Text(r,4),deleted=!r.IsDBNull(5),uploaded=r.GetInt64(6)},("$owner",teacherId),("$client",form["clientId"].ToString())).FirstOrDefault();
            if(previous is not null)
            {
                if(previous.hash!=hash||previous.name!=name)return Error("같은 파일 요청 번호의 내용을 바꿀 수 없습니다.",409);
                if(previous.notice is not null||previous.deleted||previous.uploaded<=Now-86_400_000)return Error("이미 사용했거나 만료된 파일입니다.",409);
                if(sourceExpiresAt is{} originalExpiry){using var shorten=PortalStore.Command(db,"UPDATE Files SET ExpiresAt=MIN(COALESCE(ExpiresAt,UploadedAt+2592000000),$expires) WHERE Id=$id",tx,("$expires",originalExpiry),("$id",previous.id));shorten.ExecuteNonQuery();tx.Commit();}
                return Results.Ok(new PortalFile(previous.id,previous.name,previous.size));
            }
            using var insert=PortalStore.Command(db,"INSERT INTO Files(Id,OwnerId,ClientId,Hash,Name,Size,UploadedAt,ExpiresAt) SELECT $id,$owner,$client,$hash,$name,$size,$now,$expires WHERE (SELECT COALESCE(SUM(Size),0) FROM Files WHERE OwnerId=$owner AND NoticeId IS NULL AND DeletedAt IS NULL)+$size<=$quota",tx,("$id",id),("$owner",teacherId),("$client",form["clientId"].ToString()),("$hash",hash),("$name",name),("$size",file.Length),("$now",Now),("$expires",Math.Min(sourceExpiresAt??long.MaxValue,Now+(environment.IsDevelopment()?Math.Max(1,config.GetValue("Portal:RetentionSeconds",2_592_000)):2_592_000)*1000L)),("$quota",FilePolicy.MaxStaged));
            if(insert.ExecuteNonQuery()!=1)return Error("미게시 첨부가 500MB를 넘습니다.",413);StoredFiles.State(db,"portal",id,"ready",hash,tx);tx.Commit();keep=true;return Results.Ok(new PortalFile(id,name,file.Length));
        }
        catch(FilePolicyException error){return Error(error.Message,error.Status);}
        finally{if(!keep){File.Delete(path);using var state=store.Open();StoredFiles.State(state,"portal",id,"rejected");}}
    }
    static IResult Retract(PortalStore store,string id,string teacherId)
    {
        if(store.Person(teacherId) is not{Active:true,Role:"teacher"})return Error("교사 권한이 필요합니다.",403);
        if(store.Execute("UPDATE Notices SET Withdrawn=1 WHERE Id=$id AND SenderId=$user",("$id",id),("$user",teacherId))==0)return Results.NotFound();return Results.Ok();
    }
    static bool CanRead(PortalStore store,string id,string user,bool allowWithdrawn)
    {
        var account=store.Person(user);if(account is not{Active:true})return false;
        if(account.Role=="teacher"&&!VerifiedTeacher(store,account))return false;
        return store.Query("SELECT n.Id FROM Notices n WHERE n.Id=$id AND (n.SenderId=$user AND $teacher=1 OR n.Withdrawn=0 AND "+Accessible+")",r=>r.GetString(0),("$id",id),("$user",user),("$teacher",account.Role=="teacher"),("$child","")).Count>0;
    }
    static IResult Detail(PortalStore store,string id,string user)
    {
        if(!CanRead(store,id,user,true))return Results.NotFound();
        var notice=store.Query(Select+" WHERE n.Id=$id",ReadNotice,("$id",id),("$user",user)).Single();
        var owner=store.Query("SELECT 1 FROM Notices WHERE Id=$id AND SenderId=$user",r=>r.GetInt32(0),("$id",id),("$user",user)).Count>0;
        var files=store.Query("SELECT Id,Name,Size,ExpiresAt,DeletedAt FROM Files WHERE NoticeId=$id ORDER BY UploadedAt,rowid",r=>new PortalFile(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),!r.IsDBNull(4)||r.GetInt64(3)<=Now||!File.Exists(Path.Combine(store.Files,r.GetString(0)))),("$id",id)).ToArray();
        var teacher=store.Person(user)!;var classes=store.Classes(user);var sectionStudents=store.Query("SELECT DISTINCT ns.StudentId FROM NoticeSectionMembers ns JOIN SectionTeachers st ON st.SectionId=ns.SectionId JOIN SectionMembers sm ON sm.SectionId=ns.SectionId AND sm.StudentId=ns.StudentId AND sm.Stamp=ns.Stamp JOIN People s ON s.Id=ns.StudentId AND s.Active=1 WHERE ns.NoticeId=$notice AND st.TeacherId=$teacher",r=>r.GetString(0),("$notice",id),("$teacher",user)).ToHashSet(StringComparer.Ordinal);
        PortalReceipt[]? receipts=owner?store.Query("SELECT t.UserId,p.Name,s.Name,t.ReadAt,t.Revoked,t.ClassId,t.StudentId FROM Targets t JOIN People p ON p.Id=t.UserId JOIN People s ON s.Id=t.StudentId WHERE t.NoticeId=$id ORDER BY p.Name,s.Name",r=>new PortalReceipt(r.GetString(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetInt64(3),r.GetBoolean(4),r.GetString(5),r.GetString(6)),("$id",id)).Where(r=>classes.Contains(r.ClassId)||VerifiedTeacher(store,teacher,true)||sectionStudents.Contains(r.StudentId??"")).ToArray():null;
        return Results.Ok(new PortalNoticeDetail(notice,files,receipts));
    }
    const string Accessible="""
        EXISTS(SELECT 1 FROM Targets t JOIN People s ON s.Id=t.StudentId
         WHERE t.NoticeId=n.Id AND t.UserId=$user AND t.Revoked=0 AND s.Active=1 AND s.ClassId=t.ClassId AND ($child='' OR t.StudentId=$child)
         AND (NOT EXISTS(SELECT 1 FROM NoticeSectionMembers ns WHERE ns.NoticeId=n.Id AND ns.StudentId=t.StudentId) OR EXISTS(SELECT 1 FROM NoticeSectionMembers ns JOIN SectionMembers sm ON sm.SectionId=ns.SectionId AND sm.StudentId=ns.StudentId AND sm.Stamp=ns.Stamp WHERE ns.NoticeId=n.Id AND ns.StudentId=t.StudentId))
         AND (t.UserId=t.StudentId OR EXISTS(SELECT 1 FROM Families f WHERE f.ParentId=t.UserId AND f.StudentId=t.StudentId)
          OR EXISTS(SELECT 1 FROM TimetableHomerooms h JOIN People teacher ON teacher.InternalUserId=h.TeacherId WHERE h.ClassId=t.ClassId AND teacher.Id=t.UserId AND teacher.Active=1 AND teacher.Role='teacher')))
        """;
    const string Select="""
        SELECT n.Id,n.Title,n.Body,p.Name,n.PublishedAt,(SELECT MIN(ReadAt) FROM Targets WHERE NoticeId=n.Id AND UserId=$user),n.Withdrawn,
          CASE WHEN n.SenderId=$user THEN (SELECT COUNT(DISTINCT UserId) FROM Targets WHERE NoticeId=n.Id) ELSE 0 END,CASE WHEN n.SenderId=$user THEN (SELECT COUNT(DISTINCT UserId) FROM Targets WHERE NoticeId=n.Id AND ReadAt IS NOT NULL) ELSE 0 END,n.Kind
        FROM Notices n JOIN People p ON p.Id=n.SenderId
        """;
    static PortalNotice ReadNotice(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.IsDBNull(5)?null:r.GetInt64(5),r.GetBoolean(6),r.GetInt32(7),r.GetInt32(8),r.GetString(9));
}
record BridgeWithdrawal(string TeacherId);

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public record InviteRegistration(string? Code,string? Username,string? Password,string? Name=null,string? Role=null,string? ClassId=null,int? StudentNumber=null,SchoolMessenger.Contracts.RegistrationAcceptance? Agreement=null);
record NewClass(string? Name,int Grade);
record NewPerson(string? Name,string? Role,string? ClassId,string? InternalUserId,string[]? ClassIds,bool CanBroadcast);
record UpdatePerson(string? Name,bool Active,string? ClassId,string[]? ClassIds,bool CanBroadcast);
record FamilyLink(string? ParentId,string? StudentId);

public static class PortalAdministration
{
    public static IResult Register(PortalStore store,PasswordHasher<Account> hasher,InviteRegistration request,SchoolMessenger.Shared.RegistrationPolicy policy)
    {
        if(request.Code?.Length is not (>=32 and <=128)||!Username(request.Username)||!Password(request.Password))return Error("학교 초대번호, 아이디(3~32자), 비밀번호(12~128자)를 확인하세요.");
        if(policy.Validate(request.Agreement) is{} policyError)return Error(policyError,policy.Current.CanRegister?400:503);
        using var db=store.Open();using var tx=db.BeginTransaction();
        string personId;
        using(var invite=PortalStore.Command(db,"SELECT PersonId FROM Invites WHERE Hash=$hash AND UsedAt IS NULL AND ExpiresAt>$now",tx,("$hash",Hash(request.Code)),("$now",Now)))
        {personId=invite.ExecuteScalar() as string??"";}
        var account=store.Person(personId);
        if(account is not{Active:true,PasswordHash:null})return Error("사용할 수 없는 초대번호입니다.");
        var passwordHash=hasher.HashPassword(account,request.Password!);
        try
        {
            using var change=PortalStore.Command(db,"UPDATE People SET Username=$name,PasswordHash=$password,Version=Version+1,FailedAttempts=0,LockedUntil=0 WHERE Id=$id AND Active=1 AND PasswordHash IS NULL",tx,("$name",request.Username),("$password",passwordHash),("$id",account.Id));
            if(change.ExecuteNonQuery()!=1)return Error("이미 등록된 초대입니다.",409);
            using var consume=PortalStore.Command(db,"UPDATE Invites SET UsedAt=$now WHERE Hash=$hash AND UsedAt IS NULL AND ExpiresAt>$now",tx,("$now",Now),("$hash",Hash(request.Code)));
            if(consume.ExecuteNonQuery()!=1)return Error("만료되었거나 이미 사용한 초대입니다.",409);
            policy.Record(db,tx,account.Id,request.Agreement!);
            tx.Commit();return Results.Ok(new{registered=true});
        }
        catch(SqliteException e)when(e.SqliteExtendedErrorCode is 1555 or 2067){return Error("사용 중인 아이디입니다.",409);}
    }
    public static void Map(RouteGroupBuilder admin,PortalStore store,IDataProtectionProvider protection)
    {
        admin.MapGet("/classes",()=>store.DirectorySnapshot().Classes);
        admin.MapPost("/classes",(NewClass request)=>
        {
            if(!Name(request.Name)||request.Grade is <1 or >3)return Error("학급명과 학년(1~3)을 확인하세요.");
            var id=Guid.NewGuid().ToString("N");store.Execute("INSERT INTO Classes VALUES($id,$name,$grade)",("$id",id),("$name",request.Name!.Trim()),("$grade",request.Grade));return Results.Ok(new{id});
        });
        admin.MapGet("/people",()=>store.Query("SELECT * FROM People ORDER BY Role,Name",PortalStore.ReadAccount).Select(a=>new{a.Id,a.Username,a.Name,a.Role,a.ClassId,a.InternalUserId,a.Active,a.CanBroadcast,registered=a.PasswordHash is not null,classIds=store.Classes(a.Id),membership=store.Regular(a)?"regular":"temporary",application=store.Query("SELECT StudentNumber,RequestedAt,ApprovedAt FROM StudentApplications WHERE PersonId=$id",r=>new{studentNumber=r.GetInt32(0),requestedAt=r.GetInt64(1),pending=r.IsDBNull(2)},("$id",a.Id)).FirstOrDefault()}));
        admin.MapPost("/people",(NewPerson request)=>
        {
            if(!Name(request.Name)||request.Role is not("teacher"or"student"or"parent")||request.ClassIds is null||request.ClassIds.Length>100)return Error("이름과 학교에서 확인한 역할·담당 범위를 입력하세요.");
            if(request.Role=="student"&&(request.ClassId is null||!ClassExists(store,request.ClassId)))return Error("학생의 학급을 선택하세요.");
            if(request.Role!="student"&&request.ClassId is not null)return Error("학생만 소속 학급을 지정합니다.");
            if(request.Role=="teacher"&&!Guid.TryParseExact(request.InternalUserId,"N",out _))return Error("학교가 확인한 교직원 사용자 ID를 입력하세요.");
            if(request.Role!="teacher"&&(request.InternalUserId is not null||request.CanBroadcast||request.ClassIds.Length>0))return Error("교직원 권한은 교사 계정에만 설정합니다.");
            if(request.ClassIds.Any(id=>!ClassExists(store,id)))return Error("담당 학급을 확인하세요.");
            var id=Guid.NewGuid().ToString("N");
            using var db=store.Open();using var tx=db.BeginTransaction();
            try
            {
                using var create=PortalStore.Command(db,"INSERT INTO People(Id,Name,Role,ClassId,InternalUserId,CanBroadcast) VALUES($id,$name,$role,$class,$internal,$all)",tx,("$id",id),("$name",request.Name!.Trim()),("$role",request.Role),("$class",request.ClassId),("$internal",request.InternalUserId),("$all",request.CanBroadcast));create.ExecuteNonQuery();
                foreach(var scope in request.ClassIds.Distinct()){using var add=PortalStore.Command(db,"INSERT INTO TeacherClasses VALUES($id,$class)",tx,("$id",id),("$class",scope));add.ExecuteNonQuery();}
                tx.Commit();return Results.Ok(new{id});
            }catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("이미 연결한 교직원 계정이거나 잘못된 학급입니다.",409);}
        });
        admin.MapPatch("/people/{id}",(string id,UpdatePerson request,HttpContext c)=>
        {
            var person=store.Person(id);if(person is null)return Results.NotFound();
            if(!Name(request.Name)||request.ClassIds is null||request.ClassIds.Length>100||request.ClassIds.Any(scope=>!ClassExists(store,scope)))return Error("이름·담당 학급을 확인하세요.");
            if(person.Role=="admin"&&(!request.Active||request.CanBroadcast||request.ClassId is not null||request.ClassIds.Length>0))return Error("공지 관리자 권한을 이 화면에서 해제할 수 없습니다.");
            if(person.Role=="student"&&(request.ClassId is null||!ClassExists(store,request.ClassId)))return Error("학생 학급을 확인하세요.");
            if(person.Role!="student"&&request.ClassId is not null||person.Role!="teacher"&&(request.CanBroadcast||request.ClassIds.Length>0))return Error("역할에 맞는 담당 범위를 입력하세요.");
            using var db=store.Open();using var tx=db.BeginTransaction();
            if(person.Role=="student"&&(person.ClassId!=request.ClassId||!request.Active))
            {
                using var revoke=PortalStore.Command(db,"UPDATE Targets SET Revoked=1 WHERE StudentId=$id",tx,("$id",id));revoke.ExecuteNonQuery();
                using var parents=PortalStore.Command(db,"UPDATE People SET Version=Version+1 WHERE Id IN(SELECT ParentId FROM Families WHERE StudentId=$id)",tx,("$id",id));parents.ExecuteNonQuery();
            }
            if(!request.Active){using var revoke=PortalStore.Command(db,"UPDATE Targets SET Revoked=1 WHERE UserId=$id",tx,("$id",id));revoke.ExecuteNonQuery();}
            using(var update=PortalStore.Command(db,"UPDATE People SET Name=$name,Active=$active,ClassId=$class,CanBroadcast=$all,ScopeVersion=ScopeVersion+CASE WHEN Active=1 AND $active=0 OR Role='student' AND ClassId!=$class THEN 1 ELSE 0 END,Version=Version+1 WHERE Id=$id",tx,("$name",request.Name!.Trim()),("$active",request.Active),("$class",request.ClassId),("$all",request.CanBroadcast),("$id",id)))update.ExecuteNonQuery();
            if(person.Role=="student")
            {
                using var approve=PortalStore.Command(db,"UPDATE StudentApplications SET ApprovedAt=COALESCE(ApprovedAt,$now) WHERE PersonId=$id AND $active=1",tx,("$now",Now),("$id",id),("$active",request.Active));approve.ExecuteNonQuery();
                if(!request.Active){using var revokeCode=PortalStore.Command(db,"UPDATE StudentCodes SET Active=0 WHERE StudentId=$id",tx,("$id",id));revokeCode.ExecuteNonQuery();}
            }
            using(var remove=PortalStore.Command(db,"DELETE FROM TeacherClasses WHERE TeacherId=$id",tx,("$id",id)))remove.ExecuteNonQuery();
            foreach(var scope in request.ClassIds.Distinct()){using var add=PortalStore.Command(db,"INSERT INTO TeacherClasses VALUES($id,$class)",tx,("$id",id),("$class",scope));add.ExecuteNonQuery();}
            tx.Commit();return Results.Ok();
        });
        admin.MapPost("/people/{id}/invite",(string id)=>
        {
            var person=store.Person(id);if(person is not{Active:true,PasswordHash:null}||person.Role=="admin")return Error("미등록 학교 계정에만 초대를 발급할 수 있습니다.");
            var code=RandomToken();using var db=store.Open();using var tx=db.BeginTransaction();
            using(var old=PortalStore.Command(db,"UPDATE Invites SET ExpiresAt=$now WHERE PersonId=$id AND UsedAt IS NULL",tx,("$now",Now),("$id",id)))old.ExecuteNonQuery();
            var expires=Now+2*86_400_000L;using(var add=PortalStore.Command(db,"INSERT INTO Invites VALUES($hash,$id,$expires,NULL)",tx,("$hash",Hash(code)),("$id",id),("$expires",expires)))add.ExecuteNonQuery();
            tx.Commit();return Results.Ok(new{code,expiresAt=expires});
        });
        admin.MapGet("/families",()=>store.Query("SELECT f.ParentId,p.Name,f.StudentId,s.Name,c.Name FROM Families f JOIN People p ON p.Id=f.ParentId JOIN People s ON s.Id=f.StudentId JOIN Classes c ON c.Id=s.ClassId",r=>new{parentId=r.GetString(0),parentName=r.GetString(1),studentId=r.GetString(2),studentName=r.GetString(3),className=r.GetString(4)}));
        admin.MapPost("/families",(FamilyLink request)=>
        {
            if(request.ParentId is null||request.StudentId is null||store.Person(request.ParentId) is not{Role:"parent",Active:true}||store.Person(request.StudentId) is not{Role:"student",Active:true})return Error("학교가 확인한 보호자·학생 계정을 선택하세요.");
            store.Execute("INSERT OR IGNORE INTO Families(ParentId,StudentId,Stamp) VALUES($parent,$student,lower(hex(randomblob(16))))",("$parent",request.ParentId),("$student",request.StudentId));return Results.Ok();
        });
        admin.MapDelete("/families/{parent}/{student}",(string parent,string student)=>
        {
            using var db=store.Open();using var tx=db.BeginTransaction();
            using(var unlink=PortalStore.Command(db,"DELETE FROM Families WHERE ParentId=$parent AND StudentId=$student",tx,("$parent",parent),("$student",student)))unlink.ExecuteNonQuery();
            using(var revoke=PortalStore.Command(db,"UPDATE Targets SET Revoked=1 WHERE UserId=$parent AND StudentId=$student",tx,("$parent",parent),("$student",student)))revoke.ExecuteNonQuery();
            using(var session=PortalStore.Command(db,"UPDATE People SET Version=Version+1 WHERE Id=$id",tx,("$id",parent)))session.ExecuteNonQuery();
            if(store.Person(student) is{Role:"student",Active:true})PortalEnrollment.ReplaceCode(db,tx,protection,student);
            tx.Commit();return Results.Ok();
        });
        admin.MapGet("/status",()=>new{people=store.Query("SELECT COUNT(*) FROM People WHERE Active=1",r=>r.GetInt32(0))[0],
            lastBridgeContact=store.Query("SELECT MAX(VerifiedAt) FROM TeacherProofs",r=>r.IsDBNull(0)?(long?)null:r.GetInt64(0))[0],
            freeBytes=new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace,retentionDays=30,attachmentBackup=store.AttachmentBackupEnabled});
    }
    static bool ClassExists(PortalStore store,string id)=>store.Query("SELECT 1 FROM Classes WHERE Id=$id",r=>r.GetInt32(0),("$id",id)).Count>0;
}

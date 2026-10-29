using SchoolMessenger.Shared;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public record FamilyCode(string? Code);

public static class PortalEnrollment
{
    const string Alphabet="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    static IDataProtector Protector(IDataProtectionProvider provider)=>provider.CreateProtector("StudentFamilyCode.v1");

    public static IResult Register(PortalStore store,PasswordHasher<Account> hasher,InviteRegistration request,SchoolMessenger.Shared.RegistrationPolicy policy)
    {
        if(!Username(request.Username)||!Password(request.Password)||!Name(request.Name)||request.Role is not("parent"or"student"))return Error("이름·아이디(3~32자)·비밀번호(12~128자)와 학생/학부모 구분을 확인하세요.");
        if(request.Role=="parent"&&(request.ClassId is not null||request.StudentNumber is not null))return Error("학부모는 가입 후 설정에서 자녀 고유번호를 입력하세요.");
        if(request.Role=="student"&&(request.StudentNumber is not(>=1 and <=99)||!store.Query("SELECT 1 FROM Classes WHERE Id=$id",r=>r.GetInt32(0),("$id",request.ClassId)).Any()))return Error("학생의 학급과 번호(1~99)를 확인하세요.");
        if(policy.Validate(request.Agreement) is{} policyError)return Error(policyError,policy.Current.CanRegister?400:503);
        var person=new Account(Guid.NewGuid().ToString("N"),request.Username,request.Name!.Trim(),request.Role,request.ClassId,null,false,request.Role=="parent",1,null,0,0);
        var password=hasher.HashPassword(person,request.Password!);
        using var db=store.Open();using var tx=db.BeginTransaction();
        try
        {
            using(var create=PortalStore.Command(db,"INSERT INTO People(Id,Username,Name,Role,ClassId,Active,PasswordHash) VALUES($id,$username,$name,$role,$class,$active,$password)",tx,("$id",person.Id),("$username",person.Username),("$name",person.Name),("$role",person.Role),("$class",person.ClassId),("$active",person.Active),("$password",password)))create.ExecuteNonQuery();
            if(person.Role=="student"){using var application=PortalStore.Command(db,"INSERT INTO StudentApplications VALUES($id,$number,$now,NULL)",tx,("$id",person.Id),("$number",request.StudentNumber),("$now",Now));application.ExecuteNonQuery();}
            policy.Record(db,tx,person.Id,request.Agreement!);
            tx.Commit();return Results.Ok(new{registered=true,membership=person.Role=="parent"?"temporary":"pending",message=person.Role=="parent"?"임시회원 가입 완료. 로그인 후 설정에서 자녀 고유번호를 입력하세요.":"학생 가입 신청 완료. 학교 관리자의 승인 후 로그인할 수 있습니다."});
        }
        catch(SqliteException e)when(e.SqliteExtendedErrorCode is 1555 or 2067 or 787){return Error("사용 중인 아이디이거나 학급 정보가 변경되었습니다.",409);}
    }

    // Retain hashes of retired codes so a later student can never receive an old family's code.
    public static string ReplaceCode(SqliteConnection db,SqliteTransaction tx,IDataProtectionProvider protection,string student)
    {
        using(var retire=PortalStore.Command(db,"UPDATE StudentCodes SET Active=0 WHERE StudentId=$id",tx,("$id",student)))retire.ExecuteNonQuery();
        while(true)
        {
            var code=RandomNumberGenerator.GetString(Alphabet,8);
            if(!code.Any(char.IsLower)||!code.Any(char.IsUpper)||!code.Any(char.IsDigit))continue;
            using(var retired=PortalStore.Command(db,"SELECT 1 FROM RetiredStudentCodes WHERE Hash=$hash",tx,("$hash",Hash(code))))if(retired.ExecuteScalar() is not null)continue;
            using var add=PortalStore.Command(db,"INSERT OR IGNORE INTO StudentCodes VALUES($hash,$student,$protected,1,$now)",tx,("$hash",Hash(code)),("$student",student),("$protected",Protector(protection).Protect(code)),("$now",Now));
            if(add.ExecuteNonQuery()==1)return code;
        }
    }
    static string Code(PortalStore store,IDataProtectionProvider protection,string student,bool replace=false)
    {
        using var db=store.Open();using var tx=db.BeginTransaction();
        using var existing=PortalStore.Command(db,"SELECT ProtectedCode FROM StudentCodes WHERE StudentId=$id AND Active=1",tx,("$id",student));
        var protectedCode=existing.ExecuteScalar() as string;
        var code=!replace&&protectedCode is not null?Protector(protection).Unprotect(protectedCode):ReplaceCode(db,tx,protection,student);
        tx.Commit();return code;
    }
    static IResult Link(PortalStore store,HttpContext c,FamilyCode request)
    {
        var parent=Id(c);if(store.Person(parent) is not{Role:"parent",Active:true})return Error("학부모 계정에서만 자녀를 추가할 수 있습니다.",403);
        using var db=store.Open();using var tx=db.BeginTransaction();
        using(var initialize=PortalStore.Command(db,"INSERT OR IGNORE INTO FamilyCodeAttempts(ParentId) VALUES($id)",tx,("$id",parent)))initialize.ExecuteNonQuery();
        using(var lockCheck=PortalStore.Command(db,"SELECT LockedUntil FROM FamilyCodeAttempts WHERE ParentId=$id",tx,("$id",parent)))
            if((long)lockCheck.ExecuteScalar()!>Now)return Error("고유번호 입력을 여러 번 실패했습니다. 15분 후 다시 시도하세요.",429);
        using(var expired=PortalStore.Command(db,"UPDATE FamilyCodeAttempts SET Failures=0,LockedUntil=0 WHERE ParentId=$id AND LockedUntil>0 AND LockedUntil<=$now",tx,("$id",parent),("$now",Now)))expired.ExecuteNonQuery();
        string? student=null;
        if(request.Code is{Length:8} code&&code.All(Alphabet.Contains))
        {
            using var find=PortalStore.Command(db,"SELECT s.Id FROM StudentCodes k JOIN People s ON s.Id=k.StudentId WHERE k.Hash=$hash AND k.Active=1 AND s.Role='student' AND s.Active=1 AND s.ClassId IS NOT NULL",tx,("$hash",Hash(code)));
            student=find.ExecuteScalar() as string;
        }
        if(student is null)
        {
            using var fail=PortalStore.Command(db,"UPDATE FamilyCodeAttempts SET Failures=Failures+1,LockedUntil=CASE WHEN Failures+1>=5 THEN $until ELSE 0 END WHERE ParentId=$id",tx,("$until",Now+900_000),("$id",parent));fail.ExecuteNonQuery();tx.Commit();
            return Error("고유번호를 확인하세요. 영문 대소문자를 구분하며 승인된 재학생의 번호만 사용할 수 있습니다.");
        }
        var mode=c.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()?"CodeOnly":c.RequestServices.GetRequiredService<IConfiguration>()["Portal:GuardianBindingMode"]??"SchoolReview";
        if(mode!="SchoolReview"&&mode!="CodeOnly")return Error("학부모 연결 정책 설정을 확인하세요.",503);
        if(mode=="CodeOnly"&&!c.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()&&!c.RequestServices.GetRequiredService<IConfiguration>().GetValue("Portal:GuardianCodeOnlyApproved",false))return Error("학교의 학부모 본인확인 정책 승인이 필요합니다.",503);
        using(var reset=PortalStore.Command(db,"UPDATE FamilyCodeAttempts SET Failures=0,LockedUntil=0 WHERE ParentId=$id",tx,("$id",parent)))reset.ExecuteNonQuery();
        if(mode=="SchoolReview"&&store.Query("SELECT 1 FROM Families WHERE ParentId=$parent AND StudentId=$student",r=>r.GetInt32(0),("$parent",parent),("$student",student)).Count==0)
        {
            using var pending=PortalStore.Command(db,"INSERT INTO GuardianRequests(ParentId,StudentId,CodeHash,RequestedAt,State) VALUES($parent,$student,$hash,$now,'pending') ON CONFLICT(ParentId,StudentId) DO UPDATE SET CodeHash=excluded.CodeHash,RequestedAt=excluded.RequestedAt,State='pending',ReviewedBy=NULL,ReviewedAt=NULL",tx,("$parent",parent),("$student",student),("$hash",Hash(request.Code!)),("$now",Now));pending.ExecuteNonQuery();
            BackendOperations.Audit(db,tx,parent,"guardian.request","pending",c.TraceIdentifier);tx.Commit();return Results.Ok(new{membership="pending",message="자녀 연결을 신청했습니다. 학교가 보호자 관계를 확인하면 이용할 수 있습니다."});
        }
        using(var add=PortalStore.Command(db,"INSERT OR IGNORE INTO Families(ParentId,StudentId,Stamp) VALUES($parent,$student,lower(hex(randomblob(16))))",tx,("$parent",parent),("$student",student)))add.ExecuteNonQuery();
        BackendOperations.Audit(db,tx,parent,"guardian.link","success",c.TraceIdentifier);
        tx.Commit();return Results.Ok(new{membership="regular",message="자녀가 연결되었습니다. 정회원으로 이용할 수 있습니다."});
    }
    public static void Map(WebApplication app,RouteGroupBuilder api,RouteGroupBuilder admin,PortalStore store,IDataProtectionProvider protection)
    {
        app.MapGet("/api/registration/classes",()=>store.DirectorySnapshot().Classes).RequireRateLimiting("login");
        api.MapGet("/settings",(HttpContext c)=>
        {
            var person=store.Person(Id(c))!;
            if(person.Role is not("student"or"parent"))return Error("학생·학부모 설정입니다.",403);
            return Results.Ok(new{membership=store.Regular(person)?"regular":"temporary",studentCode=person.Role=="student"?Code(store,protection,person.Id):null});
        });
        api.MapPost("/settings/children",(HttpContext c,FamilyCode request)=>Link(store,c,request)).RequireRateLimiting("family-code");
        admin.MapGet("/guardian-requests",()=>store.Query("SELECT ParentId,StudentId,RequestedAt FROM GuardianRequests WHERE State='pending' ORDER BY RequestedAt",r=>new{parentId=r.GetString(0),studentId=r.GetString(1),requestedAt=r.GetInt64(2)}));
        admin.MapPost("/guardian-requests/{parent}/{student}/approve",(string parent,string student,HttpContext c)=>
        {
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            using var valid=PortalStore.Command(db,"SELECT 1 FROM GuardianRequests g JOIN People p ON p.Id=g.ParentId JOIN People s ON s.Id=g.StudentId JOIN StudentCodes k ON k.StudentId=s.Id AND k.Hash=g.CodeHash AND k.Active=1 WHERE g.ParentId=$parent AND g.StudentId=$student AND g.State='pending' AND g.RequestedAt>$cutoff AND p.Role='parent' AND p.Active=1 AND s.Role='student' AND s.Active=1 AND s.ClassId IS NOT NULL",tx,("$parent",parent),("$student",student),("$cutoff",Now-7*86_400_000L));if(valid.ExecuteScalar() is null)return Error("신청이 만료되었거나 학생 번호·계정이 변경되었습니다. 다시 신청하세요.",409);
            using(var add=PortalStore.Command(db,"INSERT OR IGNORE INTO Families(ParentId,StudentId,Stamp) VALUES($parent,$student,lower(hex(randomblob(16))))",tx,("$parent",parent),("$student",student)))add.ExecuteNonQuery();
            using(var update=PortalStore.Command(db,"UPDATE GuardianRequests SET State='approved',ReviewedBy=$admin,ReviewedAt=$now WHERE ParentId=$parent AND StudentId=$student",tx,("$admin",Id(c)),("$now",Now),("$parent",parent),("$student",student)))update.ExecuteNonQuery();
            BackendOperations.Audit(db,tx,Id(c),"guardian.approve","success",c.TraceIdentifier);tx.Commit();return Results.Ok();
        });
        admin.MapDelete("/guardian-requests/{parent}/{student}",(string parent,string student,HttpContext c)=>store.Execute("UPDATE GuardianRequests SET State='rejected',ReviewedBy=$admin,ReviewedAt=$now WHERE ParentId=$parent AND StudentId=$student AND State='pending'",("$admin",Id(c)),("$now",Now),("$parent",parent),("$student",student))==1?Results.Ok():Results.NotFound());
        admin.MapGet("/people/{id}/student-code",(string id)=>store.Person(id) is{Role:"student",Active:true}?Results.Ok(new{code=Code(store,protection,id)}):Error("학교가 승인한 재학생의 번호만 확인할 수 있습니다."));
        admin.MapPost("/people/{id}/student-code/reissue",(string id)=>store.Person(id) is{Role:"student",Active:true}?Results.Ok(new{code=Code(store,protection,id,true)}):Error("학교가 승인한 재학생의 번호만 재발급할 수 있습니다."));
    }
}

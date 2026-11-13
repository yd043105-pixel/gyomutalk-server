using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public static class StaffAccounts
{
    // Keep the existing wire/DB field name for upgrades; it now means the initial password is still in use.
    public const string SharedPassword="12345678";
    public record Profile(string UserId, string? SeatIp, bool NeedsActivation, long ExpiresAt,bool SharedPassword=false);
    public static void Initialize(Store store)
    {
        store.Execute("""
        CREATE TABLE IF NOT EXISTS StaffProfiles(UserId TEXT PRIMARY KEY REFERENCES Users(Id),SeatIp TEXT UNIQUE,
          NeedsActivation INTEGER NOT NULL,ActivationHash TEXT,ExpiresAt INTEGER NOT NULL,Failures INTEGER NOT NULL DEFAULT 0,LockedUntil INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS OfficeAudit(Id INTEGER PRIMARY KEY AUTOINCREMENT,ActorId TEXT NOT NULL REFERENCES Users(Id),
          Action TEXT NOT NULL,ResourceId TEXT NOT NULL,CreatedAt INTEGER NOT NULL);
        """);
        if(!store.Query("PRAGMA table_info(StaffProfiles)",r=>r.GetString(1)).Contains("SharedPassword"))store.Execute("ALTER TABLE StaffProfiles ADD COLUMN SharedPassword INTEGER NOT NULL DEFAULT 0");
    }
    public static Profile? Get(Store store, string id) => store.Query("SELECT UserId,SeatIp,NeedsActivation,ExpiresAt,SharedPassword FROM StaffProfiles WHERE UserId=$id",
        r=>new Profile(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetBoolean(2),r.GetInt64(3),r.GetBoolean(4)),("$id",id)).SingleOrDefault();
    // One rule for every password a person chooses: 8 to 128 characters, and not the initial password everyone knows.
    public static bool PersonalPassword(string? password)=>password is{Length:>=8 and <=128}&&!string.IsNullOrWhiteSpace(password)&&password!=SharedPassword;
    public static bool PasswordValid(StaffAccountRequest r)=>r.UseSharedPassword?r.TemporaryPassword==SharedPassword:PersonalPassword(r.TemporaryPassword);
    public static bool Pending(Store store, string id) => Get(store,id) is{NeedsActivation:true}or{SharedPassword:true};
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool LoginName(string? value) => value is {Length:>=2 and <=50} && Regex.IsMatch(value,"^[\\p{L}\\p{N}._-]+$");
    public static bool Position(string? value)=>value is not null&&value.Length<=50&&!value.Any(char.IsControl);
    public static void Audit(SqliteConnection db, SqliteTransaction tx, string actor, string action, string id)
    { using var c=Store.Command(db,"INSERT INTO OfficeAudit(ActorId,Action,ResourceId,CreatedAt) VALUES($actor,$action,$id,$now)",tx,("$actor",actor),("$action",action),("$id",id),("$now",Maintenance.Now));c.ExecuteNonQuery(); }
    static string Actor(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static IResult Error(string message,int status=400)=>Results.Json(new{error=message},statusCode:status);
    // The first password is known to everyone, so an account nobody has signed in to stops accepting it after this long.
    public const int FirstSignInDays=30;
    public static void Map(WebApplication app, RouteGroupBuilder admin, Store store, PasswordHasher<User> hasher)
    {
        admin.MapGet("/staff",()=>store.Users().Select(u=>new{u.Id,u.Username,u.Name,u.Department,u.Position,u.Active,profile=Get(store,u.Id)}));
        admin.MapPost("/staff",IResult(StaffAccountRequest request,HttpContext context)=>
        {
            if(!LoginName(request.LoginName)||string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>50||string.IsNullOrWhiteSpace(request.Department)||request.Department.Length>50||!Position(request.Position)||!PasswordValid(request))return Error("로그인 이름(2~50자), 이름·부서·직책(50자)을 확인하세요. 공통 비밀번호는 12345678, 개별 임시 비밀번호는 8~128자입니다.");
            string? ip=null;
            if(!string.IsNullOrWhiteSpace(request.SeatIp)){if(!IPAddress.TryParse(request.SeatIp,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.Broadcast))return Error("좌석에는 유효한 IPv4 주소를 입력하세요.");ip=address.ToString();}
            var id=Guid.NewGuid().ToString("N");var expires=Maintenance.Now+FirstSignInDays*86_400_000L;
            var user=new User(id,request.LoginName,request.Name.Trim(),request.Department.Trim(),"",false,false,true,1,0,0);
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            try
            {
                using(var insert=Store.Command(db,"INSERT INTO Users(Id,Username,Name,Department,Position,PasswordHash,IsAdmin,CanBroadcast) VALUES($id,$login,$name,$department,$position,$hash,0,0)",tx,("$id",id),("$login",user.Username),("$name",user.Name),("$department",user.Department),("$position",request.Position.Trim()),("$hash",hasher.HashPassword(user,request.TemporaryPassword))))insert.ExecuteNonQuery();
                using(var insert=Store.Command(db,"INSERT INTO StaffProfiles(UserId,SeatIp,NeedsActivation,ActivationHash,ExpiresAt,SharedPassword) VALUES($id,$ip,1,NULL,$expiry,$shared)",tx,("$id",id),("$ip",ip),("$expiry",expires),("$shared",request.UseSharedPassword)))insert.ExecuteNonQuery();
                Audit(db,tx,Actor(context),"staff.create",id);tx.Commit();return Results.Ok(new{id,expiresAt=expires});
            }
            catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("이미 등록된 로그인 이름 또는 좌석 IP입니다. 동명이인은 로그인 이름을 구분하세요.",409);}
        });
        admin.MapPost("/staff/{id}/reset",IResult(string id,StaffAccountRequest request,HttpContext context)=>
        {
            var user=store.GetUser(id);if(user is null||user.IsAdmin)return Results.NotFound();
            if(!PasswordValid(request)||!LoginName(request.LoginName))return Error("로그인 이름과 비밀번호 정책을 확인하세요.");
            if(request.UseSharedPassword&&store.FindUser(request.LoginName) is{} duplicate&&duplicate.Id!=id)return Error("동명이인은 이름 뒤에 .2 등을 붙여 구분하세요.",409);
            var expires=Maintenance.Now+FirstSignInDays*86_400_000L;
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            using(var profile=Store.Command(db,"INSERT OR IGNORE INTO StaffProfiles(UserId,NeedsActivation,ExpiresAt) VALUES($id,1,0)",tx,("$id",id)))profile.ExecuteNonQuery();
            try{using var update=Store.Command(db,"UPDATE Users SET Username=$login,PasswordHash=$hash,SessionVersion=SessionVersion+1,FailedAttempts=0,LockedUntil=0 WHERE Id=$id",tx,("$login",request.UseSharedPassword?request.LoginName:user.Username),("$hash",hasher.HashPassword(user,request.TemporaryPassword)),("$id",id));update.ExecuteNonQuery();}
            catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("이미 등록된 로그인 이름입니다.",409);}
            using(var update=Store.Command(db,"UPDATE StaffProfiles SET NeedsActivation=1,ActivationHash=NULL,ExpiresAt=$expiry,Failures=0,LockedUntil=0,SharedPassword=$shared WHERE UserId=$id",tx,("$expiry",expires),("$id",id),("$shared",request.UseSharedPassword)))update.ExecuteNonQuery();
            Audit(db,tx,Actor(context),"staff.reset",id);tx.Commit();return Results.Ok(new{expiresAt=expires});
        });
        admin.MapPost("/staff/{id}/seat",IResult(string id,StaffSeatRequest request,HttpContext context)=>
        {
            if(store.GetUser(id) is null)return Results.NotFound();string? ip=null;
            if(!string.IsNullOrWhiteSpace(request.SeatIp)){if(!IPAddress.TryParse(request.SeatIp,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.Broadcast))return Error("좌석 IPv4 주소를 확인하세요.");ip=address.ToString();}
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            try{using var update=Store.Command(db,"INSERT INTO StaffProfiles(UserId,SeatIp,NeedsActivation,ExpiresAt) VALUES($id,$ip,0,0) ON CONFLICT(UserId) DO UPDATE SET SeatIp=$ip",tx,("$ip",ip),("$id",id));update.ExecuteNonQuery();Audit(db,tx,Actor(context),"staff.seat",id);tx.Commit();return Results.Ok();}
            catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("다른 교사에게 연결된 좌석 IP입니다. 기존 연결을 먼저 해제하세요.",409);}
        });
        app.MapPost("/api/activate",async Task<IResult>(StaffActivation request,HttpContext context)=>
        {
            var id=Actor(context);var user=store.GetUser(id)!;
            if(!PersonalPassword(request.NewPassword))return Error("개인 비밀번호는 8~128자이고, 초기 비밀번호와 달라야 합니다.");
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            // The first sign-in needs no separate number: the person sets a password of their own.
            using var read=Store.Command(db,"SELECT ExpiresAt FROM StaffProfiles WHERE UserId=$id AND NeedsActivation=1",tx,("$id",id));
            if(read.ExecuteScalar() is not long expires)return Error("초기 설정 대상이 아닙니다.",409);
            if(expires<=Maintenance.Now)return Error("초기 비밀번호를 쓸 수 있는 기간이 지났습니다. 관리자에게 계정 초기화를 요청하세요.",410);
            // Reject invalid/locked codes before spending CPU on PBKDF2 password work.
            if(hasher.VerifyHashedPassword(user,user.PasswordHash,request.NewPassword)!=PasswordVerificationResult.Failed)return Error("임시 비밀번호와 다른 개인 비밀번호를 설정하세요.");
            var passwordHash=hasher.HashPassword(user,request.NewPassword);
            using(var update=Store.Command(db,"UPDATE Users SET PasswordHash=$hash,SessionVersion=SessionVersion+1 WHERE Id=$id AND Active=1 AND SessionVersion=$version AND PasswordHash=$old",tx,("$hash",passwordHash),("$id",id),("$version",context.User.FindFirstValue("version")),("$old",user.PasswordHash)))if(update.ExecuteNonQuery()!=1)return Error("계정 상태가 변경되었습니다. 다시 로그인하세요.",409);
            using(var finish=Store.Command(db,"UPDATE StaffProfiles SET NeedsActivation=0,SharedPassword=0,ActivationHash=NULL,Failures=0,LockedUntil=0 WHERE UserId=$id",tx,("$id",id)))finish.ExecuteNonQuery();
            Audit(db,tx,id,"staff.activate",id);tx.Commit();await context.SignOutAsync();return Results.Ok(new{message="초기 설정 완료. 개인 비밀번호로 다시 로그인하세요."});
        }).RequireAuthorization().RequireRateLimiting("credentials");
    }
}

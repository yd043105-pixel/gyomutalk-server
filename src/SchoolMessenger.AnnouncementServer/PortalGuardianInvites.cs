using SchoolMessenger.Shared;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using QRCoder;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public record GuardianInviteRequest(string? Address,string? Pin);

// A signed-in student shows a QR code to a guardian standing next to them. The guardian's 학교톡 opens from the code,
// signs up (or, already signed in, adds the child) and is linked at once: holding the student's signed-in phone is the
// check that replaces the school's review of a typed family code. An invite works once and only for ten minutes.
public static class PortalGuardianInvites
{
    const string Alphabet="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    const long Lifetime=600_000;
    const string Expired="초대가 만료되었거나 이미 사용되었습니다. 학생 앱에서 새 QR 코드를 만들어 주세요.";

    public static void Initialize(PortalStore store)=>store.Execute("CREATE TABLE IF NOT EXISTS GuardianInvites(Hash TEXT PRIMARY KEY,StudentId TEXT NOT NULL,ExpiresAt INTEGER NOT NULL,UsedAt INTEGER,UsedBy TEXT)");

    // The QR carries the address the student's app uses and, for a self-issued certificate, the fingerprint it trusts,
    // so the guardian's phone connects to the same server without comparing the connection code by hand.
    public static string Link(string address,string token,string? pin)=>"schooltalk://guardian?server="+Uri.EscapeDataString(address)+"&invite="+token+(pin is null?"":"&pin="+pin);

    static IResult Create(PortalStore store,HttpContext c,GuardianInviteRequest request)
    {
        if(store.Person(Id(c)) is not{Role:"student",Active:true,ClassId:not null} student)return Error("학교가 승인한 재학생 계정에서만 보호자 초대를 만들 수 있습니다.",403);
        if(!Uri.TryCreate(request.Address,UriKind.Absolute,out var address)||address.Scheme is not("https"or"http")||request.Address!.Length>200||address.UserInfo.Length>0||address.Query.Length>0||address.Fragment.Length>0)return Error("학교 서버 주소를 확인하세요.");
        var pin=request.Pin is{Length:>0}?request.Pin.ToUpperInvariant():null;
        if(pin is not null&&(pin.Length!=64||!pin.All(Uri.IsHexDigit)))return Error("연결 확인 정보를 확인하세요.");
        var token=RandomNumberGenerator.GetString(Alphabet,24);var expiresAt=Now+Lifetime;
        using(var db=store.Open())
        {
            using var tx=db.BeginTransaction();
            // Only the newest invite of a student is valid; expired and used rows are dropped on the way.
            using(var old=PortalStore.Command(db,"DELETE FROM GuardianInvites WHERE (StudentId=$student AND UsedAt IS NULL) OR ExpiresAt<$now",tx,("$student",student.Id),("$now",Now)))old.ExecuteNonQuery();
            using(var add=PortalStore.Command(db,"INSERT INTO GuardianInvites(Hash,StudentId,ExpiresAt) VALUES($hash,$student,$expires)",tx,("$hash",Hash(token)),("$student",student.Id),("$expires",expiresAt)))add.ExecuteNonQuery();
            BackendOperations.Audit(db,tx,student.Id,"guardian.invite","created",c.TraceIdentifier);tx.Commit();
        }
        var link=Link(address.GetLeftPart(UriPartial.Authority),token,pin);
        using var data=new QRCodeGenerator().CreateQrCode(link,QRCodeGenerator.ECCLevel.M);
        return Results.Ok(new{link,expiresAt,qr=Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(10))});
    }

    // Lets the guardian see whose guardian they are about to become before signing up.
    static IResult Preview(PortalStore store,string token)
    {
        var found=store.Query("SELECT s.Name,c.Name FROM GuardianInvites g JOIN People s ON s.Id=g.StudentId JOIN Classes c ON c.Id=s.ClassId WHERE g.Hash=$hash AND g.UsedAt IS NULL AND g.ExpiresAt>$now AND s.Role='student' AND s.Active=1",
            r=>new{studentName=r.GetString(0),className=r.GetString(1)},("$hash",Hash(token)),("$now",Now));
        return found.Count==1?Results.Ok(found[0]):Error(Expired,404);
    }

    // Spends the invite and links the guardian inside the caller's transaction; null when the invite cannot be used.
    public static string? Use(SqliteConnection db,SqliteTransaction tx,string? token,string parent,string trace)
    {
        if(token is not{Length:24}||!token.All(Alphabet.Contains))return null;
        using var spend=PortalStore.Command(db,"UPDATE GuardianInvites SET UsedAt=$now,UsedBy=$parent WHERE Hash=$hash AND UsedAt IS NULL AND ExpiresAt>$now AND EXISTS(SELECT 1 FROM People s WHERE s.Id=GuardianInvites.StudentId AND s.Role='student' AND s.Active=1 AND s.ClassId IS NOT NULL) RETURNING StudentId",
            tx,("$now",Now),("$parent",parent),("$hash",Hash(token)));
        if(spend.ExecuteScalar() is not string student)return null;
        using(var link=PortalStore.Command(db,"INSERT OR IGNORE INTO Families(ParentId,StudentId,Stamp) VALUES($parent,$student,lower(hex(randomblob(16))))",tx,("$parent",parent),("$student",student)))link.ExecuteNonQuery();
        using(var settled=PortalStore.Command(db,"UPDATE GuardianRequests SET State='approved',ReviewedBy=$parent,ReviewedAt=$now WHERE ParentId=$parent AND StudentId=$student AND State='pending'",tx,("$parent",parent),("$student",student),("$now",Now)))settled.ExecuteNonQuery();
        BackendOperations.Audit(db,tx,parent,"guardian.link","invite",trace);
        return student;
    }

    // An already signed-in guardian adds another child from the student's QR code.
    static IResult LinkSignedIn(PortalStore store,HttpContext c,string? token)
    {
        var parent=Id(c);if(store.Person(parent) is not{Role:"parent",Active:true})return Error("학부모 계정에서만 자녀를 추가할 수 있습니다.",403);
        using var db=store.Open();using var tx=db.BeginTransaction();
        var student=Use(db,tx,token,parent,c.TraceIdentifier);if(student is null)return Error(Expired);
        tx.Commit();var name=store.Person(student)!.Name;
        return Results.Ok(new{membership="regular",message=name+" 학생과 연결되었습니다."});
    }

    public static void Map(WebApplication app,RouteGroupBuilder api,PortalStore store)
    {
        Initialize(store);
        api.MapPost("/settings/guardian-invite",(HttpContext c,GuardianInviteRequest request)=>Create(store,c,request)).RequireRateLimiting("family-code");
        api.MapPost("/settings/children/invite",(HttpContext c,FamilyCode request)=>LinkSignedIn(store,c,request.Code)).RequireRateLimiting("family-code");
        app.MapGet("/api/registration/guardian-invite/{token}",(string token)=>Preview(store,token)).RequireRateLimiting("login");
    }
}

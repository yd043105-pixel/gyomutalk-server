using SchoolMessenger.Shared;
using Microsoft.Data.Sqlite;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalGraduation
{
    static readonly object Gate=new();
    record Schedule(string[] StudentIds,long At,string Confirmation);
    record Acknowledge(string[] Ids);
    public static void Initialize(PortalStore store)=>store.Execute("""
        CREATE TABLE IF NOT EXISTS Graduations(StudentId TEXT PRIMARY KEY REFERENCES People(Id),At INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS PrivacyErasure(Id TEXT PRIMARY KEY,PublicDone INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS RetiredStudentCodes(Hash TEXT PRIMARY KEY);
        """);
    public static bool Expired(PortalStore store,string id)=>store.Query("""
        SELECT 1 FROM Graduations WHERE StudentId=$id AND At<=$now
        UNION ALL SELECT 1 FROM PrivacyErasure WHERE Id=$id
        UNION ALL SELECT 1 FROM People p WHERE p.Id=$id AND p.Role='parent'
          AND EXISTS(SELECT 1 FROM Families f JOIN Graduations g ON g.StudentId=f.StudentId WHERE f.ParentId=p.Id AND g.At<=$now)
          AND NOT EXISTS(SELECT 1 FROM Families f JOIN People s ON s.Id=f.StudentId
            LEFT JOIN Graduations g ON g.StudentId=s.Id WHERE f.ParentId=p.Id AND s.Role='student' AND (g.At IS NULL OR g.At>$now))
        LIMIT 1
        """,r=>r.GetInt32(0),("$id",id),("$now",Now)).Count>0;
    public static void Map(RouteGroupBuilder admin,RouteGroupBuilder bridge,PortalStore store)
    {
        admin.MapGet("/graduations",()=>new{scheduled=store.Query("SELECT g.StudentId,p.Name,c.Name,g.At FROM Graduations g JOIN People p ON p.Id=g.StudentId LEFT JOIN Classes c ON c.Id=p.ClassId ORDER BY g.At,p.Name",r=>new{studentId=r.GetString(0),name=r.GetString(1),className=r.IsDBNull(2)?"":r.GetString(2),at=r.GetInt64(3)}),pendingErasure=store.Query("SELECT COUNT(*) FROM PrivacyErasure",r=>r.GetInt32(0)).Single()});
        admin.MapPost("/graduations",(Schedule request)=>
        {
            if(request.StudentIds is not{Length:>=1 and <=2000}||request.StudentIds.Distinct().Count()!=request.StudentIds.Length||request.StudentIds.Any(id=>!Guid.TryParseExact(id,"N",out _))||request.At<=Now||request.At>Now+5*366*86_400_000L||request.Confirmation!="졸업 자동 탈퇴 및 파기")return Error("재학생과 앞으로 5년 이내의 졸업 처리 시각을 확인하고 파기 확인 문구를 입력하세요.");
            lock(Gate)
            {
                using var db=store.Open();using var tx=db.BeginTransaction();
                foreach(var id in request.StudentIds)
                {
                    using var check=PortalStore.Command(db,"SELECT 1 FROM People WHERE Id=$id AND Role='student'",tx,("$id",id));if(check.ExecuteScalar() is null)return Error("학교가 확인한 학생 계정만 졸업 예약할 수 있습니다.",409);
                    using var c=PortalStore.Command(db,"INSERT INTO Graduations VALUES($id,$at) ON CONFLICT(StudentId) DO UPDATE SET At=excluded.At",tx,("$id",id),("$at",request.At));c.ExecuteNonQuery();
                }
                tx.Commit();return Results.Ok(new{scheduled=request.StudentIds.Length});
            }
        });
        admin.MapDelete("/graduations/{id}",(string id)=>{lock(Gate){return store.Execute("DELETE FROM Graduations WHERE StudentId=$id AND At>$now",("$id",id),("$now",Now))==1?Results.Ok():Error("처리 시각이 지난 졸업 예약은 되돌릴 수 없습니다.",409);}});
        bridge.MapGet("/privacy-erasure",()=>store.Query("SELECT Id FROM PrivacyErasure WHERE PublicDone=1 LIMIT 2000",r=>r.GetString(0)).ToArray());
        bridge.MapPost("/privacy-erasure/ack",(Acknowledge request)=>
        {
            if(request.Ids is not{Length:>=1 and <=2000}||request.Ids.Any(id=>!Guid.TryParseExact(id,"N",out _)))return Error("파기 확인 대상을 확인하세요.");
            lock(Gate){foreach(var id in request.Ids)store.Execute("DELETE FROM PrivacyErasure WHERE Id=$id AND PublicDone=1",("$id",id));}return Results.Ok();
        });
    }
    public static void Run(PortalStore store)
    {
        lock(Gate)
        lock(AttachmentBackup.Gate)
        {
            using(var db=store.Open())using(var tx=db.BeginTransaction())
            {
                var ids=new HashSet<string>();
                using(var c=PortalStore.Command(db,"SELECT StudentId FROM Graduations WHERE At<=$now",tx,("$now",Now)))using(var r=c.ExecuteReader())while(r.Read())ids.Add(r.GetString(0));
                if(ids.Count>0)
                {
                    var parents=new HashSet<string>();
                    foreach(var id in ids){using var c=PortalStore.Command(db,"SELECT ParentId FROM Families WHERE StudentId=$id",tx,("$id",id));using var r=c.ExecuteReader();while(r.Read())parents.Add(r.GetString(0));}
                    foreach(var parent in parents)
                    {
                        var children=new List<string>();using(var c=PortalStore.Command(db,"SELECT f.StudentId FROM Families f JOIN People s ON s.Id=f.StudentId WHERE f.ParentId=$id AND s.Role='student'",tx,("$id",parent)))using(var r=c.ExecuteReader())while(r.Read())children.Add(r.GetString(0));
                        if(children.Count>0&&children.All(ids.Contains))ids.Add(parent);
                    }
                    foreach(var id in ids)
                    {
                        using(var job=PortalStore.Command(db,"INSERT OR IGNORE INTO PrivacyErasure(Id) VALUES($id)",tx,("$id",id)))job.ExecuteNonQuery();
                        using(var stop=PortalStore.Command(db,"UPDATE People SET Active=0,Version=Version+1 WHERE Id=$id",tx,("$id",id)))stop.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            var pending=store.Query("SELECT Id FROM PrivacyErasure WHERE PublicDone=0",r=>r.GetString(0)).ToArray();
            if(pending.Length==0)return;
            using(var db=store.Open()){GraduationErasure.Portal(db,pending);GraduationErasure.Compact(db);}
            GraduationErasure.Backups(store.Root,pending,true,store.BackupDirectory);
            foreach(var id in pending)store.Execute("UPDATE PrivacyErasure SET PublicDone=1 WHERE Id=$id",("$id",id));
        }
    }
    public static void ProcessDue(PortalStore store)
    {
        if(store.Query("SELECT 1 FROM Graduations WHERE At<=$now LIMIT 1",r=>r.GetInt32(0),("$now",Now)).Count>0)Run(store);
    }
}

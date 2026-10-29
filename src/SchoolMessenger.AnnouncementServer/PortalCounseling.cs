using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalCounseling
{
    public static void Initialize(PortalStore store) => store.Execute("""
        CREATE TABLE IF NOT EXISTS CounselingSlots(
          Id TEXT PRIMARY KEY,TeacherId TEXT NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
          ClassId TEXT NOT NULL REFERENCES Classes(Id),StartsAt INTEGER NOT NULL,EndsAt INTEGER NOT NULL,
          OpensAt INTEGER NOT NULL,Closed INTEGER NOT NULL DEFAULT 0,ClientId TEXT NOT NULL,PeriodId INTEGER NOT NULL,RequestHash TEXT NOT NULL,
          UNIQUE(TeacherId,StartsAt),UNIQUE(TeacherId,ClientId,PeriodId));
        CREATE INDEX IF NOT EXISTS IX_CounselingSlots_ClassDate ON CounselingSlots(ClassId,StartsAt);
        CREATE INDEX IF NOT EXISTS IX_CounselingSlots_Expiry ON CounselingSlots(EndsAt);
        CREATE TABLE IF NOT EXISTS CounselingOpenRequests(TeacherId TEXT NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
          ClientId TEXT NOT NULL,RequestHash TEXT NOT NULL,Result TEXT NOT NULL,EndsAt INTEGER NOT NULL,PRIMARY KEY(TeacherId,ClientId));
        CREATE TABLE IF NOT EXISTS CounselingBookings(
          Id TEXT PRIMARY KEY,SlotId TEXT NOT NULL REFERENCES CounselingSlots(Id) ON DELETE CASCADE,
          StudentId TEXT NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
          RequesterId TEXT NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
          ClientId TEXT NOT NULL,RequestHash TEXT NOT NULL,CreatedAt INTEGER NOT NULL,CanceledAt INTEGER,Active INTEGER NOT NULL DEFAULT 1,
          UNIQUE(RequesterId,ClientId));
        CREATE UNIQUE INDEX IF NOT EXISTS IX_CounselingBookings_Slot ON CounselingBookings(SlotId) WHERE Active=1;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_CounselingBookings_Student ON CounselingBookings(StudentId) WHERE Active=1;
        CREATE INDEX IF NOT EXISTS IX_CounselingBookings_StudentHistory ON CounselingBookings(StudentId,SlotId);
        """);

    public static void Cleanup(PortalStore store)
    {
        // Cascades remove reservation identifiers with expired slots; no additional worker is needed.
        store.Execute("DELETE FROM CounselingSlots WHERE EndsAt<$cutoff",("$cutoff",DateTimeOffset.UtcNow.AddMonths(-6).ToUnixTimeMilliseconds()));
        store.Execute("DELETE FROM CounselingOpenRequests WHERE EndsAt<$cutoff",("$cutoff",DateTimeOffset.UtcNow.AddMonths(-6).ToUnixTimeMilliseconds()));
    }
    static bool Key(string? value) => value is not null && Guid.TryParseExact(value,"N",out _);
    static IResult Safe(Func<IResult> action)
    {
        try { return action(); }
        catch(SqliteException e) when(e.SqliteErrorCode==19) { return Error("예약 또는 상담 시간이 변경되었습니다. 새로고침 후 다시 선택하세요.",409); }
        catch(SqliteException e) when(e.SqliteErrorCode is 5 or 6) { return Error("다른 예약을 처리 중입니다. 같은 요청으로 다시 시도하세요.",503); }
    }
    static PortalClass[] TeacherClasses(PortalStore store, Account teacher) => VerifiedTeacher(store,teacher)
        ? store.Query("SELECT c.Id,c.Name,c.Grade FROM Classes c JOIN TeacherClasses t ON t.ClassId=c.Id WHERE t.TeacherId=$id ORDER BY c.Grade,c.Name",r=>new PortalClass(r.GetString(0),r.GetString(1),r.GetInt32(2)),("$id",teacher.Id)).ToArray() : [];
    static CounselingStudent[] Students(PortalStore store, Account user)
    {
        return store.Query("""
            SELECT p.Id,p.Name,p.ClassId,c.Name FROM People p JOIN Classes c ON c.Id=p.ClassId WHERE p.Role='student' AND p.Active=1
            AND (($role='student' AND p.Id=$id) OR ($role='parent' AND EXISTS(SELECT 1 FROM Families f WHERE f.ParentId=$id AND f.StudentId=p.Id)))
            """,r=>new CounselingStudent(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3)),("$role",user.Role),("$id",user.Id))
            .Where(s=>!PortalGraduation.Expired(store,s.Id)).ToArray();
    }
    static Account? Teacher(PortalStore store,string? teacherId) => Key(teacherId)&&store.Person(teacherId!) is { } teacher&&VerifiedTeacher(store,teacher)?teacher:null;

    public static void Map(RouteGroupBuilder api,RouteGroupBuilder bridge,PortalStore store)
    {
        Initialize(store);
        api=api.MapGroup("").RequireRateLimiting("counseling");
        bridge=bridge.MapGroup("").RequireRateLimiting("counseling");
        api.MapGet("/counseling",(string? date,HttpContext c)=>Safe(()=>View(store,store.Person(Id(c))!,date)));
        api.MapPost("/counseling/slots",(CounselingOpen r,HttpContext c)=>Safe(()=>Open(store,store.Person(Id(c))!,r)));
        api.MapPost("/counseling/slots/{id}/close",(string id,HttpContext c)=>Safe(()=>Close(store,store.Person(Id(c))!,id)));
        api.MapPost("/counseling/bookings",(CounselingReserve r,HttpContext c)=>Safe(()=>Reserve(store,store.Person(Id(c))!,r)));
        api.MapPost("/counseling/bookings/{id}/cancel",(string id,HttpContext c)=>Safe(()=>Cancel(store,store.Person(Id(c))!,id)));
        // These routes also pass through the existing HMAC timestamp/nonce/body verification.
        bridge.MapGet("/counseling",(string? teacherId,string? date)=>Safe(()=>Teacher(store,teacherId) is { } t?View(store,t,date):Error("교직원 연결을 확인하세요.",403)));
        bridge.MapPost("/counseling/slots",(string? teacherId,CounselingOpen r)=>Safe(()=>Teacher(store,teacherId) is { } t?Open(store,t,r):Error("교직원 연결을 확인하세요.",403)));
        bridge.MapPost("/counseling/slots/{id}/close",(string id,string? teacherId)=>Safe(()=>Teacher(store,teacherId) is { } t?Close(store,t,id):Error("교직원 연결을 확인하세요.",403)));
        bridge.MapPost("/counseling/bookings/{id}/cancel",(string id,string? teacherId)=>Safe(()=>Teacher(store,teacherId) is { } t?Cancel(store,t,id):Error("교직원 연결을 확인하세요.",403)));
    }

    static IResult View(PortalStore store,Account user,string? date)
    {
        if(!CounselingTimes.Day(date??CounselingTimes.Today,out var day)||Math.Abs(day.DayNumber-DateOnly.Parse(CounselingTimes.Today).DayNumber)>183)return Error("상담 날짜는 오늘부터 앞뒤 6개월 이내로 선택하세요.");
        if(user.Role is not("student" or "parent" or "teacher"))return Error("학생·학부모 또는 담당 교사 계정으로 이용하세요.",403);
        if(user.Role=="teacher"&&!VerifiedTeacher(store,user))return Error("교직원 연결을 확인하세요.",403);
        Cleanup(store);
        var manage=user.Role=="teacher";var students=Students(store,user);var classes=manage?TeacherClasses(store,user):store.Query("SELECT Id,Name,Grade FROM Classes ORDER BY Grade,Name",r=>new PortalClass(r.GetString(0),r.GetString(1),r.GetInt32(2))).Where(c=>students.Any(s=>s.ClassId==c.Id)).ToArray();
        var start=CounselingTimes.At(day,"00:00");var end=CounselingTimes.At(day.AddDays(1),"00:00");
        var slots=store.Query("""
            SELECT s.Id,t.Name,s.ClassId,c.Name,s.StartsAt,s.EndsAt,s.OpensAt,s.Closed,
              EXISTS(SELECT 1 FROM CounselingBookings b WHERE b.SlotId=s.Id AND b.Active=1),s.TeacherId
            FROM CounselingSlots s JOIN People t ON t.Id=s.TeacherId JOIN Classes c ON c.Id=s.ClassId
            WHERE s.StartsAt>=$start AND s.StartsAt<$end AND
              (($manage=1 AND s.TeacherId=$user) OR ($manage=0 AND EXISTS(SELECT 1 FROM People p WHERE p.Active=1 AND p.Role='student' AND p.ClassId=s.ClassId
                AND (p.Id=$user OR EXISTS(SELECT 1 FROM Families f WHERE f.ParentId=$user AND f.StudentId=p.Id)))))
            ORDER BY s.StartsAt LIMIT 500
            """,r=>(slot:new CounselingSlot(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6),r.GetBoolean(7),r.GetBoolean(8)),teacher:r.GetString(9)),("$start",start),("$end",end),("$manage",manage),("$user",user.Id))
            .Where(x=>classes.Any(c=>c.Id==x.slot.ClassId)&&(manage?x.teacher==user.Id:!x.slot.Closed&&x.slot.StartsAt>Now&&Teacher(store,x.teacher) is { } teacher&&store.Classes(teacher.Id).Contains(x.slot.ClassId)))
            .Select(x=>x.slot).ToArray();
        var bookings=store.Query("""
            SELECT b.Id,b.SlotId,b.StudentId,p.Name,c.Name,t.Name,s.StartsAt,s.EndsAt,b.CanceledAt,q.Role,s.TeacherId,s.ClassId
            FROM CounselingBookings b JOIN CounselingSlots s ON s.Id=b.SlotId JOIN People p ON p.Id=b.StudentId
            JOIN People q ON q.Id=b.RequesterId JOIN People t ON t.Id=s.TeacherId JOIN Classes c ON c.Id=s.ClassId
            WHERE (($manage=1 AND s.TeacherId=$user AND s.StartsAt>=$start AND s.StartsAt<$end) OR
              ($manage=0 AND s.EndsAt>$now AND (b.StudentId=$user OR EXISTS(SELECT 1 FROM Families f WHERE f.ParentId=$user AND f.StudentId=b.StudentId))))
            ORDER BY s.StartsAt LIMIT 500
            """,r=>(booking:new CounselingBooking(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.GetInt64(7),r.IsDBNull(8)?null:r.GetInt64(8),r.GetString(9)),teacher:r.GetString(10),room:r.GetString(11)),("$manage",manage),("$user",user.Id),("$start",start),("$end",end),("$now",Now))
            .Where(x=>manage?classes.Any(c=>c.Id==x.room)&&store.Person(x.booking.StudentId) is {Active:true} s&&s.ClassId==x.room:students.Any(s=>s.Id==x.booking.StudentId))
            .Select(x=>x.booking).ToArray();
        return Results.Ok(new CounselingView(manage,classes,students,slots,bookings));
    }

    static IResult Open(PortalStore store,Account teacher,CounselingOpen request)
    {
        if(!Key(request.ClientId)||!Key(request.ClassId)||!CounselingTimes.Day(request.Date,out var day)||request.PeriodIds is null||request.PeriodIds.Length is <1 or >10||request.PeriodIds.Distinct().Count()!=request.PeriodIds.Length)return Error("학급·날짜·시간을 확인하세요.");
        var today=DateOnly.Parse(CounselingTimes.Today);if(day<today||day>today.AddMonths(6))return Error("오늘부터 6개월 이내의 날짜를 선택하세요.");
        var fingerprint=Hash(JsonSerializer.Serialize(request));
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        var room=TeacherClasses(store,teacher).FirstOrDefault(c=>c.Id==request.ClassId);if(room is null)return Error("담당 학급의 상담 시간만 열 수 있습니다.",403);
        if(room.Grade is <1 or >3)return Error("관리자에게 학급의 학년 정보를 확인하세요.");
        var periods=request.PeriodIds.Select(id=>CounselingTimes.Periods(room.Grade).FirstOrDefault(p=>p.Id==id)).ToArray();if(periods.Any(p=>p is null))return Error("상담은 해당 학년의 1~8교시·야간1·야간2 중 선택하세요.");
        using(var q=PortalStore.Command(db,"SELECT Result,RequestHash FROM CounselingOpenRequests WHERE TeacherId=$teacher AND ClientId=$client",tx,("$teacher",teacher.Id),("$client",request.ClientId)))using(var reader=q.ExecuteReader())if(reader.Read())return reader.GetString(1)==fingerprint?Results.Ok(new{ids=JsonSerializer.Deserialize<string[]>(reader.GetString(0))}):Error("같은 요청 번호의 내용이 다릅니다.",409);
        var ids=new List<string>();foreach(var period in periods.OrderBy(p=>p!.Id))
        {
            var from=CounselingTimes.At(day,period!.Start);var to=CounselingTimes.At(day,period.End);var opens=request.OpensAt??Now;
            if(from<=Now||opens>from||opens<Now-60_000)return Error("지난 시간은 열 수 없고 예약 오픈은 상담 시작 전이어야 합니다.");
            using(var overlap=PortalStore.Command(db,"SELECT 1 FROM CounselingSlots WHERE TeacherId=$teacher AND Closed=0 AND StartsAt<$end AND EndsAt>$start",tx,("$teacher",teacher.Id),("$start",from),("$end",to)))if(overlap.ExecuteScalar() is not null)return Error("다른 학급 또는 이미 열린 상담 시간과 겹칩니다.",409);
            var id=Guid.NewGuid().ToString("N");using var add=PortalStore.Command(db,"""
                INSERT INTO CounselingSlots(Id,TeacherId,ClassId,StartsAt,EndsAt,OpensAt,ClientId,PeriodId,RequestHash)
                VALUES($id,$teacher,$class,$start,$end,$opens,$client,$period,$hash)
                ON CONFLICT(TeacherId,StartsAt) DO UPDATE SET ClassId=$class,OpensAt=$opens,Closed=0,ClientId=$client,PeriodId=$period,RequestHash=$hash
                WHERE CounselingSlots.Closed=1 AND CounselingSlots.ClassId=$class AND NOT EXISTS(SELECT 1 FROM CounselingBookings b WHERE b.SlotId=CounselingSlots.Id AND b.Active=1)
                """,tx,("$id",id),("$teacher",teacher.Id),("$class",request.ClassId),("$start",from),("$end",to),("$opens",opens),("$client",request.ClientId),("$period",period.Id),("$hash",fingerprint));
            if(add.ExecuteNonQuery()!=1)return Error("이미 열린 상담 시간입니다.",409);
            using var saved=PortalStore.Command(db,"SELECT Id FROM CounselingSlots WHERE TeacherId=$teacher AND StartsAt=$start",tx,("$teacher",teacher.Id),("$start",from));ids.Add((string)saved.ExecuteScalar()!);
        }
        using(var remember=PortalStore.Command(db,"INSERT INTO CounselingOpenRequests VALUES($teacher,$client,$hash,$result,$end)",tx,("$teacher",teacher.Id),("$client",request.ClientId),("$hash",fingerprint),("$result",JsonSerializer.Serialize(ids)),("$end",CounselingTimes.At(day,periods.MaxBy(p=>p!.Id)!.End))))remember.ExecuteNonQuery();
        tx.Commit();return Results.Ok(new{ids});
    }
    static IResult Close(PortalStore store,Account teacher,string id)
    {
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        var classes=TeacherClasses(store,teacher);using var query=PortalStore.Command(db,"SELECT ClassId,StartsAt FROM CounselingSlots WHERE Id=$id AND TeacherId=$teacher",tx,("$id",id),("$teacher",teacher.Id));
        string room;long starts;using(var reader=query.ExecuteReader()){if(!reader.Read()||!classes.Any(c=>c.Id==reader.GetString(0)))return Results.NotFound();room=reader.GetString(0);starts=reader.GetInt64(1);}
        if(starts<=Now)return Error("이미 시작된 상담 시간입니다.",409);
        using(var occupied=PortalStore.Command(db,"SELECT 1 FROM CounselingBookings WHERE SlotId=$id AND Active=1",tx,("$id",id)))if(occupied.ExecuteScalar() is not null)return Error("신청된 예약을 먼저 취소한 뒤 시간을 닫으세요.",409);
        using(var close=PortalStore.Command(db,"UPDATE CounselingSlots SET Closed=1 WHERE Id=$id",tx,("$id",id)))close.ExecuteNonQuery();tx.Commit();return Results.Ok();
    }
    static IResult Reserve(PortalStore store,Account user,CounselingReserve request)
    {
        if(!Key(request.ClientId)||!Key(request.SlotId)||!Key(request.StudentId)||request.ReplaceBookingId is not null&&!Key(request.ReplaceBookingId))return Error("상담 시간과 학생을 확인하세요.");
        var fingerprint=Hash(JsonSerializer.Serialize(request));using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        var student=Students(store,user).FirstOrDefault(s=>s.Id==request.StudentId);if(student is null)return Error("본인 또는 연결된 재학 자녀의 상담만 신청할 수 있습니다.",403);
        using(var retry=PortalStore.Command(db,"SELECT Id,RequestHash FROM CounselingBookings WHERE RequesterId=$user AND ClientId=$client",tx,("$user",user.Id),("$client",request.ClientId)))using(var reader=retry.ExecuteReader())if(reader.Read())return reader.GetString(1)==fingerprint?Results.Ok(new{id=reader.GetString(0)}):Error("같은 요청 번호의 내용이 다릅니다.",409);
        string teacherId,room;long starts;using(var query=PortalStore.Command(db,"SELECT TeacherId,ClassId,StartsAt,OpensAt,Closed FROM CounselingSlots WHERE Id=$id",tx,("$id",request.SlotId)))using(var reader=query.ExecuteReader())
        {if(!reader.Read())return Results.NotFound();teacherId=reader.GetString(0);room=reader.GetString(1);starts=reader.GetInt64(2);if(reader.GetBoolean(4)||reader.GetInt64(3)>Now||starts<=Now)return Error("아직 열리지 않았거나 마감된 상담 시간입니다.",409);}
        if(room!=student.ClassId||Teacher(store,teacherId) is not { } teacher||!store.Classes(teacher.Id).Contains(room))return Error("담당 학급의 상담 시간만 신청할 수 있습니다.",403);
        using(var expired=PortalStore.Command(db,"UPDATE CounselingBookings SET Active=0 WHERE Active=1 AND SlotId IN(SELECT Id FROM CounselingSlots WHERE StartsAt<=$now)",tx,("$now",Now)))expired.ExecuteNonQuery();
        if(request.ReplaceBookingId is not null)
        {
            using var replace=PortalStore.Command(db,"""
                UPDATE CounselingBookings SET Active=0,CanceledAt=$now WHERE Id=$id AND StudentId=$student AND Active=1
                  AND SlotId IN(SELECT Id FROM CounselingSlots WHERE StartsAt>$now)
                """,tx,("$now",Now),("$id",request.ReplaceBookingId),("$student",student.Id));
            if(replace.ExecuteNonQuery()!=1)return Error("변경할 예약이 없거나 이미 변경되었습니다.",409);
        }
        var id=Guid.NewGuid().ToString("N");using(var add=PortalStore.Command(db,"INSERT INTO CounselingBookings(Id,SlotId,StudentId,RequesterId,ClientId,RequestHash,CreatedAt) VALUES($id,$slot,$student,$user,$client,$hash,$now)",tx,("$id",id),("$slot",request.SlotId),("$student",student.Id),("$user",user.Id),("$client",request.ClientId),("$hash",fingerprint),("$now",Now)))add.ExecuteNonQuery();
        tx.Commit();return Results.Ok(new{id});
    }
    static IResult Cancel(PortalStore store,Account user,string id)
    {
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        string student,teacher,room;long starts;long? canceled;
        using(var query=PortalStore.Command(db,"SELECT b.StudentId,s.TeacherId,s.ClassId,s.StartsAt,b.CanceledAt FROM CounselingBookings b JOIN CounselingSlots s ON s.Id=b.SlotId WHERE b.Id=$id",tx,("$id",id)))using(var reader=query.ExecuteReader())
        {if(!reader.Read())return Results.NotFound();student=reader.GetString(0);teacher=reader.GetString(1);room=reader.GetString(2);starts=reader.GetInt64(3);canceled=reader.IsDBNull(4)?null:reader.GetInt64(4);}
        if(!(Students(store,user).Any(s=>s.Id==student)||user.Role=="teacher"&&user.Id==teacher&&TeacherClasses(store,user).Any(c=>c.Id==room)))return Results.NotFound();
        if(canceled is not null)return Results.Ok();if(starts<=Now)return Error("이미 시작된 상담은 취소할 수 없습니다.",409);
        using(var cancel=PortalStore.Command(db,"UPDATE CounselingBookings SET Active=0,CanceledAt=$now WHERE Id=$id",tx,("$now",Now),("$id",id)))cancel.ExecuteNonQuery();tx.Commit();return Results.Ok();
    }
}

using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalTimetables
{
    static IResult Safe(Func<IResult> action){try{return action();}catch(FilePolicyException e){return Error(e.Message,e.Status);}}
    static PortalClass[] Classes(PortalStore store,string user)
    {
        var person=store.Person(user)!;var rooms=store.DirectorySnapshot().Classes;
        if(person.Role=="teacher"){if(!VerifiedTeacher(store,person))throw new FilePolicyException("교직원 연결 확인이 만료되었습니다.",403);return rooms;}
        if(person.Role=="admin")return rooms;
        if(person.Role=="student")return rooms.Where(c=>c.Id==person.ClassId).ToArray();
        var ids=store.Query("SELECT DISTINCT s.ClassId FROM Families f JOIN People s ON s.Id=f.StudentId WHERE f.ParentId=$id AND s.Active=1",r=>r.GetString(0),("$id",user));return rooms.Where(c=>ids.Contains(c.Id)).ToArray();
    }
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder bridge,PortalStore store)
    {
        var data=new TimetableData(store.Open);data.Initialize();
        api.MapGet("/timetable/setup",(HttpContext c)=>Safe(()=>Results.Ok(new{classes=Classes(store,Id(c)),revision=data.Revision,canManage=false})));
        api.MapGet("/timetable/day",(string? date,string? classId,bool? mine,HttpContext c)=>Safe(()=>
        {
            var user=Id(c);var rooms=Classes(store,user);if(classId is not null&&!rooms.Any(r=>r.Id==classId))return Results.NotFound();
            var day=data.Day(date??TimetableData.Today,classId is null?rooms.Select(c=>c.Id).ToArray():[classId]);
            if(mine==true){var teacher=store.Person(user)!;if(teacher.Role!="teacher")return Results.NotFound();day=day with{Slots=day.Slots.Where(s=>s.TeacherId==teacher.InternalUserId).ToArray()};}
            return Results.Ok(day);
        }));
        api.MapGet("/timetable/notices",(HttpContext c)=>Safe(()=>
        {
            var classes=Classes(store,Id(c)).Select(r=>r.Id).ToArray();return Results.Ok(data.Notices(Id(c)).Where(n=>n.ClassIds.Any(classes.Contains)).Select(n=>n with{ClassIds=n.ClassIds.Where(classes.Contains).ToArray()}));
        }));
        api.MapPost("/timetable/notices/{revision:long}/read",(long revision,HttpContext c)=>Safe(()=>
        {
            var classes=Classes(store,Id(c)).Select(r=>r.Id).ToArray();if(!data.Notices(Id(c)).Any(n=>n.Revision==revision&&n.ClassIds.Any(classes.Contains)))return Results.NotFound();
            data.Execute("UPDATE TimetableNotices SET ReadAt=COALESCE(ReadAt,$now) WHERE Revision=$rev AND UserId=$id",("$now",Now),("$rev",revision),("$id",Id(c)));return Results.Ok();
        }));
        bridge.MapPost("/timetable",(TimetableDelivery delivery)=>Safe(()=>
        {
            if(delivery?.Batch is null||delivery.Homerooms is null||delivery.Notices is null||delivery.Notices.Length>10000||delivery.Homerooms.Length>100)throw new FilePolicyException("시간표 전달 형식을 확인하세요.");
            TimetableData.Validate(delivery.Batch);if(delivery.Batch.Revision<1)throw new FilePolicyException("시간표 버전이 필요합니다.");
            var directory=store.DirectorySnapshot();var classes=delivery.Batch.Cells.Select(s=>s.ClassId).Distinct().ToArray();
            if(classes.Any(id=>!directory.Classes.Any(c=>c.Id==id)))throw new FilePolicyException("시간표 학급 명부가 변경되었습니다.",409);
            if(delivery.Homerooms.Any(h=>h is null||!Guid.TryParseExact(h.ClassId,"N",out _)||!Guid.TryParseExact(h.TeacherId,"N",out _))||delivery.Homerooms.Select(h=>h.ClassId).Distinct().Count()!=delivery.Homerooms.Length)throw new FilePolicyException("담임 연결 형식을 확인하세요.");
            if(delivery.Notices.Any(n=>n is null||n.Revision!=delivery.Batch.Revision||n.Date!=delivery.Batch.Start||n.ClassIds is null||n.ClassIds.Length>100||n.ClassIds.Any(id=>!classes.Contains(id))||n.Title is null||n.Title.Length>100||!directory.People.Any(p=>p.Id==n.UserId))||delivery.Notices.Select(n=>n.UserId).Distinct().Count()!=delivery.Notices.Length)throw new FilePolicyException("알림 수신 대상을 확인하세요.");
            // Re-check each family's current relationship; a moved child cannot receive a queued old-class alert.
            var allowed=delivery.Notices.Where(n=>
            {
                var p=directory.People.First(p=>p.Id==n.UserId);
                return p.Role=="teacher"||p.Role=="student"&&n.ClassIds.Contains(p.ClassId??"")||p.Role=="parent"&&directory.People.Any(s=>s.Role=="student"&&p.Children.Contains(s.Id)&&n.ClassIds.Contains(s.ClassId??""));
            }).ToArray();
            data.Replicate(delivery with{Notices=allowed});return Results.Ok(new{revision=delivery.Batch.Revision});
        }));
    }
}

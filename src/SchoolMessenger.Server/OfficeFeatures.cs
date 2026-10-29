using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

public static class OfficeFeatures
{
    record Event(string Id,string OwnerId,string OwnerName,string Title,string Description,long Start,long End,bool AllDay,int Revision,bool Cancelled);
    record Room(string Id,string Name,string Location,bool Active,int Revision);
    record Booking(string Id,string OwnerId,string OwnerName,string ClientId,string RoomId,string RoomName,string Title,long Start,long End,int Revision,bool Cancelled,int Grade);
    static string User(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static IResult Error(string text,int status=400)=>Results.Json(new{error=text},statusCode:status);
    public static void Initialize(Store store)
    {
        store.Execute("""
        CREATE TABLE IF NOT EXISTS OfficePermissions(UserId TEXT PRIMARY KEY REFERENCES Users(Id),CalendarManager INTEGER NOT NULL DEFAULT 0,RoomManager INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS OfficeEvents(Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES Users(Id),ClientId TEXT NOT NULL,
          Title TEXT NOT NULL,Description TEXT NOT NULL,Start INTEGER NOT NULL,End INTEGER NOT NULL,AllDay INTEGER NOT NULL,
          Revision INTEGER NOT NULL DEFAULT 1,Cancelled INTEGER NOT NULL DEFAULT 0,UNIQUE(OwnerId,ClientId));
        CREATE INDEX IF NOT EXISTS IX_OfficeEvents_Time ON OfficeEvents(Start,End);
        CREATE TABLE IF NOT EXISTS OfficeRooms(Id TEXT PRIMARY KEY,Name TEXT NOT NULL UNIQUE,Location TEXT NOT NULL,Active INTEGER NOT NULL,Revision INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS OfficeBookings(Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL REFERENCES Users(Id),ClientId TEXT NOT NULL,
          RoomId TEXT NOT NULL REFERENCES OfficeRooms(Id),Title TEXT NOT NULL,Start INTEGER NOT NULL,End INTEGER NOT NULL,
          Revision INTEGER NOT NULL DEFAULT 1,Cancelled INTEGER NOT NULL DEFAULT 0,UNIQUE(OwnerId,ClientId));
        CREATE INDEX IF NOT EXISTS IX_OfficeBookings_Overlap ON OfficeBookings(RoomId,Cancelled,Start,End);
        """);
        if(!store.Query("PRAGMA table_info(OfficeBookings)",r=>r.GetString(1)).Contains("Grade"))store.Execute("ALTER TABLE OfficeBookings ADD COLUMN Grade INTEGER NOT NULL DEFAULT 1");
    }
    static bool Granted(SqliteConnection db,SqliteTransaction? tx,string id,string permission)
    {
        // Column names are constants chosen by this module, never request input.
        using var c=Store.Command(db,$"SELECT 1 FROM Users u LEFT JOIN OfficePermissions p ON p.UserId=u.Id WHERE u.Id=$id AND u.Active=1 AND (u.IsAdmin=1 OR p.{permission}=1)",tx,("$id",id));return c.ExecuteScalar() is not null;
    }
    static bool Current(SqliteConnection db,SqliteTransaction tx,HttpContext c)
    {
        using var command=Store.Command(db,"SELECT 1 FROM Users u LEFT JOIN StaffProfiles p ON p.UserId=u.Id WHERE u.Id=$id AND u.Active=1 AND u.SessionVersion=$version AND COALESCE(p.NeedsActivation,0)=0",tx,("$id",User(c)),("$version",c.User.FindFirstValue("version")));return command.ExecuteScalar() is not null;
    }
    static bool Time(string? value,out long result)
    {
        result=0;
        if(value is null||value.Length>40||!Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")||!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var time)||time.Year is <2000 or >2100)return false;
        result=time.ToUnixTimeMilliseconds();return true;
    }
    static bool Range(string? from,string? to,out long start,out long end)
    {
        start=end=0;
        if(!DateOnly.TryParseExact(from,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var a)||!DateOnly.TryParseExact(to,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var b)||b<=a||b.DayNumber-a.DayNumber>366)return false;
        start=new DateTimeOffset(a.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();end=new DateTimeOffset(b.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();return true;
    }
    static Event ReadEvent(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetInt64(6),r.GetBoolean(7),r.GetInt32(8),r.GetBoolean(9));
    static Booking ReadBooking(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt64(7),r.GetInt64(8),r.GetInt32(9),r.GetBoolean(10),r.GetInt32(11));
    const string Events="SELECT e.Id,e.OwnerId,u.Name,e.Title,e.Description,e.Start,e.End,e.AllDay,e.Revision,e.Cancelled FROM OfficeEvents e JOIN Users u ON u.Id=e.OwnerId";
    const string Bookings="SELECT b.Id,b.OwnerId,u.Name,b.ClientId,b.RoomId,r.Name,b.Title,b.Start,b.End,b.Revision,b.Cancelled,b.Grade FROM OfficeBookings b JOIN Users u ON u.Id=b.OwnerId JOIN OfficeRooms r ON r.Id=b.RoomId";
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder admin,Store store)
    {
        api.MapGet("/office/setup",(HttpContext c)=>
        {
            using var db=store.Open();return Results.Ok(new{calendarManager=Granted(db,null,User(c),"CalendarManager"),roomManager=Granted(db,null,User(c),"RoomManager"),userId=User(c),rooms=store.Query("SELECT Id,Name,Location,Active,Revision FROM OfficeRooms ORDER BY Name",r=>new Room(r.GetString(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),r.GetInt32(4)))});
        });
        admin.MapGet("/office/permissions",()=>store.Query("SELECT u.Id,u.Name,u.Department,COALESCE(p.CalendarManager,0),COALESCE(p.RoomManager,0) FROM Users u LEFT JOIN OfficePermissions p ON p.UserId=u.Id WHERE u.Active=1 ORDER BY u.Department,u.Name",r=>new{userId=r.GetString(0),name=r.GetString(1),department=r.GetString(2),calendarManager=r.GetBoolean(3),roomManager=r.GetBoolean(4)}));
        admin.MapPost("/office/permissions",IResult(OfficePermissionRequest request,HttpContext c)=>
        {
            using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
            using(var find=Store.Command(db,"SELECT 1 FROM Users WHERE Id=$id AND Active=1",tx,("$id",request.UserId)))if(find.ExecuteScalar() is null)return Results.NotFound();
            using(var command=Store.Command(db,"INSERT INTO OfficePermissions VALUES($id,$calendar,$rooms) ON CONFLICT(UserId) DO UPDATE SET CalendarManager=$calendar,RoomManager=$rooms",tx,("$id",request.UserId),("$calendar",request.CalendarManager),("$rooms",request.RoomManager)))command.ExecuteNonQuery();
            StaffAccounts.Audit(db,tx,User(c),"office.permissions",request.UserId);tx.Commit();return Results.Ok();
        });
        api.MapGet("/office/events",IResult(string? from,string? to)=>!Range(from,to,out var start,out var end)?Error("조회 범위는 yyyy-MM-dd 형식으로 최대 366일입니다."):Results.Ok(store.Query(Events+" WHERE e.Cancelled=0 AND e.Start<$end AND e.End>$start ORDER BY e.Start LIMIT 1000",ReadEvent,("$start",start),("$end",end))));
        api.MapPost("/office/events",(OfficeEventRequest request,HttpContext c)=>SaveEvent(store,c,request,null));
        api.MapPost("/office/events/{id}",(string id,OfficeEventRequest request,HttpContext c)=>SaveEvent(store,c,request,id));
        api.MapPost("/office/events/{id}/cancel",(string id,OfficeRevision request,HttpContext c)=>Cancel(store,c,id,request.Revision,true));
        api.MapPost("/office/rooms",(OfficeRoomRequest request,HttpContext c)=>SaveRoom(store,c,request,null));
        api.MapPost("/office/rooms/{id}",(string id,OfficeRoomRequest request,HttpContext c)=>SaveRoom(store,c,request,id));
        api.MapGet("/office/bookings",IResult(string? from,string? to,string? roomId)=>!Range(from,to,out var start,out var end)?Error("예약 조회 날짜 범위를 확인하세요."):Results.Ok(store.Query(Bookings+" WHERE b.Cancelled=0 AND b.Start<$end AND b.End>$start AND ($room IS NULL OR b.RoomId=$room) ORDER BY b.Start LIMIT 1000",ReadBooking,("$start",start),("$end",end),("$room",roomId))));
        api.MapPost("/office/bookings",(OfficeBookingRequest request,HttpContext c)=>SaveBooking(store,c,request,null));
        api.MapPost("/office/bookings/{id}",(string id,OfficeBookingRequest request,HttpContext c)=>SaveBooking(store,c,request,id));
        api.MapPost("/office/bookings/{id}/cancel",(string id,OfficeRevision request,HttpContext c)=>Cancel(store,c,id,request.Revision,false));
        admin.MapGet("/office/audit",()=>store.Query("SELECT a.Id,u.Name,a.Action,a.ResourceId,a.CreatedAt FROM OfficeAudit a JOIN Users u ON u.Id=a.ActorId ORDER BY a.Id DESC LIMIT 200",r=>new{id=r.GetInt64(0),actor=r.GetString(1),action=r.GetString(2),resourceId=r.GetString(3),createdAt=r.GetInt64(4)}));
    }
    static IResult SaveEvent(Store store,HttpContext c,OfficeEventRequest request,string? id)
    {
        if(!Guid.TryParseExact(request.ClientId,"N",out _)||string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200||request.Description is null||request.Description.Length>10000||!Time(request.Start,out var start)||!Time(request.End,out var end)||end<=start||end-start>366*86_400_000L)return Error("일정 제목·설명과 시작/종료 시간을 확인하세요.");
        if(request.AllDay&&(start%86_400_000!=54_000_000||end%86_400_000!=54_000_000))return Error("종일 일정은 한국 시간 자정부터 다음 날 자정까지 지정하세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        if(!Current(db,tx,c))return Error("계정 상태가 변경되었습니다. 다시 로그인하세요.",401);
        if(!Granted(db,tx,User(c),"CalendarManager"))return Error("학사일정 담당 권한이 필요합니다.",403);
        if(id is null)
        {
            using var previous=Store.Command(db,Events+" WHERE e.OwnerId=$user AND e.ClientId=$client",tx,("$user",User(c)),("$client",request.ClientId));
            using(var reader=previous.ExecuteReader())if(reader.Read()){var value=ReadEvent(reader);return value.Title==request.Title.Trim()&&value.Description==request.Description&&value.Start==start&&value.End==end&&value.AllDay==request.AllDay&&!value.Cancelled?Results.Ok(new{id=value.Id,revision=value.Revision,duplicate=true}):Error("같은 요청 번호의 내용을 바꿀 수 없습니다.",409);}
            id=Guid.NewGuid().ToString("N");using var add=Store.Command(db,"INSERT INTO OfficeEvents(Id,OwnerId,ClientId,Title,Description,Start,End,AllDay) VALUES($id,$user,$client,$title,$description,$start,$end,$all)",tx,("$id",id),("$user",User(c)),("$client",request.ClientId),("$title",request.Title.Trim()),("$description",request.Description),("$start",start),("$end",end),("$all",request.AllDay));add.ExecuteNonQuery();
        }
        else
        {
            using var update=Store.Command(db,"UPDATE OfficeEvents SET Title=$title,Description=$description,Start=$start,End=$end,AllDay=$all,Revision=Revision+1 WHERE Id=$id AND Revision=$version AND Cancelled=0",tx,("$title",request.Title.Trim()),("$description",request.Description),("$start",start),("$end",end),("$all",request.AllDay),("$id",id),("$version",request.Revision));if(update.ExecuteNonQuery()!=1)return Error("일정이 변경 또는 취소되었습니다. 다시 조회하세요.",409);
        }
        StaffAccounts.Audit(db,tx,User(c),"event.save",id);tx.Commit();return Results.Ok(new{id,revision=request.Revision+1});
    }
    static IResult SaveRoom(Store store,HttpContext c,OfficeRoomRequest request,string? id)
    {
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>80||request.Location is null||request.Location.Length>100)return Error("특별실 이름(80자)과 위치(100자)를 확인하세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);if(!Current(db,tx,c))return Error("계정 상태가 변경되었습니다. 다시 로그인하세요.",401);if(!Granted(db,tx,User(c),"RoomManager"))return Error("특별실 관리 권한이 필요합니다.",403);
        try
        {
            if(id is null){id=Guid.NewGuid().ToString("N");using var add=Store.Command(db,"INSERT INTO OfficeRooms(Id,Name,Location,Active) VALUES($id,$name,$location,$active)",tx,("$id",id),("$name",request.Name.Trim()),("$location",request.Location.Trim()),("$active",request.Active));add.ExecuteNonQuery();}
            else{using var update=Store.Command(db,"UPDATE OfficeRooms SET Name=$name,Location=$location,Active=$active,Revision=Revision+1 WHERE Id=$id AND Revision=$version",tx,("$name",request.Name.Trim()),("$location",request.Location.Trim()),("$active",request.Active),("$id",id),("$version",request.Revision));if(update.ExecuteNonQuery()!=1)return Error("특별실 정보가 변경되었습니다. 다시 조회하세요.",409);}
            StaffAccounts.Audit(db,tx,User(c),"room.save",id);tx.Commit();return Results.Ok(new{id,revision=request.Revision+1});
        }
        catch(SqliteException e)when(e.SqliteErrorCode==19){return Error("동일한 이름의 특별실이 있습니다.",409);}
    }
    static IResult SaveBooking(Store store,HttpContext c,OfficeBookingRequest request,string? id)
    {
        if(!Guid.TryParseExact(request.ClientId,"N",out _)||!Guid.TryParseExact(request.RoomId,"N",out _)||string.IsNullOrWhiteSpace(request.Title)||request.Title.Length>200||!Time(request.Start,out var start)||!Time(request.End,out var end)||start<Maintenance.Now-300_000||!SchoolDay.RoomRange(request.Grade,start,end))return Error("특별실은 선택한 학년의 같은 날짜 1~7교시만 예약할 수 있습니다. 시작·종료를 교시 시각에 맞추세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        if(!Current(db,tx,c))return Error("계정 상태가 변경되었습니다. 다시 로그인하세요.",401);
        if(id is null)
        {
            using var previous=Store.Command(db,Bookings+" WHERE b.OwnerId=$user AND b.ClientId=$client",tx,("$user",User(c)),("$client",request.ClientId));
            using(var reader=previous.ExecuteReader())if(reader.Read()){var value=ReadBooking(reader);return value.RoomId==request.RoomId&&value.Title==request.Title.Trim()&&value.Start==start&&value.End==end&&value.Grade==request.Grade&&!value.Cancelled?Results.Ok(new{id=value.Id,revision=value.Revision,duplicate=true}):Error("같은 예약 요청 번호를 재사용할 수 없습니다.",409);}
        }
        else
        {
            using var owner=Store.Command(db,"SELECT OwnerId FROM OfficeBookings WHERE Id=$id",tx,("$id",id));var ownerId=owner.ExecuteScalar() as string;if(ownerId is null)return Results.NotFound();
            if(ownerId!=User(c)&&!Granted(db,tx,User(c),"RoomManager"))return Error("본인의 예약만 수정할 수 있습니다.",403);
        }
        using(var room=Store.Command(db,"SELECT 1 FROM OfficeRooms WHERE Id=$id AND Active=1",tx,("$id",request.RoomId)))if(room.ExecuteScalar() is null)return Error("예약할 수 없는 특별실입니다.",409);
        using(var overlap=Store.Command(db,"SELECT 1 FROM OfficeBookings WHERE RoomId=$room AND Cancelled=0 AND Start<$end AND End>$start AND ($id IS NULL OR Id!=$id) LIMIT 1",tx,("$room",request.RoomId),("$start",start),("$end",end),("$id",id)))if(overlap.ExecuteScalar() is not null)return Error("선택한 시간에 다른 예약이 있습니다.",409);
        if(id is null){id=Guid.NewGuid().ToString("N");using var add=Store.Command(db,"INSERT INTO OfficeBookings(Id,OwnerId,ClientId,RoomId,Title,Start,End,Grade) VALUES($id,$user,$client,$room,$title,$start,$end,$grade)",tx,("$id",id),("$user",User(c)),("$client",request.ClientId),("$room",request.RoomId),("$title",request.Title.Trim()),("$start",start),("$end",end),("$grade",request.Grade));add.ExecuteNonQuery();}
        else{using var update=Store.Command(db,"UPDATE OfficeBookings SET RoomId=$room,Title=$title,Start=$start,End=$end,Grade=$grade,Revision=Revision+1 WHERE Id=$id AND Revision=$version AND Cancelled=0",tx,("$room",request.RoomId),("$title",request.Title.Trim()),("$start",start),("$end",end),("$grade",request.Grade),("$id",id),("$version",request.Revision));if(update.ExecuteNonQuery()!=1)return Error("예약이 변경 또는 취소되었습니다. 다시 조회하세요.",409);}
        StaffAccounts.Audit(db,tx,User(c),"booking.save",id);tx.Commit();return Results.Ok(new{id,revision=request.Revision+1});
    }
    static IResult Cancel(Store store,HttpContext c,string id,int revision,bool isEvent)
    {
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);if(!Current(db,tx,c))return Error("계정 상태가 변경되었습니다. 다시 로그인하세요.",401);var table=isEvent?"OfficeEvents":"OfficeBookings";
        using var owner=Store.Command(db,$"SELECT OwnerId FROM {table} WHERE Id=$id",tx,("$id",id));var ownerId=owner.ExecuteScalar() as string;if(ownerId is null)return Results.NotFound();
        if(isEvent?!Granted(db,tx,User(c),"CalendarManager"):ownerId!=User(c)&&!Granted(db,tx,User(c),"RoomManager"))return Error("취소 권한이 없습니다.",403);
        using var cancel=Store.Command(db,$"UPDATE {table} SET Cancelled=1,Revision=Revision+1 WHERE Id=$id AND Revision=$version AND Cancelled=0",tx,("$id",id),("$version",revision));if(cancel.ExecuteNonQuery()!=1)return Error("이미 변경 또는 취소되었습니다. 다시 조회하세요.",409);
        StaffAccounts.Audit(db,tx,User(c),isEvent?"event.cancel":"booking.cancel",id);tx.Commit();return Results.Ok();
    }
}

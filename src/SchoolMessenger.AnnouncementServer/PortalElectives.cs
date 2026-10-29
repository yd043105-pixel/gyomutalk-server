using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

// Applications live only on the school-news service. No course has an enrollment capacity.
public static class PortalElectives
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    sealed record Stored(string Id,ElectiveRoundRequest Request,string State,int Revision);
    sealed class Invalid(string message,int status=400):Exception(message){public int Status=>status;}
    static void Require(bool valid,string message,int status=400){if(!valid)throw new Invalid(message,status);}
    static bool GuidId(string? value)=>Guid.TryParseExact(value,"N",out _);
    static bool Label(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&!value.Any(char.IsControl);
    static SqliteCommand Cmd(SqliteConnection db,string sql,SqliteTransaction? tx=null,params (string,object?)[] values)=>PortalStore.Command(db,sql,tx,values);
    static IResult Safe(Func<IResult> action){try{return action();}catch(Invalid e){return Error(e.Message,e.Status);}}
    public static void Initialize(PortalStore store)=>store.Execute("""
        CREATE TABLE IF NOT EXISTS ElectiveRounds(Id TEXT PRIMARY KEY,ClientId TEXT NOT NULL UNIQUE,Creator TEXT NOT NULL,Fingerprint TEXT NOT NULL,Payload TEXT NOT NULL,State TEXT NOT NULL DEFAULT 'draft',Revision INTEGER NOT NULL DEFAULT 1,CreatedAt INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS ElectiveApplications(RoundId TEXT NOT NULL REFERENCES ElectiveRounds(Id),StudentId TEXT NOT NULL REFERENCES People(Id),Revision INTEGER NOT NULL,Submitted INTEGER NOT NULL,UpdatedAt INTEGER NOT NULL,PRIMARY KEY(RoundId,StudentId));
        CREATE TABLE IF NOT EXISTS ElectiveChoices(RoundId TEXT NOT NULL,StudentId TEXT NOT NULL,CourseId TEXT NOT NULL,PRIMARY KEY(RoundId,StudentId,CourseId),FOREIGN KEY(RoundId,StudentId) REFERENCES ElectiveApplications(RoundId,StudentId) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS ElectiveReceipts(RoundId TEXT NOT NULL REFERENCES ElectiveRounds(Id),ActorId TEXT NOT NULL,ClientId TEXT NOT NULL,Fingerprint TEXT NOT NULL,Response TEXT NOT NULL,PRIMARY KEY(RoundId,ActorId,ClientId));
        CREATE TABLE IF NOT EXISTS ElectiveCourseSections(RoundId TEXT NOT NULL REFERENCES ElectiveRounds(Id),CourseId TEXT NOT NULL,SectionId TEXT NOT NULL REFERENCES Sections(Id),PRIMARY KEY(RoundId,CourseId),UNIQUE(SectionId));
        UPDATE ElectiveRounds SET Payload=json_set(json_remove(Payload,'$.classIds'),'$.grades',json((
            SELECT json_group_array(Grade) FROM (SELECT DISTINCT c.Grade FROM Classes c
            JOIN json_each(ElectiveRounds.Payload,'$.classIds') scope ON scope.value=c.Id WHERE c.Grade IN (1,2) ORDER BY c.Grade)
        ))) WHERE json_type(Payload,'$.grades') IS NULL OR json_type(Payload,'$.grades')='null';
        """);
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder bridge,PortalStore store)
    {
        api.MapGet("/electives",(HttpContext c)=>Safe(()=>
        {
            var person=Student(store,Id(c));using var db=store.Open();
            return Results.Ok(Rounds(db,Grade(db,person.ClassId!)).Select(r=>View(db,r,person.Id)).ToArray());
        }));
        api.MapGet("/electives/{id}",(string id,HttpContext c)=>Safe(()=>
        {var person=Student(store,Id(c));using var db=store.Open();var round=Read(db,id);Eligible(db,round,person);return Results.Ok(View(db,round,person.Id));}));
        api.MapPost("/electives/{id}/application",(string id,ElectiveApplicationRequest request,HttpContext c)=>Safe(()=>Apply(store,id,Id(c),request,false)));
        api.MapPost("/electives/{id}/cancel",(string id,ElectiveCancellation request,HttpContext c)=>Safe(()=>Apply(store,id,Id(c),new(request.ClientId,request.Revision,[]),true)));
        bridge.MapGet("/electives/setup",()=>Results.Ok(new
        {
            grades=new[]{1,2},
            teachers=store.Query("SELECT Id,Name FROM People WHERE Active=1 AND Role='teacher' AND InternalUserId IS NOT NULL ORDER BY Name",r=>new{id=r.GetString(0),name=r.GetString(1)})
        }));
        bridge.MapGet("/electives",()=>{using var db=store.Open();return Results.Ok(Rounds(db).Select(r=>View(db,r)).ToArray());});
        bridge.MapGet("/electives/{id}",(string id)=>Safe(()=>{using var db=store.Open();return Results.Ok(Report(db,Read(db,id)));}));
        bridge.MapPost("/electives",(ElectiveRoundRequest request,string actor)=>Safe(()=>Save(store,null,actor,request)));
        bridge.MapPost("/electives/{id}",(string id,ElectiveRoundRequest request,string actor)=>Safe(()=>Save(store,id,actor,request)));
        bridge.MapPost("/electives/{id}/state",(string id,ElectiveTransition request,string actor)=>Safe(()=>Transition(store,id,actor,request)));
    }
    static Account Student(PortalStore store,string id)
    {var person=store.Person(id);Require(person is{Active:true,Role:"student",ClassId:not null},"학생 본인의 계정으로 신청하세요.",403);return person!;}
    static Stored Read(SqliteConnection db,string id,SqliteTransaction? tx=null)
    {
        Require(GuidId(id),"신청 회차를 찾을 수 없습니다.",404);using var c=Cmd(db,"SELECT Payload,State,Revision FROM ElectiveRounds WHERE Id=$id",tx,("$id",id));using var r=c.ExecuteReader();Require(r.Read(),"신청 회차를 찾을 수 없습니다.",404);
        return new(id,JsonSerializer.Deserialize<ElectiveRoundRequest>(r.GetString(0),Json)!,r.GetString(1),r.GetInt32(2));
    }
    static Stored[] Rounds(SqliteConnection db,int? grade=null)
    {
        using var c=Cmd(db,"SELECT Id,Payload,State,Revision FROM ElectiveRounds WHERE $grade IS NULL OR (State!='draft' AND EXISTS(SELECT 1 FROM json_each(Payload,'$.grades') WHERE value=$grade)) ORDER BY CASE WHEN $grade IS NOT NULL AND State='open' THEN 0 ELSE 1 END,CreatedAt DESC,Id LIMIT 100",null,("$grade",grade));using var r=c.ExecuteReader();var rows=new List<Stored>();
        while(r.Read())rows.Add(new(r.GetString(0),JsonSerializer.Deserialize<ElectiveRoundRequest>(r.GetString(1),Json)!,r.GetString(2),r.GetInt32(3)));return rows.ToArray();
    }
    static int Grade(SqliteConnection db,string classId,SqliteTransaction? tx=null)
    {
        using var c=Cmd(db,"SELECT Grade FROM Classes WHERE Id=$id",tx,("$id",classId));var grade=c.ExecuteScalar();
        Require(grade is not null,"재학생의 학년 정보를 확인하세요.",403);return Convert.ToInt32(grade);
    }
    static void Eligible(SqliteConnection db,Stored round,Account person,SqliteTransaction? tx=null)=>Require(round.State!="draft"&&round.Request.Grades.Contains(Grade(db,person.ClassId!,tx)),"신청 대상 학년이 아닙니다.",404);
    static ElectiveApplication Application(SqliteConnection db,string round,string student,SqliteTransaction? tx=null)
    {
        int revision=0;bool submitted=false;long? at=null;
        using(var c=Cmd(db,"SELECT Revision,Submitted,UpdatedAt FROM ElectiveApplications WHERE RoundId=$round AND StudentId=$student",tx,("$round",round),("$student",student)))using(var r=c.ExecuteReader())if(r.Read()){revision=r.GetInt32(0);submitted=r.GetBoolean(1);at=r.GetInt64(2);}
        var choices=new List<string>();using(var c=Cmd(db,"SELECT CourseId FROM ElectiveChoices WHERE RoundId=$round AND StudentId=$student ORDER BY CourseId",tx,("$round",round),("$student",student)))using(var r=c.ExecuteReader())while(r.Read())choices.Add(r.GetString(0));
        return new(revision,submitted,choices.ToArray(),at);
    }
    static ElectiveRound View(SqliteConnection db,Stored stored,string? student=null,SqliteTransaction? tx=null)
    {
        var r=stored.Request;return new(stored.Id,r.Title,r.Term,r.Grades,r.StartsAt,r.EndsAt,stored.State,stored.Revision,r.Groups,stored.State=="open"&&r.StartsAt<=Now&&Now<r.EndsAt,student is null?null:Application(db,stored.Id,student,tx),r.MinCredits,r.MaxCredits);
    }
    static ElectiveReport Report(SqliteConnection db,Stored stored)
    {
        using var tx=db.BeginTransaction(deferred:true);stored=Read(db,stored.Id,tx);
        var students=new List<ElectiveStudent>();
        using(var c=Cmd(db,"SELECT p.Id,p.Name,p.ClassId,c.Name,c.Grade FROM People p JOIN Classes c ON c.Id=p.ClassId WHERE p.Active=1 AND p.Role='student' ORDER BY c.Grade,c.Name,p.Name,p.Id",tx))
        using(var r=c.ExecuteReader())while(r.Read())if(stored.Request.Grades.Contains(r.GetInt32(4)))students.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),false,0,[],null));
        for(var i=0;i<students.Count;i++){var s=students[i];var a=Application(db,stored.Id,s.Id,tx);students[i]=s with{Submitted=a.Submitted,Revision=a.Revision,CourseIds=a.CourseIds,UpdatedAt=a.UpdatedAt};}
        var sections=new List<ElectiveSection>();using(var c=Cmd(db,"SELECT CourseId,SectionId FROM ElectiveCourseSections WHERE RoundId=$id",tx,("$id",stored.Id)))using(var r=c.ExecuteReader())while(r.Read())sections.Add(new(r.GetString(0),r.GetString(1)));
        using var count=Cmd(db,"SELECT StudentId FROM ElectiveApplications WHERE RoundId=$id AND Submitted=1",tx,("$id",stored.Id));using var read=count.ExecuteReader();var eligible=students.Select(s=>s.Id).ToHashSet();var excluded=0;while(read.Read())if(!eligible.Contains(read.GetString(0)))excluded++;
        return new(View(db,stored,tx:tx),students.ToArray(),sections.ToArray(),excluded);
    }
    static void Validate(SqliteConnection db,ElectiveRoundRequest r,SqliteTransaction tx)
    {
        Require(GuidId(r.ClientId)&&r.Revision>=0&&Label(r.Title,100)&&Label(r.Term,40),"회차명·학기·요청 번호를 확인하세요.");
        Require(r.StartsAt>0&&r.EndsAt>r.StartsAt&&r.EndsAt-r.StartsAt<=366L*86_400_000&&r.EndsAt<=Now+5*366L*86_400_000,"신청 시작·종료 시각을 확인하세요.");
        Require(r.Grades is{Length:>=1 and <=2}&&r.Grades.All(g=>g is 1 or 2)&&r.Grades.Distinct().Count()==r.Grades.Length,"대상 학년을 1·2학년 중 선택하세요. 3학년은 수강신청 대상이 아닙니다.");
        Require(r.Groups is{Length:>=1 and <=10}&&r.Groups.All(g=>g is not null&&GuidId(g.Id)&&Label(g.Name,60)&&g.Courses is{Length:>=1 and <=100}&&g.Courses.All(c=>c is not null)&&g.Required>=0&&g.Required<=g.Courses.Count(c=>c.Type!="common")&&(g.Required>0||g.Courses.All(c=>c.Type=="common"))),"선택 묶음·선택과목 개수·공통과목을 확인하세요. 공통과목만 있는 묶음은 선택 개수 0입니다.");
        var courses=r.Groups.SelectMany(g=>g.Courses).ToArray();Require(courses.Length<=100&&r.Groups.Select(g=>g.Id).Distinct().Count()==r.Groups.Length&&courses.All(c=>c is not null&&GuidId(c.Id)&&Label(c.Name,100)&&c.TeacherIds is{Length:>=1 and <=20}&&c.TeacherIds.All(GuidId)&&c.TeacherIds.Distinct().Count()==c.TeacherIds.Length)&&courses.Select(c=>c.Id).Distinct().Count()==courses.Length,"과목 이름·담당 교사·과목 식별자를 확인하세요.");
        Require(courses.All(c=>ElectiveRules.Types.Contains(c.Type)&&c.Credits is >=1 and <=20&&c.Area is not null&&c.Area.Length<=30&&!c.Area.Any(char.IsControl)),"과목 유형과 1~20 범위의 학점을 입력하세요. 교과 이름은 30자 이내입니다.");
        Require(r.MinCredits is >=0 and <=2000&&r.MaxCredits is >=0 and <=2000&&(r.MaxCredits==0||r.MaxCredits>=r.MinCredits),"신청 학점의 최소·최대 조건을 확인하세요. 0은 제한 없음입니다.");
        Require(ElectiveRules.Feasible(r.Groups,r.MinCredits,r.MaxCredits),"현재 과목과 선택 개수로 신청 학점 조건을 충족할 수 없습니다.");
        Teachers(db,r.Groups,tx);
    }
    static void Teachers(SqliteConnection db,ElectiveGroup[] groups,SqliteTransaction tx)
    {
        foreach(var id in groups.SelectMany(g=>g.Courses).SelectMany(c=>c.TeacherIds).Distinct())
        {using var c=Cmd(db,"SELECT 1 FROM People WHERE Id=$id AND Role='teacher' AND Active=1 AND InternalUserId IS NOT NULL",tx,("$id",id));Require(c.ExecuteScalar() is not null,"담당 교사의 계정·교내 계정 연결을 확인하세요.",409);}
    }
    static string Fingerprint(string operation,object body)=>Hash(operation+"\n"+JsonSerializer.Serialize(body,Json));
    static IResult? Receipt(SqliteConnection db,SqliteTransaction tx,string round,string actor,string client,string fingerprint)
    {
        using var c=Cmd(db,"SELECT Fingerprint,Response FROM ElectiveReceipts WHERE RoundId=$round AND ActorId=$actor AND ClientId=$client",tx,("$round",round),("$actor",actor),("$client",client));using var r=c.ExecuteReader();
        if(!r.Read())return null;Require(r.GetString(0)==fingerprint,"같은 요청 번호로 신청 내용을 바꿀 수 없습니다.",409);return Results.Content(r.GetString(1),"application/json");
    }
    static IResult Remember(SqliteConnection db,SqliteTransaction tx,string round,string actor,string client,string fingerprint,object response)
    {
        var json=JsonSerializer.Serialize(response,Json);using var c=Cmd(db,"INSERT INTO ElectiveReceipts VALUES($round,$actor,$client,$hash,$response)",tx,("$round",round),("$actor",actor),("$client",client),("$hash",fingerprint),("$response",json));c.ExecuteNonQuery();tx.Commit();return Results.Content(json,"application/json");
    }
    static IResult Save(PortalStore store,string? id,string actor,ElectiveRoundRequest request)
    {
        Require(GuidId(actor),"담당자 계정을 확인하세요.",403);using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);Validate(db,request,tx);
        var fingerprint=Fingerprint("save",request);var owner="office:"+actor;
        if(id is null)
        {
            using var existing=Cmd(db,"SELECT Id,Creator,Fingerprint FROM ElectiveRounds WHERE ClientId=$client",tx,("$client",request.ClientId));
            using(var r=existing.ExecuteReader())if(r.Read()){Require(r.GetString(1)==actor&&r.GetString(2)==fingerprint,"이미 사용한 생성 요청 번호입니다.",409);return Results.Ok(new{id=r.GetString(0),revision=1});}
            Require(request.Revision==0,"신규 회차의 버전을 확인하세요.");id=Guid.NewGuid().ToString("N");
            using var insert=Cmd(db,"INSERT INTO ElectiveRounds(Id,ClientId,Creator,Fingerprint,Payload,CreatedAt) VALUES($id,$client,$actor,$hash,$payload,$now)",tx,("$id",id),("$client",request.ClientId),("$actor",actor),("$hash",fingerprint),("$payload",JsonSerializer.Serialize(request,Json)),("$now",Now));insert.ExecuteNonQuery();tx.Commit();return Results.Ok(new{id,revision=1});
        }
        var round=Read(db,id,tx);var prior=Receipt(db,tx,id,owner,request.ClientId,fingerprint);if(prior is not null)return prior;
        Require(round.State=="draft"&&round.Revision==request.Revision,"초안만 편집할 수 있습니다. 변경된 회차를 다시 조회하세요.",409);
        using(var update=Cmd(db,"UPDATE ElectiveRounds SET Payload=$payload,Revision=Revision+1 WHERE Id=$id",tx,("$payload",JsonSerializer.Serialize(request,Json)),("$id",id)))update.ExecuteNonQuery();
        return Remember(db,tx,id,owner,request.ClientId,fingerprint,new{id,revision=round.Revision+1});
    }
    static IResult Transition(PortalStore store,string id,string actor,ElectiveTransition request)
    {
        Require(GuidId(actor)&&GuidId(request.ClientId)&&request.Revision>=1&&request.Action is("open"or"close"or"finalize"),"회차 처리 요청을 확인하세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);var round=Read(db,id,tx);var hash=Fingerprint("state",request);var owner="office:"+actor;
        var prior=Receipt(db,tx,id,owner,request.ClientId,hash);if(prior is not null)return prior;Require(round.Revision==request.Revision,"회차가 변경되었습니다. 새로고침하세요.",409);
        string state;
        if(request.Action=="open"){Require(round.State=="draft"&&round.Request.EndsAt>Now,"신청 종료 전의 초안만 공개할 수 있습니다.",409);Validate(db,round.Request,tx);state="open";}
        else if(request.Action=="close"){Require(round.State=="open","공개된 회차만 마감할 수 있습니다.",409);state="closed";}
        else
        {
            Require(round.State=="closed"||round.State=="open"&&round.Request.EndsAt<=Now,"신청을 마감한 뒤 분반을 확정하세요.",409);Teachers(db,round.Request.Groups,tx);
            foreach(var group in round.Request.Groups)foreach(var course in group.Courses)
            {
                var section=Guid.NewGuid().ToString("N");var name=group.Name+" · "+course.Name;if(name.Length>100)name=name[..100];
                using(var add=Cmd(db,"INSERT INTO Sections(Id,Name,Term) VALUES($id,$name,$term);INSERT INTO ElectiveCourseSections VALUES($round,$course,$id)",tx,("$id",section),("$name",name),("$term",round.Request.Term),("$round",id),("$course",course.Id)))add.ExecuteNonQuery();
                foreach(var teacher in course.TeacherIds){using var add=Cmd(db,"INSERT INTO SectionTeachers VALUES($id,$teacher)",tx,("$id",section),("$teacher",teacher));add.ExecuteNonQuery();}
                using var members=Cmd(db,"SELECT p.Id,room.Grade FROM ElectiveChoices c JOIN ElectiveApplications a ON a.RoundId=c.RoundId AND a.StudentId=c.StudentId JOIN People p ON p.Id=c.StudentId JOIN Classes room ON room.Id=p.ClassId WHERE c.RoundId=$round AND c.CourseId=$course AND a.Submitted=1 AND p.Active=1 AND p.Role='student'",tx,("$round",id),("$course",course.Id));
                var ids=new List<string>();using(var r=members.ExecuteReader())while(r.Read())if(round.Request.Grades.Contains(r.GetInt32(1)))ids.Add(r.GetString(0));
                foreach(var student in ids){using var add=Cmd(db,"INSERT INTO SectionMembers VALUES($id,$student,$stamp)",tx,("$id",section),("$student",student),("$stamp",Guid.NewGuid().ToString("N")));add.ExecuteNonQuery();}
            }
            state="finalized";
        }
        using(var update=Cmd(db,"UPDATE ElectiveRounds SET State=$state,Revision=Revision+1 WHERE Id=$id",tx,("$state",state),("$id",id)))update.ExecuteNonQuery();
        return Remember(db,tx,id,owner,request.ClientId,hash,new{id,revision=round.Revision+1,state});
    }
    static IResult Apply(PortalStore store,string id,string student,ElectiveApplicationRequest request,bool cancel)
    {
        var person=Student(store,student);Require(GuidId(request.ClientId)&&request.Revision>=0&&request.CourseIds is{Length:<=100}&&request.CourseIds.All(GuidId)&&request.CourseIds.Distinct().Count()==request.CourseIds.Length,"신청 번호·선택 과목을 확인하세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);var round=Read(db,id,tx);
        // Re-read current membership under the same write transaction as the application.
        using(var current=Cmd(db,"SELECT ClassId FROM People WHERE Id=$id AND Active=1 AND Role='student'",tx,("$id",student))){var room=current.ExecuteScalar() as string;Require(room is not null,"재학생 계정을 확인하세요.",403);person=person with{ClassId=room};}
        Eligible(db,round,person,tx);var hash=Fingerprint(cancel?"cancel":"apply",request);var prior=Receipt(db,tx,id,student,request.ClientId,hash);if(prior is not null)return prior;
        Require(round.State=="open"&&round.Request.StartsAt<=Now&&Now<round.Request.EndsAt,"현재는 신청·변경·취소 기간이 아닙니다.",409);
        var previous=Application(db,id,student,tx);Require(previous.Revision==request.Revision,"다른 기기에서 신청이 바뀌었습니다. 다시 조회하세요.",409);
        if(cancel)Require(previous.Submitted,"취소할 신청이 없습니다.",409);
        else
        {
            var selectionError=ElectiveRules.SelectionError(round.Request.Groups,request.CourseIds,round.Request.MinCredits,round.Request.MaxCredits);
            Require(selectionError is null,selectionError??"");
        }
        var at=Now;using(var update=Cmd(db,"INSERT INTO ElectiveApplications VALUES($round,$student,$revision,$submitted,$now) ON CONFLICT(RoundId,StudentId) DO UPDATE SET Revision=excluded.Revision,Submitted=excluded.Submitted,UpdatedAt=excluded.UpdatedAt;DELETE FROM ElectiveChoices WHERE RoundId=$round AND StudentId=$student",tx,("$round",id),("$student",student),("$revision",previous.Revision+1),("$submitted",!cancel),("$now",at)))update.ExecuteNonQuery();
        if(!cancel)foreach(var course in request.CourseIds){using var add=Cmd(db,"INSERT INTO ElectiveChoices VALUES($round,$student,$course)",tx,("$round",id),("$student",student),("$course",course));add.ExecuteNonQuery();}
        return Remember(db,tx,id,student,request.ClientId,hash,new ElectiveApplication(previous.Revision+1,!cancel,cancel?[]:request.CourseIds.Order().ToArray(),at));
    }
}

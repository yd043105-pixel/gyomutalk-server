using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalSections
{
    public static void Initialize(PortalStore store)=>store.Execute("""
        CREATE TABLE IF NOT EXISTS Sections(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Term TEXT NOT NULL,Revision INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS SectionTeachers(SectionId TEXT NOT NULL REFERENCES Sections(Id),TeacherId TEXT NOT NULL REFERENCES People(Id),PRIMARY KEY(SectionId,TeacherId));
        CREATE TABLE IF NOT EXISTS SectionMembers(SectionId TEXT NOT NULL REFERENCES Sections(Id),StudentId TEXT NOT NULL REFERENCES People(Id),Stamp TEXT NOT NULL,PRIMARY KEY(SectionId,StudentId));
        CREATE TABLE IF NOT EXISTS NoticeSectionMembers(NoticeId TEXT NOT NULL REFERENCES Notices(Id),SectionId TEXT NOT NULL REFERENCES Sections(Id),StudentId TEXT NOT NULL REFERENCES People(Id),Stamp TEXT NOT NULL,PRIMARY KEY(NoticeId,SectionId,StudentId));
        """);
    public static PortalSection[] List(PortalStore store)=>store.Query("SELECT Id,Name,Term,Revision FROM Sections ORDER BY Term,Name",r=>(id:r.GetString(0),name:r.GetString(1),term:r.GetString(2),revision:r.GetInt32(3))).Select(s=>new PortalSection(s.id,s.name,s.term,
        store.Query("SELECT TeacherId FROM SectionTeachers WHERE SectionId=$id",r=>r.GetString(0),("$id",s.id)).ToArray(),store.Query("SELECT StudentId,Stamp FROM SectionMembers WHERE SectionId=$id",r=>new PortalSectionMember(r.GetString(0),r.GetString(1)),("$id",s.id)).ToArray(),s.revision)).ToArray();
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder admin,RouteGroupBuilder bridge,PortalStore store)
    {
        api.MapGet("/sections",(HttpContext c)=>{var teacher=store.Person(Id(c))!;return teacher.Role=="teacher"&&VerifiedTeacher(store,teacher)?Results.Ok(List(store).Where(s=>s.TeacherIds.Contains(teacher.Id)||VerifiedTeacher(store,teacher,true)).Select(s=>new{s.Id,s.Name,s.Term,s.Revision,memberCount=s.Members.Count(m=>store.Person(m.StudentId) is{Active:true,Role:"student"})})):Error("교직원 권한이 필요합니다.",403);});
        admin.MapGet("/sections",()=>List(store));
        admin.MapPost("/sections",(SectionRequest r)=>Save(store,r,null));admin.MapPost("/sections/{id}",(string id,SectionRequest r)=>Save(store,r,id));
        bridge.MapPost("/sections",(SectionRequest r)=>Save(store,r,null));bridge.MapPost("/sections/{id}",(string id,SectionRequest r)=>Save(store,r,id));
    }
    static IResult Save(PortalStore store,SectionRequest request,string? id)
    {
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>100||string.IsNullOrWhiteSpace(request.Term)||request.Term.Length>40||request.TeacherIds is null||request.TeacherIds.Length is <1 or >20||request.StudentIds is null||request.StudentIds.Length>1000||request.TeacherIds.Distinct().Count()!=request.TeacherIds.Length||request.StudentIds.Distinct().Count()!=request.StudentIds.Length)return Error("분반 이름·학기·담당 교사·학생 목록을 확인하세요.");
        using var db=store.Open();using var tx=db.BeginTransaction(deferred:false);
        foreach(var teacher in request.TeacherIds){using var check=PortalStore.Command(db,"SELECT 1 FROM People WHERE Id=$id AND Role='teacher' AND Active=1",tx,("$id",teacher));if(check.ExecuteScalar() is null)return Error("활성 교사 계정을 선택하세요.");}
        foreach(var student in request.StudentIds){using var check=PortalStore.Command(db,"SELECT 1 FROM People WHERE Id=$id AND Role='student' AND Active=1 AND ClassId IS NOT NULL",tx,("$id",student));if(check.ExecuteScalar() is null)return Error("학교가 승인한 재학생을 선택하세요.");}
        if(id is null){id=Guid.NewGuid().ToString("N");using var add=PortalStore.Command(db,"INSERT INTO Sections(Id,Name,Term) VALUES($id,$name,$term)",tx,("$id",id),("$name",request.Name.Trim()),("$term",request.Term.Trim()));add.ExecuteNonQuery();}
        else{using var update=PortalStore.Command(db,"UPDATE Sections SET Name=$name,Term=$term,Revision=Revision+1 WHERE Id=$id AND Revision=$revision",tx,("$name",request.Name.Trim()),("$term",request.Term.Trim()),("$id",id),("$revision",request.Revision));if(update.ExecuteNonQuery()!=1)return Error("분반 명부가 변경되었습니다. 다시 조회하세요.",409);}
        using(var clear=PortalStore.Command(db,"DELETE FROM SectionTeachers WHERE SectionId=$id",tx,("$id",id)))clear.ExecuteNonQuery();
        foreach(var teacher in request.TeacherIds){using var add=PortalStore.Command(db,"INSERT INTO SectionTeachers VALUES($id,$teacher)",tx,("$id",id),("$teacher",teacher));add.ExecuteNonQuery();}
        var current=new List<string>();using(var read=PortalStore.Command(db,"SELECT StudentId FROM SectionMembers WHERE SectionId=$id",tx,("$id",id)))using(var reader=read.ExecuteReader())while(reader.Read())current.Add(reader.GetString(0));
        foreach(var removed in current.Except(request.StudentIds)){using var drop=PortalStore.Command(db,"DELETE FROM SectionMembers WHERE SectionId=$id AND StudentId=$student",tx,("$id",id),("$student",removed));drop.ExecuteNonQuery();}
        foreach(var student in request.StudentIds.Except(current)){using var add=PortalStore.Command(db,"INSERT INTO SectionMembers VALUES($id,$student,$stamp)",tx,("$id",id),("$student",student),("$stamp",Guid.NewGuid().ToString("N")));add.ExecuteNonQuery();}
        tx.Commit();return Results.Ok(new{id,revision=request.Revision+1});
    }
}

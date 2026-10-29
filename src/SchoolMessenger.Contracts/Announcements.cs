namespace SchoolMessenger.Contracts;

public record PortalClass(string Id, string Name, int Grade);
public record DirectoryPerson(string Id, string Role, string? ClassId, string? InternalUserId, bool CanBroadcast, string[] Classes, string[] Children, int ScopeVersion=1, Dictionary<string,string>? FamilyStamps=null,string? Name=null);
public record PortalSectionMember(string StudentId,string Stamp);
public record PortalSection(string Id,string Name,string Term,string[] TeacherIds,PortalSectionMember[] Members,int Revision=1);
public record PortalHomeroom(string ClassId,string TeacherId);
public record SectionRequest(string Name,string Term,string[] TeacherIds,string[] StudentIds,int Revision=0);
public record PortalDirectory(PortalClass[] Classes, DirectoryPerson[] People,PortalSection[]? Sections=null,PortalHomeroom[]? Homerooms=null);
public record TeacherProof(string Id, bool Active, bool CanBroadcast);
public record BridgeHeartbeat(TeacherProof[] Teachers);
public record AnnouncementRequest(string ClientId, string Title, string Body, string Audience, string[] ClassIds, bool All, string[] AttachmentIds,string[]? SectionIds=null,string Kind="notice");
public record AnnouncementTarget(string UserId, string StudentId, string ClassId, string Scope="");
public static class AnnouncementScopes
{
    public static string Stamp(DirectoryPerson student,DirectoryPerson? parent=null)=>student.ScopeVersion+":"+(parent is null?"student":parent.ScopeVersion+":"+parent.FamilyStamps?.GetValueOrDefault(student.Id));
}
public static class AnnouncementAudience
{
    public static AnnouncementTarget[] Build(PortalDirectory directory,DirectoryPerson teacher,AnnouncementRequest request)
    {
        var ids=request.SectionIds??[];var sections=(directory.Sections??[]).Where(s=>ids.Contains(s.Id)).ToArray();
        if(ids.Length>0&&(request.All||request.ClassIds.Length>0||ids.Distinct().Count()!=ids.Length||sections.Length!=ids.Length||sections.Any(s=>!s.TeacherIds.Contains(teacher.Id)&&!teacher.CanBroadcast)))throw new InvalidOperationException("담당 분반을 선택하세요. 학급/전체 발송과 분반 발송은 함께 선택하지 않습니다.");
        if(sections.Any(s=>s.Members.Length==0))throw new InvalidOperationException("수강 명부가 없는 분반에는 공지를 보낼 수 없습니다.");
        var classes=request.All?directory.Classes.Select(c=>c.Id).ToArray():request.ClassIds;
        if(ids.Length==0&&(!request.All&&classes.Any(id=>!teacher.Classes.Contains(id))||request.All&&!teacher.CanBroadcast))throw new InvalidOperationException("담당 학급 또는 전체 발송 권한이 필요합니다.");
        var students=directory.People.Where(p=>p.Role=="student"&&p.ClassId is not null&&(ids.Length>0?sections.Any(s=>s.Members.Any(m=>m.StudentId==p.Id)):classes.Contains(p.ClassId))).ToArray();
        var result=new List<AnnouncementTarget>();
        foreach(var student in students)
        {
            var membership=ids.Length==0?"":":"+string.Join("|",sections.Where(s=>s.Members.Any(m=>m.StudentId==student.Id)).OrderBy(s=>s.Id,StringComparer.Ordinal).Select(s=>s.Id+":"+s.Members.Single(m=>m.StudentId==student.Id).Stamp));
            if(request.Audience is "students" or "both")result.Add(new(student.Id,student.Id,student.ClassId!,AnnouncementScopes.Stamp(student)+membership));
            if(request.Audience is "parents" or "both")foreach(var parent in directory.People.Where(p=>p.Role=="parent"&&p.Children.Contains(student.Id)))result.Add(new(parent.Id,student.Id,student.ClassId!,AnnouncementScopes.Stamp(student,parent)+membership));
            foreach(var home in (directory.Homerooms??[]).Where(h=>h.ClassId==student.ClassId))if(directory.People.Any(p=>p.Id==home.TeacherId&&p.Role=="teacher"))result.Add(new(home.TeacherId,student.Id,student.ClassId!,"homeroom:"+home.TeacherId+membership));
        }
        return result.Distinct().OrderBy(t=>t.UserId,StringComparer.Ordinal).ThenBy(t=>t.StudentId,StringComparer.Ordinal).ToArray();
    }
}
public record BridgeAnnouncement(string TeacherId, AnnouncementRequest Request, AnnouncementTarget[] Targets);
public record PortalFile(string Id, string Name, long Size, long? ExpiresAt = null, bool Expired = false);
public record PortalNotice(string Id, string Title, string Body, string SenderName, long PublishedAt, long? ReadAt, bool Withdrawn, int RecipientCount, int ReadCount,string Kind="notice");
public record PortalReceipt(string UserId, string Name, string StudentName, long? ReadAt, bool Revoked, string ClassId,string? StudentId=null);
public record PortalNoticeDetail(PortalNotice Notice, PortalFile[] Attachments, PortalReceipt[]? Receipts);
public record PortalUser(string Id, string Name, string Role, bool CanBroadcast, string[] Classes, string Membership="regular");
public record PortalSession(string SchoolName, string CsrfToken, PortalUser? User);

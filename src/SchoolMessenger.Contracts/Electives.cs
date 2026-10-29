namespace SchoolMessenger.Contracts;

public record ElectiveCourse(string Id,string Name,string[] TeacherIds,string Type="general",int Credits=0,string Area="");
public record ElectiveGroup(string Id,string Name,int Required,ElectiveCourse[] Courses);
public record ElectiveRoundRequest(string ClientId,string Title,string Term,int[] Grades,long StartsAt,long EndsAt,ElectiveGroup[] Groups,int Revision=0,int MinCredits=0,int MaxCredits=0);
public record ElectiveTransition(string ClientId,int Revision,string Action);
public record ElectiveApplicationRequest(string ClientId,int Revision,string[] CourseIds);
public record ElectiveCancellation(string ClientId,int Revision);
public record ElectiveApplication(int Revision,bool Submitted,string[] CourseIds,long? UpdatedAt);
public record ElectiveRound(string Id,string Title,string Term,int[] Grades,long StartsAt,long EndsAt,string State,int Revision,ElectiveGroup[] Groups,bool CanApply,ElectiveApplication? Application=null,int MinCredits=0,int MaxCredits=0);
public record ElectiveStudent(string Id,string Name,string ClassId,string ClassName,bool Submitted,int Revision,string[] CourseIds,long? UpdatedAt);
public record ElectiveSection(string CourseId,string SectionId);
public record ElectiveReport(ElectiveRound Round,ElectiveStudent[] Students,ElectiveSection[] Sections,int ExcludedApplications);
public record ElectiveManagerChange(string UserId,bool Enabled);

public static class ElectiveRules
{
    public static readonly string[] Types = ["common", "general", "career", "convergence"];
    public static string TypeName(string type) => type switch { "common" => "공통과목", "general" => "일반선택", "career" => "진로선택", "convergence" => "융합선택", _ => "유형 확인 필요" };
    public static int Credits(ElectiveGroup[] groups, IEnumerable<string> selected)
    { var ids = selected.ToHashSet(); return groups.SelectMany(g => g.Courses).Where(c => ids.Contains(c.Id)).Sum(c => c.Credits); }
    public static string? SelectionError(ElectiveGroup[] groups, IEnumerable<string> selected, int minimum, int maximum)
    {
        var ids = selected.ToHashSet(); var courses = groups.SelectMany(g => g.Courses).ToArray();
        if (ids.Any(id => courses.All(c => c.Id != id))) return "이 회차의 과목만 선택하세요.";
        if (courses.Any(c => c.Type == "common" && !ids.Contains(c.Id))) return "공통과목은 필수로 포함해야 합니다.";
        foreach (var group in groups)
            if (group.Courses.Count(c => c.Type != "common" && ids.Contains(c.Id)) != group.Required) return group.Name + "에서 선택과목 " + group.Required + "개를 선택하세요.";
        var credits = Credits(groups, ids);
        return credits < minimum ? "총 " + minimum + "학점 이상 선택하세요." : maximum > 0 && credits > maximum ? "총 " + maximum + "학점 이하로 선택하세요." : null;
    }
    public static bool Feasible(ElectiveGroup[] groups, int minimum, int maximum)
    {
        if (minimum == 0 && maximum == 0) return true;
        var totals = new HashSet<int> { 0 };
        foreach (var group in groups)
        {
            var sums = Enumerable.Range(0, group.Required + 1).Select(_ => new HashSet<int>()).ToArray(); sums[0].Add(0);
            foreach (var course in group.Courses.Where(c => c.Type != "common"))
                for (var count = group.Required; count > 0; count--)
                    foreach (var value in sums[count - 1].ToArray()) sums[count].Add(value + course.Credits);
            var common = group.Courses.Where(c => c.Type == "common").Sum(c => c.Credits);
            totals = totals.SelectMany(prior => sums[group.Required].Select(sum => prior + common + sum)).ToHashSet();
        }
        return totals.Any(value => value >= minimum && (maximum == 0 || value <= maximum));
    }
}

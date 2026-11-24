using System;
using System.Collections.Generic;
using System.Linq;

namespace SchoolMessenger.Desktop;

// 분반 편성 from 수강신청 results: each course's students are split into sections of nearly equal size. The teacher
// sets the most students in one section, whether students of one homeroom stay together, and may change a course's
// number of sections. Placing the sections into timetable periods is left to the timetable program.
public static class Sections
{
    public sealed record Pupil(string Id, int ClassNo, int Number, string Name);
    public sealed record Course(int Group, string GroupName, int Semester, string Name, Pupil[] Pupils);
    public sealed record Section(Course Course, int Index, Pupil[] Pupils)
    {
        public string Label => Course.Name + " " + (char)('A' + Index);
    }
    public sealed record Options(int Most = 25, int Fewest = 0, bool KeepClasses = true, IReadOnlyDictionary<string, int>? Counts = null);
    public static string Key(Course course) => course.Group + "|" + course.Name;

    // Students who chose each course that is still open (a closed course's students pick again first).
    public sealed record Choice(string Name, int Semester, string[] Courses, string[]? Closed);
    public sealed record Answer(Pupil Pupil, string[][]? Picks);
    public static Course[] Courses(IEnumerable<Choice> groups, Answer[] answers)
    {
        var list = new List<Course>();
        foreach (var (group, index) in groups.Select((g, i) => (g, i)))
            foreach (var name in group.Courses.Except(group.Closed ?? []))
                list.Add(new(index, group.Name, group.Semester, name, answers
                    .Where(a => a.Picks is { } p && index < p.Length && p[index].Contains(name))
                    .Select(a => a.Pupil).OrderBy(p => p.ClassNo).ThenBy(p => p.Number).ToArray()));
        return [.. list];
    }

    public static int Count(Course course, Options options) =>
        course.Pupils.Length == 0 ? 0 : options.Counts?.GetValueOrDefault(Key(course)) is > 0 and var chosen ? Math.Min(chosen, course.Pupils.Length) : (course.Pupils.Length + Math.Max(1, options.Most) - 1) / Math.Max(1, options.Most);

    public static Section[] Split(Course course, Options options)
    {
        var count = Count(course, options); var pupils = course.Pupils;
        if (count == 0) return [];
        // Sizes differ by at most one: 52 students in 3 sections are 18, 17, 17.
        var sizes = Enumerable.Range(0, count).Select(i => pupils.Length / count + (i < pupils.Length % count ? 1 : 0)).ToArray();
        if (options.KeepClasses)
        {
            // In homeroom order, cut into consecutive runs, so a homeroom is split at most once.
            var at = 0;
            return sizes.Select((size, i) => { var part = pupils.Skip(at).Take(size).ToArray(); at += size; return new Section(course, i, part); }).ToArray();
        }
        // Dealt like cards in homeroom order, so every section gets a share of every homeroom.
        return Enumerable.Range(0, count).Select(i => new Section(course, i, pupils.Where((_, n) => n % count == i).ToArray())).ToArray();
    }

    public static Section[] Split(Course[] courses, Options options) => courses.SelectMany(c => Split(c, options)).ToArray();

    // What the AI is given to explain the result: numbers only, never a student.
    public static string Summary(Course[] courses, Options options) =>
        $"조건: 한 분반 최대 {options.Most}명, {(options.KeepClasses ? "같은 반 학생은 되도록 같은 분반" : "반을 고르게 섞음")}{(options.Fewest > 0 ? $", {options.Fewest}명보다 적은 과목은 확인 필요" : "")}\n" +
        string.Join("\n", courses.Select(c =>
        {
            var parts = Split(c, options);
            return $"{c.GroupName} · {c.Name}: 신청 {c.Pupils.Length}명 → " + (parts.Length == 0 ? "분반 없음" : $"{parts.Length}개 분반({string.Join("/", parts.Select(p => p.Pupils.Length))}명)");
        }));

    public const string Explain = "다음은 한국 고등학교의 선택과목 수강신청 결과로 만든 분반 편성 요약입니다(과목별 인원과 분반 수만 있고 학생 정보는 없습니다). 교육과정 담당 교사에게 결과를 쉬운 말로 4~6문장으로 설명하고, 인원이 너무 적거나(폐강 검토) 많은 과목, 분반 인원 차이, 분반 수가 많아 교사·교실이 더 필요한 과목처럼 확인할 점을 짚어 주세요. 숫자는 요약에 있는 것만 쓰고 지어내지 마세요." + StyleCheck.Guide;
}

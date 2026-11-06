using System;
using System.Linq;

namespace SchoolMessenger.Contracts;

public enum RosterView { Department, Grade, Duty, Unclassified }

public record RosterClassification(int[] Grades, string[] Duties)
{
    public static RosterClassification Empty { get; } = new([], []);
    public const string NoDuty = "업무 미지정";

    public bool Matches(string query) =>
        Grades.Any(grade => $"{grade}학년".Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        Duties.Any(duty => duty.Contains(query, StringComparison.OrdinalIgnoreCase));

    public string[] Groups(RosterView view, string department) => view switch
    {
        RosterView.Grade => Grades.Length == 0 ? ["학년 미지정"] : Grades.Select(grade => $"{grade}학년").ToArray(),
        RosterView.Duty => Duties.Length == 0 ? [NoDuty] : Duties,
        RosterView.Unclassified => Grades.Length == 0 && Duties.Length == 0 ? ["미분류"] : [],
        _ => [department]
    };

    public static RosterClassification? Normalize(int[]? grades, string[]? duties)
    {
        if (grades is null || grades.Length > 6 || grades.Any(grade => grade is < 1 or > 6) ||
            duties is null || duties.Length > 10 || duties.Any(duty => string.IsNullOrWhiteSpace(duty) ||
                duty.Length > 40 || duty.Any(char.IsControl) || duty.Contains(',') || duty.Trim() == NoDuty))
            return null;
        return new(grades.Distinct().Order().ToArray(),
            duties.Select(duty => duty.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

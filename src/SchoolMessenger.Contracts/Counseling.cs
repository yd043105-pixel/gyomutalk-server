using System.Globalization;

namespace SchoolMessenger.Contracts;

public record CounselingPeriod(int Id, string Start, string End,string Name="")
{
    public string Label => Name+" · "+Start + "–" + End;
}
public static class CounselingTimes
{
    public static readonly TimeSpan KoreaOffset = TimeSpan.FromHours(9);
    public static string Today => DateTimeOffset.UtcNow.ToOffset(KoreaOffset).ToString("yyyy-MM-dd");
    public static CounselingPeriod[] Periods(int grade)=>SchoolDay.Lessons(grade).Select(p=>new CounselingPeriod(p.Number,p.Start,p.End,p.Name)).ToArray();
    public static bool Day(string? value, out DateOnly date) => DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date);
    public static long At(DateOnly day, string time) => new DateTimeOffset(day.ToDateTime(TimeOnly.ParseExact(time,"HH:mm",CultureInfo.InvariantCulture)),KoreaOffset).ToUnixTimeMilliseconds();
    public static string Display(long at) => DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(KoreaOffset).ToString("MM.dd HH:mm");
}
public record CounselingOpen(string ClientId, string ClassId, string Date, int[] PeriodIds, long? OpensAt=null);
public record CounselingReserve(string ClientId, string SlotId, string StudentId, string? ReplaceBookingId=null);
public record CounselingStudent(string Id, string Name, string ClassId, string ClassName);
public record CounselingSlot(string Id, string TeacherName, string ClassId, string ClassName, long StartsAt, long EndsAt, long OpensAt, bool Closed, bool Booked);
public record CounselingBooking(string Id, string SlotId, string StudentId, string StudentName, string ClassName, string TeacherName, long StartsAt, long EndsAt, long? CanceledAt, string RequesterRole);
public record CounselingView(bool CanManage, PortalClass[] Classes, CounselingStudent[] Students, CounselingSlot[] Slots, CounselingBooking[] Bookings);

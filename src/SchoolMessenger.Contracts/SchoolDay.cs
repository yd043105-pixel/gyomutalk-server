using System.Globalization;

namespace SchoolMessenger.Contracts;

public record SchoolPeriod(int Number,string Name,string Start,string End,bool Lesson=true)
{
    public string Label=>Name+" · "+Start+"–"+End;
}
public static class SchoolDay
{
    public static readonly TimeSpan KoreaOffset=TimeSpan.FromHours(9);
    public static SchoolPeriod[] Schedule(int grade)
    {
        if(grade is <1 or >3)throw new ArgumentOutOfRangeException(nameof(grade));
        var result=new List<SchoolPeriod>{new(0,"아침 조회","08:30","08:50",false),new(1,"1교시","09:00","09:50"),new(2,"2교시","10:00","10:50"),new(3,"3교시","11:00","11:50")};
        if(grade==3){result.Add(new(-1,"점심시간","11:50","12:50",false));result.Add(new(4,"4교시","12:50","13:40"));}
        else{result.Add(new(4,"4교시","12:00","12:50"));result.Add(new(-1,"점심시간","12:50","13:50",false));}
        result.AddRange(new SchoolPeriod[]{new(5,"5교시","13:50","14:40"),new(6,"6교시","14:50","15:40"),new(7,"7교시","15:50","16:40"),new(-2,"청소시간","16:40","17:00",false),new(8,"8교시","17:00","17:50"),new(-3,"저녁시간","17:50","18:50",false),new(9,"야간1","19:00","19:50"),new(10,"야간2","20:00","20:50")});
        return result.ToArray();
    }
    public static SchoolPeriod[] Lessons(int grade,int last=10)=>Schedule(grade).Where(p=>p.Lesson&&p.Number<=last).ToArray();
    public static string Label(int grade,int period)=>grade is >=1 and <=3?Lessons(grade).FirstOrDefault(p=>p.Number==period)?.Label??$"{period}교시 · 시각 미등록":$"{period}교시 · 학년 확인 필요";
    public static string Timestamp(DateTime date,string time)=>new DateTimeOffset(DateTime.SpecifyKind(date.Date,DateTimeKind.Unspecified).Add(TimeSpan.ParseExact(time,@"hh\:mm",CultureInfo.InvariantCulture)),KoreaOffset).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
    public static bool RoomRange(int grade,long start,long end)
    {
        if(grade is <1 or >3)return false;
        var a=DateTimeOffset.FromUnixTimeMilliseconds(start).ToOffset(KoreaOffset);var b=DateTimeOffset.FromUnixTimeMilliseconds(end).ToOffset(KoreaOffset);
        if(a.Date!=b.Date||a.Second!=0||b.Second!=0||a.Millisecond!=0||b.Millisecond!=0)return false;
        var periods=Lessons(grade,7);return start<end&&periods.Any(p=>p.Start==a.ToString("HH:mm"))&&periods.Any(p=>p.End==b.ToString("HH:mm"));
    }
}

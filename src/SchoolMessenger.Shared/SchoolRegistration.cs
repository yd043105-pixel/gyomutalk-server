using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SchoolMessenger.Shared;

// Registration requests are private working files. Only reviewed public directory entries are published.
public sealed record SchoolRegistration(string RequestId,string SchoolId,string SchoolName,string Region,string ContactPhone,DateTimeOffset CreatedAt,string OfficeAddress,string AnnouncementsAddress)
{
    public const string Recipient="yd043105@gmail.com";
    public static SchoolRegistration Create(string id,string name,string region,string phone,string office,string announcements)
    {
        id=id.Trim();name=name.Trim();region=region.Trim();phone=phone.Trim();office=office.Trim();announcements=announcements.Trim();
        var digits=new string(phone.Where(char.IsAsciiDigit).ToArray());
        if(!Regex.IsMatch(id,"^[A-Za-z0-9_-]{5,64}$")||string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl)||string.IsNullOrWhiteSpace(region)||region.Length>80||region.Any(char.IsControl))throw new InvalidOperationException("학교 식별자·학교명·지역을 확인하세요.");
        if(phone.Length>24||!Regex.IsMatch(phone,"^[0-9 ()-]+$")||digits.Length is <9 or >11||!digits.StartsWith('0')||new[]{"010","011","016","017","018","019"}.Any(digits.StartsWith))throw new InvalidOperationException("개인 휴대전화 대신 학교 홈페이지에 공개된 학교 대표전화를 입력하세요.");
        if(office.Length==0&&announcements.Length==0||office.Length>0&&!SchoolDirectory.ServerAddress(office)||announcements.Length>0&&!SchoolDirectory.ServerAddress(announcements)||office.Length>0&&announcements.Length>0&&new Uri(office)==new Uri(announcements))throw new InvalidOperationException("학교의 역할별 HTTPS 주소를 확인하세요.");
        if(new[]{office,announcements}.Where(v=>v.Length>0).Any(v=>new Uri(v).IsLoopback))throw new InvalidOperationException("다른 기기에서 연결할 수 있는 실제 학교 HTTPS 주소를 입력하세요.");
        return new(Guid.NewGuid().ToString("N"),id,name,region,phone,DateTimeOffset.UtcNow,office,announcements);
    }
    public string ToJson()=>JsonSerializer.Serialize(new{schema=1,status="pending",requestId=RequestId,schoolId=SchoolId,schoolName=SchoolName,region=Region,contactPhone=ContactPhone,createdAt=CreatedAt,officeAddress=OfficeAddress,announcementsAddress=AnnouncementsAddress},new JsonSerializerOptions{WriteIndented=true});
    public string Mailto(string file)=>"mailto:"+Recipient+"?subject="+Uri.EscapeDataString("[교무톡 학교 등록 신청] "+SchoolName)+"&body="+Uri.EscapeDataString("학교 검색 등록을 신청합니다.\r\n학교: "+SchoolName+"\r\n학교 식별자: "+SchoolId+"\r\n신청 파일: "+Path.GetFileName(file)+"\r\n\r\n저장한 신청 JSON 파일을 직접 첨부한 뒤 메일을 보내주세요. 이 메일 작성창을 여는 것만으로 접수되지는 않습니다.");
}

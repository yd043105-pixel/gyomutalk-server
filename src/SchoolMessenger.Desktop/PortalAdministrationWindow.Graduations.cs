using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public sealed partial class PortalAdministrationWindow
{
    async Task Graduations()
    {
        body.Children.Clear();Button("명부로",Load);
        body.Children.Add(Text("학교가 확정한 졸업 대상과 처리 시각을 등록하세요. 그 시각부터 로그인을 차단하고 학생 개인정보를 자동 파기합니다. 다른 재학 자녀가 없는 학부모도 함께 탈퇴합니다. 처리 후 복구할 수 없습니다."));
        var status=await api!.Get<JsonElement>("api/admin/graduations");
        body.Children.Add(Text("백업·교내 서버 파기 확인 대기: "+status.GetProperty("pendingErasure").GetInt32()+"건"));
        foreach(var row in status.GetProperty("scheduled").EnumerateArray())
        {
            var id=row.GetProperty("studentId").GetString()!;
            body.Children.Add(Text(row.GetProperty("name").GetString()+" · "+row.GetProperty("className").GetString()+" · "+DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("at").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture)));
            Button("이 학생의 예약 취소",async()=>{await api.Send<JsonElement>(HttpMethod.Delete,"api/admin/graduations/"+id);await Graduations();});
        }
        var time=Field("졸업 처리 시각 (한국 시간 · yyyy-MM-dd HH:mm)");
        var rooms=(await api.Get<JsonElement>("api/admin/classes")).EnumerateArray().ToDictionary(c=>c.GetProperty("id").GetString()!,c=>c.GetProperty("name").GetString());
        var people=await api.Get<JsonElement>("api/admin/people");var choices=new Dictionary<string,CheckBox>();
        body.Children.Add(Text("대상 학생을 직접 선택하세요. 학년만 보고 자동으로 졸업 처리하지 않습니다."));
        foreach(var person in people.EnumerateArray().Where(p=>p.GetProperty("role").GetString()=="student"))
        {var check=new CheckBox{Content=person.GetProperty("name").GetString()+" · "+rooms.GetValueOrDefault(person.GetProperty("classId").GetString()??"")+(person.GetProperty("active").GetBoolean()?"":" · 승인 대기/중지"),Margin=new Thickness(0,4,0,4)};choices.Add(person.GetProperty("id").GetString()!,check);body.Children.Add(check);}
        var confirmation=Field("확인 문구: 졸업 자동 탈퇴 및 파기");
        Button("선택 학생의 졸업 예약 저장",async()=>
        {
            if(!DateTime.TryParseExact(time.Text,"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))throw new InvalidOperationException("처리 시각을 yyyy-MM-dd HH:mm 형식으로 입력하세요.");
            var at=new DateTimeOffset(DateTime.SpecifyKind(parsed,DateTimeKind.Unspecified),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
            var ids=choices.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).ToArray();
            if(ids.Length==0)throw new InvalidOperationException("학교가 확인한 졸업 대상 학생을 선택하세요.");
            if(MessageBox.Show(InteractionOwner,$"선택 학생 {ids.Length}명 · {time.Text} (한국 시간)\n예약 시각에 학생 계정과 마지막 자녀가 졸업하는 학부모 계정을 탈퇴·파기합니다. 저장할까요?","졸업 파기 예약",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            await api.Send<JsonElement>(HttpMethod.Post,"api/admin/graduations",new{studentIds=ids,at,confirmation=confirmation.Text});await Graduations();
        });
    }
}

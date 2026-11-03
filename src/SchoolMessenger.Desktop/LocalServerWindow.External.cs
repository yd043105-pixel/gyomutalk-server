using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class LocalServerWindow
{
    internal static string ExternalHostFrom(string text)
    {
        var value=text.Trim();if(value.Length==0)return "";
        if(!Uri.TryCreate(value.Contains("://")?value:"https://"+value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.PathAndQuery!="/"||uri.UserInfo.Length>0||Uri.CheckHostName(uri.Host)!=UriHostNameType.Dns||!uri.Host.Contains('.'))
            throw new InvalidOperationException("학교가 관리하는 도메인 주소를 넣으세요. 예: gyomutalk.school.example");
        return uri.Host;
    }
    void ShowExternal(ServerCard c)
    {
        var window=new Window{Owner=this,Resources=Resources,Title="학교 밖 조회",Width=560,Height=620,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        window.SetResourceReference(BackgroundProperty,"Surface");
        var stack=new StackPanel{Margin=new Thickness(24)};window.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        void Note(string text){var note=AdminUi.Label(text,"Muted",12);note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,0);stack.Children.Add(note);}
        stack.Children.Add(AdminUi.Heading("학교 밖 조회"));
        Note("켜면 교직원이 학교 밖에서 휴대폰 앱으로 본인 시간표, 할 일, 제출 현황, 학교 일정을 볼 수 있습니다. 메시지, 입력·변경, 관리자 기능은 학교 밖에서 열리지 않습니다.");
        Note("접속 내용은 Cloudflare를 거쳐 전달되고, 학교 업무 서버에 외부 경로가 생깁니다. 켜기 전에 학교 보안 지침과 교육청 기준을 확인하고 학교장 승인 문서에 이 내용을 포함하세요.");
        var heading=AdminUi.Heading("설정 순서");heading.Margin=new Thickness(0,20,0,4);stack.Children.Add(heading);
        var target="http://127.0.0.1:"+SimpleOperation.ExternalPort;
        Note("1) 학교 계정으로 Cloudflare Zero Trust에서 터널을 만들고, 안내되는 명령으로 이 PC에 커넥터를 설치합니다. 토큰은 이 프로그램에 넣지 않습니다.");
        Note("2) 터널의 공개 주소(Public Hostname)를 학교 도메인으로 정하고, 연결 대상(Service)을 아래 주소로 넣습니다.");
        var targetRow=new DockPanel{Margin=new Thickness(0,6,0,0)};var copy=AdminUi.CopyButton(()=>target,"연결 대상 주소");DockPanel.SetDock(copy,Dock.Right);targetRow.Children.Add(copy);targetRow.Children.Add(AdminUi.Label(target,"Ink",13,true));stack.Children.Add(targetRow);
        Note("3) 같은 공개 주소를 아래에 넣고 저장합니다. 서버가 실행 중이면 다시 시작합니다.");
        var host=new TextBox{Text=c.ExternalHost,Margin=new Thickness(0,8,0,0)};System.Windows.Automation.AutomationProperties.SetName(host,"학교 밖 공개 주소");stack.Children.Add(host);
        var state=AdminUi.Label(c.ExternalHost.Length>0?"켜짐":"꺼짐","Ink",13);state.TextWrapping=TextWrapping.Wrap;state.Margin=new Thickness(0,8,0,0);stack.Children.Add(state);
        var actions=new WrapPanel{Margin=new Thickness(0,12,0,0)};stack.Children.Add(actions);
        var save=new Button{Content="저장",Margin=new Thickness(0,0,8,0)};save.SetResourceReference(StyleProperty,"PrimaryButton");actions.Children.Add(save);
        var check=new Button{Content="연결 확인",Margin=new Thickness(0,0,8,0)};actions.Children.Add(check);
        var off=new Button{Content="끄기"};actions.Children.Add(off);
        async Task Apply(string value)
        {
            c.ExternalHost=value;c.ChecklistKey="";SaveSettings();
            if(c.Host.Running){state.Text="서버를 다시 시작해 적용하는 중…";await c.Host.Stop();await Start(c);}
            Refresh();state.Text=(value.Length>0?"켜짐 · "+value:"꺼짐")+(c.Host.Running?" · 서버에 적용했습니다.":" · 서버를 시작하면 적용됩니다.");
        }
        async void Run(Func<Task> work)
        {
            save.IsEnabled=check.IsEnabled=off.IsEnabled=false;
            try{await work();}
            catch(Exception e)when(e is InvalidOperationException or IOException or UnauthorizedAccessException){state.Text=e.Message;}
            finally{save.IsEnabled=check.IsEnabled=off.IsEnabled=true;}
        }
        save.Click+=(_,_)=>Run(()=>Apply(ExternalHostFrom(host.Text)));
        off.Click+=(_,_)=>Run(async()=>{host.Clear();await Apply("");});
        check.Click+=(_,_)=>Run(async()=>
        {
            var value=ExternalHostFrom(host.Text);if(value.Length==0)throw new InvalidOperationException("공개 주소를 먼저 넣으세요.");
            state.Text="연결을 확인하는 중…";state.Text=await ExternalCheck(value,c.School.Text.Trim());
        });
        window.ShowDialog();
    }
    // Goes out through the public address the way a phone would, so a school network that blocks the connector shows up here.
    static async Task<string> ExternalCheck(string host,string school)
    {
        using var http=new HttpClient{BaseAddress=new Uri("https://"+host+"/"),Timeout=TimeSpan.FromSeconds(15)};
        try
        {
            using var info=await http.GetAsync("server-info");
            if(!info.IsSuccessStatusCode)return "공개 주소에는 닿았지만 이 서버가 응답하지 않습니다("+(int)info.StatusCode+"). 터널의 연결 대상과 서버 실행 상태를 확인하세요.";
            using var body=JsonDocument.Parse(await info.Content.ReadAsStringAsync());
            if(body.RootElement.GetProperty("schoolName").GetString()!=school)return "공개 주소가 이 학교가 아닌 다른 서버로 연결됩니다. 터널 설정을 확인하세요.";
            using var closed=await http.GetAsync("api/admin/users");
            return closed.StatusCode==System.Net.HttpStatusCode.Forbidden?"연결됨 · 학교 밖 조회 입구가 열려 있고 관리자 기능은 막혀 있습니다.":"공개 주소가 조회 전용 입구로 연결되지 않습니다. 연결 대상이 "+"http://127.0.0.1:"+SimpleOperation.ExternalPort+" 인지 확인하세요.";
        }
        catch(Exception e)when(e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException){return "공개 주소에 연결하지 못했습니다. 커넥터 실행 상태와 학교 방화벽을 확인하세요.";}
    }
}

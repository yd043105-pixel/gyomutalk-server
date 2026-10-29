using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public sealed partial class PortalAdministrationWindow:Window
{
    readonly StackPanel body=new(){Margin=new Thickness(24)};readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20,10,20,12)};Api? api;bool busy;
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    readonly string? initialAddress;
    readonly bool ownsApi=true;
    readonly Func<Task>? workspaceSignOut;
    Window InteractionOwner=>Window.GetWindow(body)??this;
    internal PortalAdministrationWindow(Window owner,Api source,Func<Task> signOut):this(owner)
    {
        api=source;ownsApi=false;workspaceSignOut=signOut;var initialized=false;
        ((FrameworkElement)Content).Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(Load);};
    }
    public PortalAdministrationWindow(Window owner,string? address=null)
    {
        initialAddress=address;Owner=owner;Resources=owner.Resources;Title="학생·학부모 · 학교 소식 관리";Width=690;Height=760;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var dock=new DockPanel();DockPanel.SetDock(error,Dock.Bottom);dock.Children.Add(error);dock.Children.Add(new ScrollViewer{Content=body});Content=dock;Closed+=(_,_)=>{if(ownsApi)api?.Dispose();};Login();
    }
    static TextBlock Text(string value)=>new(){Text=value,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,6)};
    void Button(string label,Func<Task> action){var button=new Button{Content=label,Margin=new Thickness(0,4,0,8)};button.Click+=async(_,_)=>await Run(action);body.Children.Add(button);}
    TextBox Field(string title,string value=""){body.Children.Add(Text(title));var field=new TextBox{Text=value};body.Children.Add(field);return field;}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;error.Text="";try{await action();}catch(Exception e){error.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    void Login()
    {
        foreach(var window in OwnedWindows.Cast<Window>().ToArray())window.Close();
        body.Children.Clear();body.Children.Add(Text("외부 학교 소식 서버의 별도 관리자 계정으로 로그인하세요. 비밀번호와 로그인 쿠키는 이 창에서만 사용합니다."));
        var address=Field("학교 소식 서버 HTTPS 주소",initialAddress??"");var username=Field("학교 소식 관리자 아이디","admin");body.Children.Add(Text("관리자 비밀번호"));var password=new PasswordBox();body.Children.Add(password);
        Button("관리자 로그인",async()=>{api?.Dispose();api=new Api(address.Text);try{await api.Login(username.Text,password.Password,false);}finally{password.Clear();}await api.Get<JsonElement>("api/admin/status");await Load();new ServerManagementWindow(this,api,false).Show();});
    }
    async Task Load()
    {
        body.Children.Clear();Button("오류 신고 · 개선 의견 관리",async()=>{if(InteractionOwner is LocalServerWindow manager)await manager.OpenAdministration("feedback","announcements");else new FeedbackWindow(InteractionOwner,api!,true).Show();});Button("학급·학교 명부 등록",Directory);Button("졸업 자동 탈퇴·파기 예약",Graduations);Button("보호자 연결 신청 심사",GuardianRequests);Button("보호자 연결 관리",Families);Button("로그아웃",async()=>{if(workspaceSignOut is not null){await workspaceSignOut();return;}try{await api!.Send<JsonElement>(HttpMethod.Post,"api/logout");}finally{api!.Dispose();api=null;Login();}});
        await RecoveryReview.Add(body,api!,InteractionOwner,Load);
        var people=await api!.Get<JsonElement>("api/admin/people");body.Children.Add(Text("가입 승인 · 계정 상태 · 고유번호"));
        foreach(var person in people.EnumerateArray())Button(person.GetProperty("name").GetString()+" · "+person.GetProperty("role").GetString()+" · "+(person.GetProperty("active").GetBoolean()?"활성":"승인 대기/중지"),()=>Person(person));
    }
    async Task Person(JsonElement person)
    {
        body.Children.Clear();Button("명부로",Load);var id=person.GetProperty("id").GetString()!;var role=person.GetProperty("role").GetString();var name=Field("이름",person.GetProperty("name").GetString()!);
        var active=new CheckBox{Content=role=="student"?"학교 명부 확인 후 학생 가입 승인 / 사용 허용":"계정 사용 허용",IsChecked=person.GetProperty("active").GetBoolean()};body.Children.Add(active);
        if(person.GetProperty("application").ValueKind==JsonValueKind.Object)body.Children.Add(Text("신청 출석 번호: "+person.GetProperty("application").GetProperty("studentNumber")+" · 학교 명부와 대조한 뒤 승인하세요."));
        var classes=await api!.Get<JsonElement>("api/admin/classes");var choices=classes.EnumerateArray().Select(c=>new Choice(c.GetProperty("id").GetString()!,c.GetProperty("name").GetString()!)).ToArray();
        var studentClass=new ComboBox{ItemsSource=choices};if(role=="student"){studentClass.SelectedItem=choices.FirstOrDefault(c=>c.Id==person.GetProperty("classId").GetString());body.Children.Add(Text("소속 학급"));body.Children.Add(studentClass);}
        var teachers=new Dictionary<string,CheckBox>();var all=new CheckBox{Content="학교 전체 공지 발송 허용",IsChecked=person.GetProperty("canBroadcast").GetBoolean()};
        if(role=="teacher"){body.Children.Add(all);var selected=person.GetProperty("classIds").EnumerateArray().Select(c=>c.GetString()).ToArray();foreach(var c in choices){var check=new CheckBox{Content=c.Name,IsChecked=selected.Contains(c.Id)};teachers[c.Id]=check;body.Children.Add(check);}}
        Button("계정 상태 · 학교 승인 저장",async()=>{await api.Send<JsonElement>(HttpMethod.Patch,"api/admin/people/"+id,new{name=name.Text,active=active.IsChecked==true,classId=role=="student"?(studentClass.SelectedItem as Choice)?.Id:null,classIds=role=="teacher"?teachers.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).ToArray():[],canBroadcast=role=="teacher"&&all.IsChecked==true});await Load();});
        if(role=="student"&&person.GetProperty("active").GetBoolean())
        {
            Button("학생 고유번호 확인",async()=>{var value=await api.Get<JsonElement>("api/admin/people/"+id+"/student-code");MessageBox.Show(InteractionOwner,"학생 본인·보호자에게 개별 전달하세요.\n"+value.GetProperty("code").GetString(),"학생 고유번호");});
            Button("고유번호 재발급",async()=>{if(MessageBox.Show(InteractionOwner,"이전 번호는 무효화됩니다. 기존 보호자 연결은 유지됩니다. 재발급할까요?","고유번호",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;var value=await api.Send<JsonElement>(HttpMethod.Post,"api/admin/people/"+id+"/student-code/reissue");MessageBox.Show(InteractionOwner,value.GetProperty("code").GetString(),"새 고유번호");});
        }
        if(!person.GetProperty("registered").GetBoolean())Button("학교 초대 코드 발급",async()=>{var invite=await api.Send<JsonElement>(HttpMethod.Post,"api/admin/people/"+id+"/invite");MessageBox.Show(InteractionOwner,"학교 초대 코드를 본인에게 개별 전달하세요.\n"+invite.GetProperty("code").GetString(),"학교 초대");});
    }
    async Task Directory()
    {
        body.Children.Clear();Button("가입·계정 관리로",Load);var className=Field("학급 이름");var grade=Field("학년 · 1~3");Button("학급 등록",async()=>{if(!int.TryParse(grade.Text,out var number))throw new InvalidOperationException("학년을 입력하세요.");await api!.Send<JsonElement>(HttpMethod.Post,"api/admin/classes",new{name=className.Text,grade=number});await Directory();});
        var name=Field("학교 명부의 이름");var role=new ComboBox{ItemsSource=new[]{"student","parent","teacher"},SelectedIndex=0};body.Children.Add(Text("역할 · student 학생 / parent 보호자 / teacher 교사"));body.Children.Add(role);
        var classes=await api!.Get<JsonElement>("api/admin/classes");var choices=classes.EnumerateArray().Select(c=>new Choice(c.GetProperty("id").GetString()!,c.GetProperty("name").GetString()!)).ToArray();var scope=new ComboBox{ItemsSource=choices,SelectedIndex=choices.Length>0?0:-1};body.Children.Add(Text("학생 소속 또는 교사 담당 학급"));body.Children.Add(scope);var internalId=Field("교사인 경우 교내 계정 ID · PC 계정 관리에서 확인");
        Button("명부 계정 등록",async()=>{var selected=role.SelectedItem as string??"student";var room=(scope.SelectedItem as Choice)?.Id;await api.Send<JsonElement>(HttpMethod.Post,"api/admin/people",new{name=name.Text,role=selected,classId=selected=="student"?room:null,internalUserId=selected=="teacher"?internalId.Text:null,classIds=selected=="teacher"&&room is not null?new[]{room}:[],canBroadcast=false});await Load();});
    }
    async Task GuardianRequests()
    {
        body.Children.Clear();Button("계정 관리로",Load);
        body.Children.Add(Text("고유번호만으로 보호자 신원을 확인할 수 없습니다. 학교의 확인 절차로 본인과 자녀 관계를 확인한 뒤 승인하세요. 신청은 7일 후 또는 번호 재발급 시 다시 받아야 합니다."));
        var people=await api!.Get<JsonElement>("api/admin/people");
        var names=people.EnumerateArray().ToDictionary(p=>p.GetProperty("id").GetString()!,p=>p.GetProperty("name").GetString()!);
        var requests=await api.Get<JsonElement>("api/admin/guardian-requests");
        if(requests.GetArrayLength()==0)body.Children.Add(Text("심사할 연결 신청이 없습니다."));
        foreach(var request in requests.EnumerateArray())
        {
            var parent=request.GetProperty("parentId").GetString()!;var student=request.GetProperty("studentId").GetString()!;
            var label=names.GetValueOrDefault(parent,"계정 확인 필요")+" · "+names.GetValueOrDefault(student,"학생 확인 필요");
            body.Children.Add(Text(label));
            Button("보호자 확인 후 승인",async()=>
            {
                if(MessageBox.Show(InteractionOwner,label+"\n학교 절차로 본인과 보호자 관계를 확인했습니까? 승인하면 자녀의 소식과 시간표를 열람할 수 있습니다.","보호자 연결 심사",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                await api.Send<JsonElement>(HttpMethod.Post,"api/admin/guardian-requests/"+parent+"/"+student+"/approve");await GuardianRequests();
            });
            Button("신청 반려",async()=>
            {
                if(MessageBox.Show(InteractionOwner,label+"\n이 연결 신청을 반려할까요?","보호자 연결 심사",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                await api.Send<JsonElement>(HttpMethod.Delete,"api/admin/guardian-requests/"+parent+"/"+student);await GuardianRequests();
            });
        }
    }
    async Task Families()
    {
        body.Children.Clear();Button("계정 관리로",Load);var rows=await api!.Get<JsonElement>("api/admin/families");foreach(var row in rows.EnumerateArray())Button(row.GetProperty("parentName").GetString()+" · "+row.GetProperty("studentName").GetString()+" · 연결 해제",async()=>{if(MessageBox.Show(InteractionOwner,"열람 권한을 회수하고 학생 고유번호를 변경합니다. 연결을 해제할까요?","보호자 관계 해제",MessageBoxButton.YesNo)==MessageBoxResult.Yes){await api.Send<JsonElement>(HttpMethod.Delete,"api/admin/families/"+row.GetProperty("parentId").GetString()+"/"+row.GetProperty("studentId").GetString());await Families();}});
    }
}

using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolMessenger.Shared;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using Orientation=System.Windows.Controls.Orientation;

namespace SchoolMessenger.Desktop;

public sealed class AnnouncementWindow:Window
{
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(24)};
    readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
    readonly List<string> files=[];
    bool busy;
    object? pending;
    public AnnouncementWindow(MainWindow main)
    {
        api=main.Api!;Owner=main;Resources=main.Resources;Title="교무톡 · 가정통신문";Width=760;Height=720;MinWidth=620;MinHeight=500;
        Style=(Style)FindResource("SchoolWindow");WindowStartupLocation=WindowStartupLocation.CenterOwner;Content=new ScrollViewer{Content=body};
        Loaded+=async(_,_)=>await Run(Load);
    }
    static TextBlock Text(string value,int size=13)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
    void Action(string label,Func<Task> action){var button=new Button{Content=label,Margin=new Thickness(0,0,8,10),HorizontalAlignment=HorizontalAlignment.Left};button.Click+=async(_,_)=>await Run(action);body.Children.Add(button);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;}}
    async Task Load()
    {
        body.Children.Clear();body.Children.Add(Text("가정통신문",24));body.Children.Add(Text("학생·학부모 앱으로 가정통신문과 일반 공지를 보냅니다. 발송 대상 학급·분반을 선택할 수 있습니다."));body.Children.Add(feedback);
        var setup=await api.Get<JsonElement>("api/external-announcements/setup");
        if(!setup.GetProperty("configured").GetBoolean()||setup.GetProperty("teacherId").ValueKind==JsonValueKind.Null)
        {body.Children.Add(Text("공지 관리자에게 외부 서버 설정과 교사 계정 연결을 요청하세요."));body.Children.Add(Text("교내 계정 번호: "+setup.GetProperty("internalUserId").GetString()));Action("새로고침",Load);return;}
        if(!setup.GetProperty("connected").GetBoolean())body.Children.Add(Text("외부 서버 연결을 기다리고 있습니다. 대기 중인 발송·회수는 연결이 복구되면 이어집니다."));
        Action("새로고침 · 발송 현황 확인",Load);
        var kind=new ComboBox{ItemsSource=new[]{"가정통신문 · JPG/PNG 한 장당 200KB","일반 공지 · JPG/PNG 한 장당 200KB"},SelectedIndex=0,Margin=new Thickness(0,0,0,12)};body.Children.Add(kind);
        body.Children.Add(Text("모든 공지 첨부는 JPG·PNG로 바로 표시됩니다. 한 장당 200KB · 최대 5장 · 30일 보관"));
        body.Children.Add(Text("새 공지 제목"));var title=new TextBox{MaxLength=200,Margin=new Thickness(0,0,0,12)};body.Children.Add(title);
        body.Children.Add(Text("공지 내용"));var message=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=10000,Margin=new Thickness(0,0,0,12)};body.Children.Add(message);
        var audience=new ComboBox{ItemsSource=new[]{"학생과 보호자","보호자","학생"},SelectedIndex=0,Margin=new Thickness(0,0,0,12)};body.Children.Add(audience);
        var classes=new Dictionary<string,CheckBox>();var scope=new WrapPanel();foreach(var room in setup.GetProperty("classes").EnumerateArray()){var check=new CheckBox{Content=room.GetProperty("name").GetString(),Margin=new Thickness(0,0,18,12)};classes.Add(room.GetProperty("id").GetString()!,check);scope.Children.Add(check);}body.Children.Add(scope);
        var sections=new Dictionary<string,CheckBox>();var sectionPanel=new WrapPanel();body.Children.Add(Text("선택과목 분반 · 학급 선택과 함께 사용하지 않습니다."));foreach(var section in setup.GetProperty("sections").EnumerateArray()){var check=new CheckBox{Content=section.GetProperty("name").GetString()+" · "+section.GetProperty("memberCount")+"명",IsEnabled=section.GetProperty("memberCount").GetInt32()>0,Margin=new Thickness(0,0,18,12)};sections[section.GetProperty("id").GetString()!]=check;sectionPanel.Children.Add(check);}body.Children.Add(sectionPanel);
        var whole=new CheckBox{Content="학교 전체 발송",Margin=new Thickness(0,0,0,12)};if(setup.GetProperty("canBroadcast").GetBoolean())body.Children.Add(whole);
        var attachments=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};body.Children.Add(attachments);files.Clear();pending=null;
        var addFile=new Button{Content="공지 이미지 추가 · 한 장당 200KB",Margin=new Thickness(0,0,8,10),HorizontalAlignment=HorizontalAlignment.Left};body.Children.Add(addFile);

        addFile.Click+=async(_,_)=>await Run(async()=>
        {
            if(pending is not null)throw new InvalidOperationException("전송 결과 확인 후 새 공지를 작성하세요.");if(files.Count>=5)throw new InvalidOperationException("공지 이미지는 최대 5장입니다.");
            var dialog=new Microsoft.Win32.OpenFileDialog{Filter="JPG·PNG 이미지|*.jpg;*.jpeg;*.png"};if(dialog.ShowDialog(this)!=true)return;
            FilePolicy.NoticeImage(dialog.FileName,Path.GetFileName(dialog.FileName));
            using var content=new MultipartFormDataContent();content.Add(new StreamContent(File.OpenRead(dialog.FileName)),"file",Path.GetFileName(dialog.FileName));
            content.Add(new StringContent(kind.SelectedIndex==0?"home-letter":"notice"),"kind");
            var file=await api.Send<JsonElement>(HttpMethod.Post,"api/external-announcements/attachments",content:content);files.Add(file.GetProperty("id").GetString()!);attachments.Text+=file.GetProperty("name").GetString()+Environment.NewLine;
            kind.IsEnabled=false;
        });
        Action("공지 보내기 / 같은 요청 재시도",async()=>
        {
            if(pending is null)
            {
                if(string.IsNullOrWhiteSpace(title.Text)||string.IsNullOrWhiteSpace(message.Text)||whole.IsChecked!=true&&!classes.Values.Any(c=>c.IsChecked==true)&&!sections.Values.Any(c=>c.IsChecked==true))throw new InvalidOperationException("제목·내용·수신 학급을 입력하세요.");
                if(MessageBox.Show(this,"선택한 학생·보호자에게 학교 밖으로 공지를 발송할까요?","공지 발송 확인",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                pending=new{clientId=Guid.NewGuid().ToString("N"),title=title.Text,body=message.Text,audience=audience.SelectedIndex==1?"parents":audience.SelectedIndex==2?"students":"both",classIds=whole.IsChecked==true?[]:classes.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).ToArray(),all=whole.IsChecked==true,attachmentIds=files.ToArray(),sectionIds=whole.IsChecked==true?Array.Empty<string>():sections.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).ToArray(),kind=kind.SelectedIndex==0?"home-letter":"notice"};
                title.IsEnabled=message.IsEnabled=audience.IsEnabled=whole.IsEnabled=kind.IsEnabled=false;foreach(var check in classes.Values)check.IsEnabled=false;
            }
            await api.Send<JsonElement>(HttpMethod.Post,"api/external-announcements",pending);pending=null;await Load();feedback.Text="발송 대기열에 등록했습니다. 외부 서버 확인 후 게시됩니다.";
        });
        body.Children.Add(Text("보낸 공지",19));var rows=await api.Get<JsonElement[]>("api/external-announcements");
        if(rows.Length==0)body.Children.Add(Text("아직 보낸 공지가 없습니다."));
        foreach(var row in rows)
        {
            var state=row.GetProperty("state").GetString();var id=row.GetProperty("id").GetString()!;body.Children.Add(Text(row.GetProperty("request").GetProperty("title").GetString()+" · "+(state switch{"pending"=>"전송 대기","published"=>"게시됨","needs-review"=>"대상·권한 재확인 필요","withdraw-pending"=>"회수 대기","withdrawn"=>"회수됨",_=>state}),15));
            if(row.GetProperty("error").ValueKind==JsonValueKind.String)body.Children.Add(Text(row.GetProperty("error").GetString()!));
            if(row.GetProperty("detail").ValueKind!=JsonValueKind.Null){var detail=row.GetProperty("detail");var notice=detail.GetProperty("notice");body.Children.Add(Text("확인 "+notice.GetProperty("readCount")+" / "+notice.GetProperty("recipientCount")));foreach(var receipt in detail.GetProperty("receipts").EnumerateArray())body.Children.Add(Text(receipt.GetProperty("name").GetString()+" · "+receipt.GetProperty("studentName").GetString()+" · "+(receipt.GetProperty("revoked").GetBoolean()?"접근 회수":receipt.GetProperty("readAt").ValueKind==JsonValueKind.Null?"미확인":"확인")));}
            if(state!="withdrawn")Action("이 공지 회수 요청",async()=>{if(MessageBox.Show(this,"수신자의 공지·첨부 접근을 회수할까요? 연결이 끊긴 경우 회수도 연결 복구 후 적용됩니다.","공지 회수",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/external-announcements/"+id+"/retract");await Load();});
        }
    }
}

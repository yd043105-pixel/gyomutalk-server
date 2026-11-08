using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

// Phones and teacher PCs reach the school without typing an address: the school connection QR carries the server
// address and its certificate, the Android app checks for news once a day at the school's time, and a moved server
// tells the apps its new address. The last two travel through server-info from school-app.json beside the data.
public sealed partial class LocalServerWindow
{
    internal static string SchoolLink(string server,string fingerprint,string school,string schoolAddress,string service)
        =>"schooltalk://school?server="+Uri.EscapeDataString(server.TrimEnd('/'))+(fingerprint.Length==64?"&pin="+fingerprint:"")
            +"&name="+Uri.EscapeDataString(school)+"&addr="+Uri.EscapeDataString(schoolAddress)+"&service="+service;
    // The new server's link gives the address and certificate the apps should move to.
    internal static SchoolAppSettings.Moving MoveFrom(string link,long now)
    {
        if(!Uri.TryCreate(link.Trim(),UriKind.Absolute,out var uri)||uri.Scheme!="schooltalk"||uri.Host!="school")throw new InvalidOperationException("새 서버 PC의 관리자 프로그램에서 ‘연결 링크 복사’로 받은 링크를 붙여 넣으세요.");
        var query=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2)).Where(p=>p.Length==2).GroupBy(p=>p[0]).ToDictionary(g=>g.Key,g=>Uri.UnescapeDataString(g.First()[1]));
        var server=query.GetValueOrDefault("server","").TrimEnd('/')+"/";var pin=query.GetValueOrDefault("pin","").ToUpperInvariant();
        if(!SchoolAppSettings.ValidMove(server,pin))throw new InvalidOperationException("새 서버 주소는 https://로 시작해야 합니다. 새 서버의 연결 링크를 다시 복사하세요.");
        return new(server,pin,now+SchoolAppSettings.MoveNoticeMilliseconds);
    }
    void ShowSchoolLink(ServerCard c)
    {
        var data=DataPath(c).Trim();var known=data.Length>0&&Directory.Exists(data);
        var window=new Window{Owner=this,Resources=Resources,Title="서버 이전",Width=560,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        window.SetResourceReference(BackgroundProperty,"Surface");
        var stack=new StackPanel{Margin=new Thickness(24)};window.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        void Note(string text){var note=AdminUi.Label(text,"Muted",12);note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,0);stack.Children.Add(note);}
        void Section(string title){var heading=AdminUi.Heading(title);heading.Margin=new Thickness(0,stack.Children.Count==0?0:24,0,8);stack.Children.Add(heading);}

        Section("이 서버의 연결 링크");
        var server=new TextBox{Text=Simple(c)?SimpleOperation.Addresses(c.Service)[0]:""};stack.Children.Add(server);
        Note("교사 PC가 연결할 주소입니다. 서버를 이 PC로 옮겨 왔다면 연결 링크를 복사해 이전 서버에 넣으세요.");
        var copy=new Button{Content="연결 링크 복사",HorizontalAlignment=HorizontalAlignment.Left};stack.Children.Add(copy);
        copy.Click+=(_,_)=>{Clipboard.SetText(SchoolLink(server.Text.Trim(),c.Fingerprint,c.School.Text.Trim(),c.SchoolPick?.Selected?.Address??"",c.Service));copy.Content="복사했습니다";};
        Section("서버 이전");
        var moveState=AdminUi.Label("","Ink",13);moveState.TextWrapping=TextWrapping.Wrap;stack.Children.Add(moveState);
        var target=new TextBox{IsEnabled=known,Margin=new Thickness(0,8,0,0)};System.Windows.Automation.AutomationProperties.SetName(target,"새 서버의 연결 링크");stack.Children.Add(target);
        var moveRow=new WrapPanel{Margin=new Thickness(0,8,0,0)};stack.Children.Add(moveRow);
        var start=new Button{Content="이전 안내 시작 (7일)",IsEnabled=known,Margin=new Thickness(0,0,8,0)};start.SetResourceReference(StyleProperty,"DangerButton");moveRow.Children.Add(start);
        var cancel=new Button{Content="이전 취소",IsEnabled=known};moveRow.Children.Add(cancel);
        Note("1) 새 서버 PC에 백업을 복원하고 서버를 시작합니다. 2) 새 서버 PC의 이 창에서 ‘연결 링크 복사’를 누릅니다. 3) 여기에 붙여 넣고 ‘이전 안내 시작’을 누릅니다. 7일 동안 이 서버는 새 주소만 알려 주고 다른 요청은 받지 않으며, "+"교사 PC"+"가 새 주소로 바꿔 연결합니다. 이 기간에는 이 서버를 끄지 마세요.");
        var moveError=AdminUi.Label("","Danger",12);moveError.TextWrapping=TextWrapping.Wrap;stack.Children.Add(moveError);
        void ShowMove()
        {
            var move=known?SchoolAppSettings.ActiveMove(SchoolAppSettings.Read(data),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()):null;
            moveState.Text=move is null?"이전 안내 없음":"이전 안내 중 · "+move.Address+" · "+DateTimeOffset.FromUnixTimeMilliseconds(move.Until).ToLocalTime().ToString("M월 d일 HH:mm")+"까지";
            cancel.Visibility=move is null?Visibility.Collapsed:Visibility.Visible;
        }
        start.Click+=(_,_)=>
        {
            try
            {
                var move=MoveFrom(target.Text,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if(Uri.TryCreate(server.Text.Trim(),UriKind.Absolute,out var own)&&own.GetLeftPart(UriPartial.Authority)==new Uri(move.Address).GetLeftPart(UriPartial.Authority))throw new InvalidOperationException("지금 서버와 같은 주소입니다. 새 서버의 링크를 붙여 넣으세요.");
                if(Notice.Show(window,"7일 동안 이 서버는 새 주소만 알려 주고 메시지·로그인 등 다른 요청은 받지 않습니다.\n\n새 서버가 이미 실행 중인지 확인했나요?","서버 이전",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                SchoolAppSettings.Write(data,SchoolAppSettings.Read(data)with{Moving=move});moveError.Text="";target.Clear();ShowMove();
            }
            catch(Exception e)when(e is InvalidOperationException or IOException or UnauthorizedAccessException){moveError.Text=e.Message;}
        };
        cancel.Click+=(_,_)=>{SchoolAppSettings.Write(data,SchoolAppSettings.Read(data)with{Moving=null});ShowMove();};
        ShowMove();window.ShowDialog();
    }
}

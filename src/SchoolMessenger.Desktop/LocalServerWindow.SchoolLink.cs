using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
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
        var window=new Window{Owner=this,Resources=Resources,Title=AppFeatures.Ready?"학교 연결 QR · 서버 이전":"서버 이전",Width=560,Height=AppFeatures.Ready?760:600,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
        window.SetResourceReference(BackgroundProperty,"Surface");
        var stack=new StackPanel{Margin=new Thickness(24)};window.Content=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        void Note(string text){var note=AdminUi.Label(text,"Muted",12);note.TextWrapping=TextWrapping.Wrap;note.Margin=new Thickness(0,6,0,0);stack.Children.Add(note);}
        void Section(string title){var heading=AdminUi.Heading(title);heading.Margin=new Thickness(0,stack.Children.Count==0?0:24,0,8);stack.Children.Add(heading);}

        Section(AppFeatures.Ready?"학교 연결 QR":"이 서버의 연결 링크");
        var server=new TextBox{Text=Simple(c)?SimpleOperation.Addresses(c.Service)[0]:""};System.Windows.Automation.AutomationProperties.SetName(server,AppFeatures.Ready?"앱이 연결할 서버 주소":"이 서버의 주소");stack.Children.Add(server);
        Note(!AppFeatures.Ready?"교사 PC가 연결할 주소입니다. 서버를 이 PC로 옮겨 왔다면 ‘연결 링크 복사’를 눌러 이전 서버의 ‘서버 이전’ 칸에 붙여 넣으세요."
            :c.Service=="office"?"교사 PC·교사 앱이 연결할 주소입니다. 학교망 밖에서 쓰는 주소가 있으면 그 주소로 바꾸세요."
            :"학생·학부모 앱이 학교 밖에서 연결할 주소(https://…)를 넣으세요. 외부 접속 설정이 끝난 주소여야 합니다.");
        var image=new Image{Width=220,Height=220,Margin=new Thickness(0,12,0,0),HorizontalAlignment=HorizontalAlignment.Left,Visibility=AppFeatures.Ready?Visibility.Visible:Visibility.Collapsed};System.Windows.Automation.AutomationProperties.SetName(image,"학교 연결 QR 코드");stack.Children.Add(image);
        string Link()=>SchoolLink(server.Text.Trim(),c.Fingerprint,c.School.Text.Trim(),c.SchoolPick?.Selected?.Address??"",c.Service);
        byte[] Png()=>new PngByteQRCode(QRCodeGenerator.GenerateQrCode(Link(),QRCodeGenerator.ECCLevel.M)).GetGraphic(10);
        void Draw()
        {
            if(!Uri.TryCreate(server.Text.Trim(),UriKind.Absolute,out var uri)||uri.Scheme is not("https"or"http")){image.Source=null;return;}
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=new MemoryStream(Png());bitmap.EndInit();image.Source=bitmap;
        }
        server.TextChanged+=(_,_)=>Draw();Draw();
        var actions=new WrapPanel{Margin=new Thickness(0,12,0,0)};stack.Children.Add(actions);
        var save=new Button{Content="QR 이미지 저장",Margin=new Thickness(0,0,8,0),Visibility=AppFeatures.Ready?Visibility.Visible:Visibility.Collapsed};save.SetResourceReference(StyleProperty,"PrimaryButton");actions.Children.Add(save);
        save.Click+=(_,_)=>{if(image.Source is null)return;var picker=new SaveFileDialog{Filter="PNG 이미지|*.png",FileName="학교연결QR-"+(c.Service=="office"?"교직원":"학생학부모")+".png"};if(picker.ShowDialog(window)==true)File.WriteAllBytes(picker.FileName,Png());};
        var copy=new Button{Content="연결 링크 복사"};actions.Children.Add(copy);copy.Click+=(_,_)=>{Clipboard.SetText(Link());copy.Content="복사했습니다";};
        Note(AppFeatures.Ready?"가정통신문·교실 게시물에 붙여 두면 앱에서 ‘학교 QR 찍기’로 바로 연결됩니다. 이 서버의 연결 확인 코드도 QR에 들어 있어 코드를 따로 비교하지 않아도 됩니다.":"학교 연결 QR과 휴대폰 소식 확인 시각은 앱과 연동되는 기능이라 개발 진행 중입니다.");

        if(AppFeatures.Ready){
        var settings=known?SchoolAppSettings.Read(data):new();
        Section("휴대폰 소식 확인 시각");
        var time=new TextBox{Text=SchoolAppSettings.CheckTime(settings),Width=90,HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=known};System.Windows.Automation.AutomationProperties.SetName(time,"휴대폰 소식 확인 시각");
        var timeRow=new StackPanel{Orientation=Orientation.Horizontal};timeRow.Children.Add(time);var saveTime=new Button{Content="저장",Margin=new Thickness(8,0,0,0),IsEnabled=known};timeRow.Children.Add(saveTime);stack.Children.Add(timeRow);
        var timeNote=AdminUi.Label("","Muted",12);
        saveTime.Click+=(_,_)=>
        {
            if(!SchoolAppSettings.ValidTime(time.Text.Trim())){timeNote.Text="시각은 18:00처럼 24시간제로 입력하세요.";return;}
            settings=SchoolAppSettings.Read(data)with{CheckTime=time.Text.Trim()};SchoolAppSettings.Write(data,settings);timeNote.Text="저장했습니다. 앱은 다음 접속 때부터 이 시각을 씁니다.";
        };
        Note(known?"안드로이드 앱이 하루 한 번 이 시각부터 30분 사이에 새 소식과 서버 이전 안내를 확인해 알림을 띄웁니다. 앱을 열 때도 바로 확인합니다.":"서버 데이터 위치를 찾지 못해 바꿀 수 없습니다. 서버를 한 번 시작한 뒤 다시 여세요.");
        timeNote.Margin=new Thickness(0,4,0,0);stack.Children.Add(timeNote);
        }

        Section("서버 이전");
        var moveState=AdminUi.Label("","Ink",13);moveState.TextWrapping=TextWrapping.Wrap;stack.Children.Add(moveState);
        var target=new TextBox{IsEnabled=known,Margin=new Thickness(0,8,0,0)};System.Windows.Automation.AutomationProperties.SetName(target,"새 서버의 연결 링크");stack.Children.Add(target);
        var moveRow=new WrapPanel{Margin=new Thickness(0,8,0,0)};stack.Children.Add(moveRow);
        var start=new Button{Content="이전 안내 시작 (7일)",IsEnabled=known,Margin=new Thickness(0,0,8,0)};start.SetResourceReference(StyleProperty,"DangerButton");moveRow.Children.Add(start);
        var cancel=new Button{Content="이전 취소",IsEnabled=known};moveRow.Children.Add(cancel);
        Note("1) 새 서버 PC에 백업을 복원하고 서버를 시작합니다. 2) 새 서버 PC의 이 창에서 ‘연결 링크 복사’를 누릅니다. 3) 여기에 붙여 넣고 ‘이전 안내 시작’을 누릅니다. 7일 동안 이 서버는 새 주소만 알려 주고 다른 요청은 받지 않으며, "+(AppFeatures.Ready?"앱과 교사 PC":"교사 PC")+"가 새 주소로 바꿔 연결합니다. 이 기간에는 이 서버를 끄지 마세요.");
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
                if(MessageBox.Show(window,"7일 동안 이 서버는 새 주소만 알려 주고 메시지·로그인 등 다른 요청은 받지 않습니다.\n\n새 서버가 이미 실행 중인지 확인했나요?","서버 이전",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                SchoolAppSettings.Write(data,SchoolAppSettings.Read(data)with{Moving=move});moveError.Text="";target.Clear();ShowMove();
            }
            catch(Exception e)when(e is InvalidOperationException or IOException or UnauthorizedAccessException){moveError.Text=e.Message;}
        };
        cancel.Click+=(_,_)=>{SchoolAppSettings.Write(data,SchoolAppSettings.Read(data)with{Moving=null});ShowMove();};
        ShowMove();window.ShowDialog();
    }
}

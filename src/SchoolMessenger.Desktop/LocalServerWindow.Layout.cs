using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    static Border Surface(UIElement child,Thickness? padding=null)
    {
        var border=new Border{Child=child,Padding=padding??new Thickness(22),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1)};
        border.SetResourceReference(Border.BorderBrushProperty,"Line");
        border.SetResourceReference(Border.BackgroundProperty,"Surface");return border;
    }
    static TextBlock Note(string value)
    {
        var note=Text(value,12);note.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return note;
    }
    void BuildDashboard()
    {
        Background=(Brush)FindResource("Soft");
        var layout=new Grid();layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(216)});layout.ColumnDefinitions.Add(new ColumnDefinition());Content=layout;
        var side=new DockPanel{Margin=new Thickness(12,20,12,14)};
        var heading=new DockPanel{Margin=new Thickness(8,0,0,16)};
        heading.Children.Add(new Image{Source=(System.Windows.Media.ImageSource)FindResource("ProductMark"),Width=36,Height=36,Margin=new Thickness(0,0,10,0)});
        var brand=new StackPanel();var title=Text("교무톡",16);title.FontWeight=FontWeights.Bold;title.Margin=new Thickness(0);title.SetResourceReference(TextBlock.ForegroundProperty,"OnAccent");brand.Children.Add(title);var subtitle=Note("관리자");subtitle.SetResourceReference(TextBlock.ForegroundProperty,"Pale");subtitle.Margin=new Thickness(0,3,0,0);brand.Children.Add(subtitle);heading.Children.Add(brand);DockPanel.SetDock(heading,Dock.Top);side.Children.Add(heading);
        var bottom=new StackPanel{Margin=new Thickness(6,8,6,0)};autoProgram.Content=new TextBlock{Text="Windows 로그인 시\n관리자 자동 실행",TextWrapping=TextWrapping.Wrap};autoProgram.SetResourceReference(Control.ForegroundProperty,"OnAccent");autoProgram.VerticalContentAlignment=VerticalAlignment.Top;bottom.Children.Add(autoProgram);
        var update=new Button{Content="프로그램 업데이트",MinHeight=34,Margin=new Thickness(0,0,0,6)};update.Click+=(_,_)=>DesktopUpdater.Settings(this);bottom.Children.Add(update);
        var guide=new Button{Content="운영 안내 열기",MinHeight=34};guide.Click+=(_,_)=>OpenGuide();bottom.Children.Add(guide);DockPanel.SetDock(bottom,Dock.Bottom);side.Children.Add(bottom);
        var navigation=new StackPanel();side.Children.Add(new ScrollViewer{Content=navigation,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});var rail=new Border{Child=side};rail.SetResourceReference(Border.BackgroundProperty,"Navy");layout.Children.Add(rail);
        var main=new DockPanel{Margin=new Thickness(24,18,24,14)};Grid.SetColumn(main,1);layout.Children.Add(main);
        var footer=Note("창을 닫아도 서버는 알림 영역에서 계속 실행됩니다. 관리 계정은 로그아웃됩니다.");footer.Margin=new Thickness(0,16,0,0);DockPanel.SetDock(footer,Dock.Bottom);main.Children.Add(footer);
        var identityBar=new DockPanel{Margin=new Thickness(0,0,0,16),MinHeight=34};adminIdentity.VerticalAlignment=VerticalAlignment.Center;signOut.Margin=new Thickness(16,0,0,0);signOut.FontSize=11;signOut.Padding=new Thickness(12,7,12,7);DockPanel.SetDock(signOut,Dock.Right);identityBar.Children.Add(signOut);identityBar.Children.Add(adminIdentity);signOut.Click+=async(_,_)=>await SignOutAdministrator(activeService);DockPanel.SetDock(identityBar,Dock.Top);main.Children.Add(identityBar);
        var views=new Grid();views.Children.Add(serverViews);views.Children.Add(administrationView);main.Children.Add(views);
        void Group(string label){var text=Note(label);text.FontSize=10;text.Margin=new Thickness(10,12,0,7);text.SetResourceReference(TextBlock.ForegroundProperty,"Pale");text.Opacity=.7;navigation.Children.Add(text);}
        Group("서버 운영");
        foreach(var service in new[]{"office","announcements"})AddCard(navigation,service);
        void Menu(string label,string page,string icon,string? role=null){var button=new Button{Content=NavigationLabel(label,icon),Tag=page,MinHeight=34,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,0,2),HorizontalContentAlignment=HorizontalAlignment.Stretch,BorderThickness=new Thickness(0)};NavigationState(button,false);System.Windows.Automation.AutomationProperties.SetName(button,label);button.Click+=async(_,_)=>await OpenAdministration(page,role);navigation.Children.Add(button);administrationNavigation.Add(button);}
        Menu("운영 현황","resources","SettingsIcon");Group("구성원");Menu("교직원·권한","members","UserIcon","office");Menu("학급·분반","sections","TreeIcon","office");Menu("학생·학부모","portal","UserIcon","announcements");Group("학교 설정");Menu("특별실","rooms","FileIcon","office");Menu("학사일정","calendar","HistoryIcon","office");Menu("약관·개인정보","policy","SurveyIcon");Group("기록");Menu("오류·의견","feedback","ChatIcon");Menu("운영 변경 기록","audit","HistoryIcon","office");
        SelectCard(cards.FirstOrDefault(c=>c.Host.Installed)??cards[0]);
    }
    Grid NavigationLabel(string label,string icon)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(26)});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var glyph=new System.Windows.Shapes.Path{Data=(Geometry)FindResource(icon),Style=(Style)FindResource("Glyph"),Width=16,Height=16,HorizontalAlignment=HorizontalAlignment.Left};row.Children.Add(glyph);
        var text=new TextBlock{Text=label,FontSize=12,FontWeight=FontWeights.Medium,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(text,1);row.Children.Add(text);return row;
    }
    static void NavigationState(Button button,bool selected)
    {
        button.SetResourceReference(Control.BackgroundProperty,selected?"Pale":"Navy");button.SetResourceReference(Control.ForegroundProperty,selected?"Blue":"OnAccent");button.BorderThickness=new Thickness(0);
    }
    void SelectCard(ServerCard card)
    {
        CloseAdministrationPage();activeService=card.Service;activePage="server";administrationView.Visibility=Visibility.Collapsed;serverViews.Visibility=Visibility.Visible;UpdateAdminIdentity();
        foreach(var button in administrationNavigation)NavigationState(button,false);
        foreach(var c in cards)
        {
            c.View.Visibility=c==card?Visibility.Visible:Visibility.Collapsed;
            NavigationState(c.Navigation,c==card);
            System.Windows.Automation.AutomationProperties.SetHelpText(c.Navigation,c==card?"선택된 서버":"이 서버의 실행 설정 보기");
        }
    }
    void AddCard(StackPanel navigation,string service)
    {
        var card=new ServerCard(service,new LocalServerProcess(root,service));cards.Add(card);
        var label=service=="office"?"교내 업무 서버":"학교 소식 서버";var navContent=NavigationLabel(label,"SettingsIcon");card.NavigationStatus.FontSize=9;card.NavigationStatus.Margin=new Thickness(4,0,0,0);card.NavigationStatus.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(card.NavigationStatus,2);navContent.Children.Add(card.NavigationStatus);
        card.Navigation.Content=navContent;card.Navigation.MinHeight=34;card.Navigation.Padding=new Thickness(10,7,10,7);card.Navigation.Margin=new Thickness(0,0,0,2);card.Navigation.Click+=(_,_)=>SelectCard(card);navigation.Children.Add(card.Navigation);
        System.Windows.Automation.AutomationProperties.SetName(card.Navigation,label+" 서버 선택");
        card.View.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});card.View.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});card.View.RowDefinitions.Add(new RowDefinition());serverViews.Children.Add(card.View);
        var heading=new StackPanel{Margin=new Thickness(0,0,0,22)};var title=Text(service=="office"?"교내 업무 서버":"학생·학부모 소식 서버",22);title.FontWeight=FontWeights.Bold;heading.Children.Add(title);heading.Children.Add(Note(service=="office"?"교직원 메신저와 교사용 앱의 업무를 연결합니다.":"학생·학부모 앱으로 학교의 공지와 소식을 전달합니다."));card.View.Children.Add(heading);
        var status=new StackPanel();card.Status.FontWeight=FontWeights.Bold;status.Children.Add(card.Status);card.Address.Margin=new Thickness(0,8,0,14);card.Address.SetResourceReference(TextBlock.ForegroundProperty,"Muted");status.Children.Add(card.Address);
        var actions=new WrapPanel();card.Start.SetResourceReference(StyleProperty,"PrimaryButton");card.Admin.Content="관리자 로그인";
        foreach(var button in new[]{card.Start,card.Stop,card.Admin}){button.MinWidth=110;button.MinHeight=38;button.Margin=new Thickness(0,0,10,0);actions.Children.Add(button);}status.Children.Add(actions);
        var statusCard=Surface(status);statusCard.Margin=new Thickness(0,0,0,16);Grid.SetRow(statusCard,1);card.View.Children.Add(statusCard);
        var controls=card.Controls;var settingsTitle=Text("실행 설정",16);settingsTitle.FontWeight=FontWeights.Bold;controls.Children.Add(settingsTitle);
        var mode=new Grid{Margin=new Thickness(0,12,0,0)};mode.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(100)});mode.ColumnDefinitions.Add(new ColumnDefinition());mode.Children.Add(Text("실행 모드"));Grid.SetColumn(card.Mode,1);mode.Children.Add(card.Mode);controls.Children.Add(mode);
        System.Windows.Automation.AutomationProperties.SetName(card.Mode,"서버 실행 모드");
        card.Data.Text=service=="office"&&Directory.Exists(@"C:\SchoolMessengerTrial\office")?@"C:\SchoolMessengerTrial\office":Path.Combine(directory,"trial",service);
        card.TrialFields.Children.Add(Note("가상 자료로 시험합니다. 외부 연결·파일 악성코드 검사는 사용하지 않습니다."));
        var passwords=new Grid{Margin=new Thickness(0,14,0,0)};passwords.ColumnDefinitions.Add(new ColumnDefinition());passwords.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});passwords.ColumnDefinitions.Add(new ColumnDefinition());
        var first=new StackPanel();first.Children.Add(Text("최초 관리자 비밀번호"));card.Password.MinHeight=38;first.Children.Add(card.Password);passwords.Children.Add(first);
        var second=new StackPanel();second.Children.Add(Text("비밀번호 확인"));card.Confirm.MinHeight=38;second.Children.Add(card.Confirm);Grid.SetColumn(second,2);passwords.Children.Add(second);
        System.Windows.Automation.AutomationProperties.SetName(card.Password,"최초 관리자 비밀번호");System.Windows.Automation.AutomationProperties.SetName(card.Confirm,"비밀번호 확인");
        card.PasswordFields.Children.Add(passwords);card.PasswordFields.Children.Add(Note("12~128자 · 서버를 처음 만들 때만 입력합니다."));card.TrialFields.Children.Add(card.PasswordFields);controls.Children.Add(card.TrialFields);
        card.ProductionFields.Margin=new Thickness(0,12,0,0);card.ProductionFields.Children.Add(Note("계정·HTTPS 인증서·백업을 준비한 운영 설정을 선택하세요. 도입·운영 내부결재 승인 문서는 학교에 보관하고, 적용되는 학운위 심의·보안 검토를 확인하세요. 승인 문서는 공개 설치본에 넣지 않습니다."));card.ProductionFields.Children.Add(card.Config);
        var select=new Button{Content="운영 설정 파일 선택",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,0)};select.Click+=(_,_)=>{var dialog=new OpenFileDialog{Filter="운영 설정 (*.json)|*.json",CheckFileExists=true};if(dialog.ShowDialog(this)==true){card.Config.Text=dialog.FileName;SaveSettings();Refresh();}};card.ProductionFields.Children.Add(select);controls.Children.Add(card.ProductionFields);
        card.Auto.Content=new TextBlock{Text="관리 프로그램을 열면 이 서버도 자동 시작",TextWrapping=TextWrapping.Wrap};card.Auto.Margin=new Thickness(0,18,0,12);card.Auto.ToolTip="시험 서버의 최초 설정을 완료한 후 선택할 수 있습니다.";controls.Children.Add(card.Auto);card.Auto.Click+=(_,_)=>SaveSettings();
        var path=new StackPanel();path.Children.Add(card.Data);path.Children.Add(Note("시험 데이터의 저장 위치입니다. 운영 저장 위치는 운영 설정에서 지정합니다."));var advanced=new Expander{Header="데이터 저장 위치",Content=path,Margin=new Thickness(0,6,0,8)};controls.Children.Add(advanced);
        card.Mode.SelectionChanged+=(_,_)=>{Refresh();SaveSettings();};card.Data.TextChanged+=(_,_)=>Refresh();
        card.Details.Content=card.Log;card.Log.FontFamily=new System.Windows.Media.FontFamily("Consolas");controls.Children.Add(card.Details);
        var settings=Surface(new ScrollViewer{Content=controls,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Grid.SetRow(settings,2);card.View.Children.Add(settings);
        card.Start.Click+=async(_,_)=>await Start(card);card.Stop.Click+=async(_,_)=>await Stop(card);card.Admin.Click+=(_,_)=>OpenAdmin(card);
        // Production paths are governed by the selected operation configuration.
        card.Mode.SelectionChanged+=(_,_)=>advanced.Visibility=card.Mode.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
    }
    void OpenGuide()
    {
        try
        {
            var path=File.Exists(Path.Combine(root,"서버 설치 안내.txt"))?Path.Combine(root,"서버 설치 안내.txt"):Path.Combine(root,"docs","server-installer.md");
            var open=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","notepad.exe")){UseShellExecute=false};open.ArgumentList.Add(path);Process.Start(open);
        }catch(Exception e){MessageBox.Show(this,e.Message,"운영 안내");}
    }
}

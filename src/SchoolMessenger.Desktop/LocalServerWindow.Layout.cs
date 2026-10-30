using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse=System.Windows.Shapes.Ellipse;
using Shape=System.Windows.Shapes.Shape;
using Glyph=System.Windows.Shapes.Path;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    readonly TextBlock railSchool=AdminUi.Label("","Ink",14,true),railStatus=AdminUi.Label("");
    readonly Ellipse railDot=new(){Width=7,Height=7,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};
    static Border Surface(UIElement child,Thickness? padding=null)=>AdminUi.Card(child,padding?.Left??22);
    static TextBlock Note(string value)=>AdminUi.Label(value);
    void ApplyAdminTheme()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AdminTheme.xaml",UriKind.Relative)});
        if(!SystemParameters.HighContrast)return;
        // High contrast keeps the layout but takes every color from Windows.
        foreach(var key in new[]{"Surface","Subtle","Rail","Soft","Canvas","Glass","Hover","Pale","SuccessTint","WarningTint","DangerTint","NewsTint"})Resources[key]=SystemColors.WindowBrush;
        foreach(var key in new[]{"Ink","Muted","Faint","Navy","Success","Warning","Danger","News","Line","LineStrong","GlassLine","DangerLine"})Resources[key]=SystemColors.WindowTextBrush;
        Resources["Blue"]=SystemColors.HighlightBrush;Resources["OnAccent"]=SystemColors.HighlightTextBrush;
    }
    void BuildDashboard()
    {
        ApplyAdminTheme();Background=(Brush)FindResource("Soft");
        var layout=new Grid();layout.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(232)});layout.ColumnDefinitions.Add(new ColumnDefinition());Content=layout;
        var side=new DockPanel{Margin=new Thickness(12,16,12,12)};
        var heading=new DockPanel{Margin=new Thickness(8,0,0,12)};
        heading.Children.Add(new Image{Source=(ImageSource)FindResource("ProductMark"),Width=32,Height=32,Margin=new Thickness(0,0,10,0)});
        var brand=new StackPanel{VerticalAlignment=VerticalAlignment.Center};brand.Children.Add(AdminUi.Heading("교무톡"));brand.Children.Add(AdminUi.Label("관리자","Faint"));heading.Children.Add(brand);DockPanel.SetDock(heading,Dock.Top);side.Children.Add(heading);
        var school=new StackPanel();school.Children.Add(railSchool);var state=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};state.Children.Add(railDot);state.Children.Add(railStatus);school.Children.Add(state);
        var schoolCard=AdminUi.Card(school,0);schoolCard.Padding=new Thickness(12,10,12,10);schoolCard.CornerRadius=new CornerRadius(8);schoolCard.Margin=new Thickness(0,0,0,4);DockPanel.SetDock(schoolCard,Dock.Top);side.Children.Add(schoolCard);
        var bottom=new StackPanel{Margin=new Thickness(0,10,0,0)};
        var me=new Grid();me.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});me.ColumnDefinitions.Add(new ColumnDefinition());me.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        me.Children.Add(adminAvatar);var who=new StackPanel{Margin=new Thickness(10,0,6,0),VerticalAlignment=VerticalAlignment.Center};adminIdentity.FontWeight=FontWeights.SemiBold;adminIdentity.SetResourceReference(TextBlock.ForegroundProperty,"Ink");adminIdentity.TextTrimming=TextTrimming.CharacterEllipsis;adminIdentity.TextWrapping=TextWrapping.NoWrap;who.Children.Add(adminIdentity);who.Children.Add(adminRole);Grid.SetColumn(who,1);me.Children.Add(who);
        signOut.Content=AdminUi.Icon("AdminLogoutIcon");signOut.SetResourceReference(StyleProperty,"SubtleButton");signOut.SetResourceReference(Control.ForegroundProperty,"Muted");signOut.ToolTip="이 서버 로그아웃";System.Windows.Automation.AutomationProperties.SetName(signOut,"이 서버 로그아웃");signOut.Click+=async(_,_)=>await SignOutAdministrator(activeService);Grid.SetColumn(signOut,2);me.Children.Add(signOut);
        var meCard=AdminUi.Card(me,10);meCard.CornerRadius=new CornerRadius(8);bottom.Children.Add(meCard);
        var links=new WrapPanel{Margin=new Thickness(0,6,0,0)};
        void Link(string label,Action action){var link=new Button{Content=label,FontWeight=FontWeights.Normal};link.SetResourceReference(StyleProperty,"SubtleButton");link.SetResourceReference(Control.ForegroundProperty,"Muted");link.Click+=(_,_)=>action();links.Children.Add(link);}
        Link("업데이트 확인",()=>DesktopUpdater.Settings(this));Link("운영 안내",OpenGuide);bottom.Children.Add(links);
        autoProgram.Content=AdminUi.Label("Windows 로그인 시 자동 실행","Muted",12);autoProgram.Margin=new Thickness(8,2,0,0);bottom.Children.Add(autoProgram);
        DockPanel.SetDock(bottom,Dock.Bottom);side.Children.Add(bottom);
        var navigation=new StackPanel();side.Children.Add(new ScrollViewer{Content=navigation,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var rail=new Border{Child=side,BorderThickness=new Thickness(0,0,1,0)};rail.SetResourceReference(Border.BackgroundProperty,"Rail");rail.SetResourceReference(Border.BorderBrushProperty,"Line");layout.Children.Add(rail);
        var main=new DockPanel{Margin=new Thickness(32,26,32,14)};Grid.SetColumn(main,1);layout.Children.Add(main);
        var footer=AdminUi.Label("창을 닫아도 서버는 알림 영역에서 계속 실행됩니다. 관리 계정은 로그아웃됩니다.","Faint",12);footer.Margin=new Thickness(0,12,0,0);DockPanel.SetDock(footer,Dock.Bottom);main.Children.Add(footer);
        var views=new Grid();views.Children.Add(serverViews);views.Children.Add(administrationView);main.Children.Add(views);
        void Group(string label){var text=AdminUi.Label(label,"Faint",12,true);text.Margin=new Thickness(10,10,0,3);navigation.Children.Add(text);}
        Group("서버");
        foreach(var service in new[]{"office","announcements"})AddCard(navigation,service);
        void Menu(string label,string page,string icon,string? role=null){var button=new Button{Content=NavigationLabel(label,icon),Uid=page,Margin=new Thickness(0,0,0,1)};NavigationState(button,false);System.Windows.Automation.AutomationProperties.SetName(button,label);button.Click+=async(_,_)=>await OpenAdministration(page,role);navigation.Children.Add(button);administrationNavigation.Add(button);}
        Group("운영");Menu("운영 현황","resources","AdminOverviewIcon");
        Group("교직원");Menu("교직원·권한","members","AdminPeopleIcon","office");
        // Sections hold student accounts, so they sit with the student-parent service they depend on.
        Group("학생·학부모");Menu("가입·학급 명부","portal","AdminStudentIcon","announcements");Menu("선택과목 분반","sections","AdminClassIcon","office");
        Group("학교 설정");Menu("특별실","rooms","AdminRoomIcon","office");Menu("학사일정","calendar","AdminCalendarIcon","office");Menu("약관·개인정보","policy","AdminDocIcon");
        Group("기록");Menu("오류·의견","feedback","AdminChatIcon");Menu("운영 변경 기록","audit","AdminHistoryIcon","office");
        SelectCard(cards.FirstOrDefault(c=>c.Host.Installed)??cards[0]);
    }
    Grid NavigationLabel(string label,string icon)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(26)});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var glyph=AdminUi.Icon(icon);glyph.HorizontalAlignment=HorizontalAlignment.Left;row.Children.Add(glyph);
        var text=new TextBlock{Text=label,FontSize=13,FontWeight=FontWeights.Medium,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(text,1);row.Children.Add(text);return row;
    }
    static void NavigationState(Button button,bool selected)
    {
        // NavButton draws the tint and left bar for Tag "selected"; icons sit one step quieter than the label.
        button.SetResourceReference(StyleProperty,"NavButton");button.Tag=selected?"selected":null;
        foreach(var glyph in ((Grid)button.Content).Children.OfType<Glyph>())glyph.SetResourceReference(Shape.StrokeProperty,selected?"Blue":"Muted");
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
        var card=new ServerCard(service,new LocalServerProcess(root,service));cards.Add(card);var office=service=="office";
        var label=office?"교내 업무 서버":"학생·학부모 소식 서버";var navContent=NavigationLabel(office?"교내 업무":"학생·학부모 소식",office?"AdminServerIcon":"AdminStudentIcon");
        card.NavigationStatus.FontSize=12;card.NavigationStatus.FontWeight=FontWeights.Medium;card.NavigationStatus.Margin=new Thickness(4,0,0,0);card.NavigationStatus.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(card.NavigationStatus,2);navContent.Children.Add(card.NavigationStatus);
        card.Navigation.Content=navContent;card.Navigation.Margin=new Thickness(0,0,0,1);card.Navigation.Click+=(_,_)=>SelectCard(card);navigation.Children.Add(card.Navigation);
        System.Windows.Automation.AutomationProperties.SetName(card.Navigation,label+" 서버 선택");
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star)})card.View.RowDefinitions.Add(new RowDefinition{Height=height});serverViews.Children.Add(card.View);
        // Header: title on the left, start/stop/sign-in on the right.
        var header=new DockPanel{Margin=new Thickness(0,0,0,16)};
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom};card.Start.SetResourceReference(StyleProperty,"PrimaryButton");card.Admin.Content="관리자 로그인";
        foreach(var button in new[]{card.Start,card.Stop,card.Admin}){button.Margin=new Thickness(8,0,0,0);actions.Children.Add(button);}DockPanel.SetDock(actions,Dock.Right);header.Children.Add(actions);
        var heading=new StackPanel();heading.Children.Add(AdminUi.Heading(label,22));var sub=AdminUi.Label(office?"교직원 메신저와 교사용 앱의 업무를 연결합니다.":"학생·학부모 앱으로 학교 공지와 가정통신문을 전달합니다.","Muted",14);sub.Margin=new Thickness(0,4,0,0);heading.Children.Add(sub);header.Children.Add(heading);card.View.Children.Add(header);
        // Status card: badge, mode and the connection details teachers need.
        var status=new StackPanel();var line=new StackPanel{Orientation=Orientation.Horizontal};
        card.Status.FontSize=12;card.Status.FontWeight=FontWeights.SemiBold;card.Status.Margin=new Thickness(0);card.Status.VerticalAlignment=VerticalAlignment.Center;
        var badgeRow=new StackPanel{Orientation=Orientation.Horizontal};badgeRow.Children.Add(new Ellipse{Width=6,Height=6,Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center});badgeRow.Children.Add(card.Status);
        card.StatusBadge.Child=badgeRow;card.StatusBadge.Padding=new Thickness(8,3,8,3);card.StatusBadge.CornerRadius=new CornerRadius(11);card.StatusBadge.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(card.StatusBadge);
        card.Summary.Margin=new Thickness(10,0,0,0);card.Summary.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(card.Summary);status.Children.Add(line);
        card.Address.Margin=new Thickness(0,12,0,0);card.Address.SetResourceReference(TextBlock.ForegroundProperty,"Muted");status.Children.Add(card.Address);
        foreach(var _ in Enumerable.Range(0,3))card.ConnectionPanel.ColumnDefinitions.Add(new ColumnDefinition());card.ConnectionPanel.Margin=new Thickness(0,16,0,0);
        StackPanel Column(int index,string title,TextBlock value,string note,Func<string>? copy)
        {
            var column=new StackPanel();var holder=new Border{Child=column,Padding=new Thickness(index==0?0:20,0,12,0),BorderThickness=new Thickness(index==0?0:1,0,0,0)};holder.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetColumn(holder,index);card.ConnectionPanel.Children.Add(holder);
            column.Children.Add(AdminUi.Label(title,"Faint",12,true));
            var row=new DockPanel{Margin=new Thickness(0,6,0,6)};if(copy is not null){var button=AdminUi.CopyButton(copy,title);DockPanel.SetDock(button,Dock.Right);row.Children.Add(button);}
            value.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(value);column.Children.Add(row);
            if(note.Length>0)column.Children.Add(AdminUi.Label(note,"Muted",12));return column;
        }
        foreach(var code in new[]{card.AddressValue,card.Connection}){code.FontFamily=new FontFamily("Cascadia Mono, Consolas");code.FontSize=14;code.FontWeight=FontWeights.SemiBold;code.Margin=new Thickness(0);code.TextTrimming=TextTrimming.CharacterEllipsis;code.TextWrapping=TextWrapping.NoWrap;code.SetResourceReference(TextBlock.ForegroundProperty,"Ink");}
        Column(0,"교사 PC 접속 주소",card.AddressValue,"교사 PC 메신저가 같은 학교망에서 자동으로 찾습니다.",()=>card.AddressValue.Text);
        Column(1,"연결 확인 코드",card.Connection,"교사가 처음 접속할 때 화면의 코드와 같은지 확인합니다.",()=>card.Connection.Text);
        Column(2,"학교",card.SchoolValue,"",null).Children.Add(card.SchoolNote);
        status.Children.Add(card.ConnectionPanel);
        card.StatusCard.Child=status;card.StatusCard.Margin=new Thickness(0,0,0,16);Grid.SetRow(card.StatusCard,1);card.View.Children.Add(card.StatusCard);
        BuildEmptyState(card);
        // Settings form (and the simple-setup checklist beside it).
        var controls=card.Controls;controls.Children.Add(AdminUi.Heading("실행 설정"));
        card.Mode.Items.Add("이 PC 시험 · 외부 접속 없음");card.Mode.Items.Add("학교 운영 · 운영 설정 파일 (고급)");
        // Simple setup derives HTTPS and the LAN listener for the office server only; the news server needs an external route.
        card.Mode.Items.Add(new ComboBoxItem{Content="학교 운영 · 간편 설정 (추천)",IsEnabled=office});
        card.Mode.SelectedIndex=office?2:0;
        card.SimpleData.Text=SimpleOperation.DefaultData(directory,service);card.Activated=office||!new LocalServerProcess(root,"office").Installed||Directory.Exists(Path.Combine(directory,"trial",service));
        var mode=new Grid{Margin=new Thickness(0,12,0,4)};mode.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(84)});mode.ColumnDefinitions.Add(new ColumnDefinition());var modeLabel=AdminUi.Label("실행 모드");modeLabel.VerticalAlignment=VerticalAlignment.Center;mode.Children.Add(modeLabel);Grid.SetColumn(card.Mode,1);mode.Children.Add(card.Mode);controls.Children.Add(mode);
        System.Windows.Automation.AutomationProperties.SetName(card.Mode,"서버 실행 모드");
        card.Data.Text=office&&Directory.Exists(@"C:\SchoolMessengerTrial\office")?@"C:\SchoolMessengerTrial\office":Path.Combine(directory,"trial",service);
        card.TrialFields.Children.Add(Note("가상 자료로 시험합니다. 외부 연결·파일 악성코드 검사는 사용하지 않습니다."));
        var passwords=new Grid{Margin=new Thickness(0,10,0,0)};passwords.ColumnDefinitions.Add(new ColumnDefinition());passwords.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});passwords.ColumnDefinitions.Add(new ColumnDefinition());
        var first=new StackPanel();first.Children.Add(FieldLabel("최초 관리자 비밀번호"));first.Children.Add(card.Password);passwords.Children.Add(first);
        var second=new StackPanel();second.Children.Add(FieldLabel("비밀번호 확인"));second.Children.Add(card.Confirm);Grid.SetColumn(second,2);passwords.Children.Add(second);
        System.Windows.Automation.AutomationProperties.SetName(card.Password,"최초 관리자 비밀번호");System.Windows.Automation.AutomationProperties.SetName(card.Confirm,"비밀번호 확인");
        card.PasswordFields.Children.Add(passwords);var passwordNote=Note("12~128자 · 서버를 처음 만들 때만 입력합니다.");passwordNote.Margin=new Thickness(0,6,0,0);card.PasswordFields.Children.Add(passwordNote);controls.Children.Add(card.TrialFields);
        Field(card.SimpleFields,"학교 이름",card.School,"교사 메신저의 교내 자동검색 결과와 화면 제목에 표시됩니다.");
        Field(card.SimpleFields,"관리자 아이디",card.AdminUsername,"영문·숫자·._- 3~32자 · 서버를 처음 만들 때 정하고 이후에는 바꿀 수 없습니다.");
        var approval=new DockPanel();var choose=new Button{Content="문서 선택",Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(choose,Dock.Right);approval.Children.Add(choose);approval.Children.Add(card.Approval);choose.Click+=(_,_)=>ChooseApproval(card);
        card.SimpleFields.Children.Add(FieldLabel("학교장 승인(내부결재) 문서"));card.SimpleFields.Children.Add(approval);var approvalNote=Note("결재 문서 파일은 이 PC에만 보관하고 지문(해시)으로 학교 검토 기록을 만듭니다. 1년마다 새 승인 문서로 갱신합니다.");approvalNote.Margin=new Thickness(0,6,0,0);approvalNote.FontSize=12;card.SimpleFields.Children.Add(approvalNote);
        var detail=new StackPanel();Field(detail,"서버 데이터 위치",card.SimpleData,"계정·메시지·첨부가 저장됩니다.");Field(detail,"백업 위치",card.Backup,"비워 두면 다른 드라이브를 자동으로 고릅니다. 외장 드라이브를 권장합니다.");
        detail.Children.Add(card.Ups);card.Ups.Margin=new Thickness(0,12,0,0);card.Ups.Click+=(_,_)=>{SaveSettings();Refresh();};
        card.SimpleFields.Children.Add(new Expander{Header="자세한 설정",Content=detail,Margin=new Thickness(0,12,0,4)});
        controls.Children.Add(card.SimpleFields);controls.Children.Add(card.PasswordFields);
        foreach(var box in new[]{card.School,card.AdminUsername,card.SimpleData,card.Backup})box.LostFocus+=(_,_)=>{SaveSettings();Refresh();};
        card.ProductionFields.Margin=new Thickness(0,12,0,0);card.ProductionFields.Children.Add(Note("계정·HTTPS 인증서·백업을 준비한 운영 설정을 선택하세요. 도입·운영 내부결재 승인 문서는 학교에 보관하고, 적용되는 학운위 심의·보안 검토를 확인하세요. 승인 문서는 공개 설치본에 넣지 않습니다."));card.Config.Margin=new Thickness(0,8,0,0);card.ProductionFields.Children.Add(card.Config);
        var select=new Button{Content="운영 설정 파일 선택",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,0)};select.Click+=(_,_)=>{var dialog=new OpenFileDialog{Filter="운영 설정 (*.json)|*.json",CheckFileExists=true};if(dialog.ShowDialog(this)==true){card.Config.Text=dialog.FileName;SaveSettings();Refresh();}};card.ProductionFields.Children.Add(select);controls.Children.Add(card.ProductionFields);
        var autoText=new StackPanel();autoText.Children.Add(AdminUi.Label("자동 시작","Ink",14,true));autoText.Children.Add(AdminUi.Label("관리자 프로그램을 열면 이 서버도 함께 시작합니다."));
        card.Auto.Content=autoText;card.Auto.SetResourceReference(StyleProperty,"Switch");card.Auto.Margin=new Thickness(0,14,0,4);card.Auto.BorderThickness=new Thickness(0,1,0,0);card.Auto.Padding=new Thickness(0,12,0,0);card.Auto.ToolTip="시험 서버의 최초 설정을 완료한 후 선택할 수 있습니다.";System.Windows.Automation.AutomationProperties.SetName(card.Auto,"관리 프로그램을 열면 이 서버도 자동 시작");controls.Children.Add(card.Auto);card.Auto.Click+=(_,_)=>SaveSettings();
        var path=new StackPanel();path.Children.Add(card.Data);var dataNote=Note("시험 데이터의 저장 위치입니다. 운영 저장 위치는 운영 설정에서 지정합니다.");dataNote.Margin=new Thickness(0,6,0,0);path.Children.Add(dataNote);var advanced=new Expander{Header="데이터 저장 위치",Content=path,Margin=new Thickness(0,6,0,8)};controls.Children.Add(advanced);
        card.Mode.SelectionChanged+=(_,_)=>{Refresh();SaveSettings();};card.Data.TextChanged+=(_,_)=>Refresh();
        card.Details.Content=card.Log;card.Log.FontFamily=new FontFamily("Consolas");controls.Children.Add(card.Details);
        card.SettingsCard.Child=new ScrollViewer{Content=controls,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var checklist=new DockPanel();var checklistHead=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,6)};checklistHead.Children.Add(AdminUi.Heading("운영 점검"));card.ChecklistBadge.Margin=new Thickness(8,0,0,0);checklistHead.Children.Add(card.ChecklistBadge);DockPanel.SetDock(checklistHead,Dock.Top);checklist.Children.Add(checklistHead);
        checklist.Children.Add(new ScrollViewer{Content=card.Checklist,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});card.ChecklistCard.Child=checklist;
        foreach(var part in new[]{card.StatusCard,card.SettingsCard,card.ChecklistCard,card.Empty}){part.Padding=new Thickness(part==card.Empty?40:20);part.CornerRadius=new CornerRadius(10);part.BorderThickness=new Thickness(1);part.SetResourceReference(Border.BackgroundProperty,"Surface");part.SetResourceReference(Border.BorderBrushProperty,"Line");}
        card.DetailsGrid.Children.Add(card.SettingsCard);card.DetailsGrid.Children.Add(card.ChecklistCard);
        card.DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition());card.DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});card.DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(400)});
        card.DetailsGrid.RowDefinitions.Add(new RowDefinition());card.DetailsGrid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        Grid.SetRow(card.DetailsGrid,2);card.View.Children.Add(card.DetailsGrid);card.View.SizeChanged+=(_,_)=>ArrangeDetails(card);
        card.Start.Click+=async(_,_)=>await Start(card);card.Stop.Click+=async(_,_)=>await Stop(card);card.Admin.Click+=(_,_)=>OpenAdmin(card);
        // Production paths are governed by the selected operation configuration.
        card.Mode.SelectionChanged+=(_,_)=>advanced.Visibility=card.Mode.SelectedIndex==0?Visibility.Visible:Visibility.Collapsed;
    }
    // Checklist beside the form on wide windows, below it on narrow ones; other modes have no checklist.
    static void ArrangeDetails(ServerCard card)
    {
        var simple=Simple(card);var wide=card.View.ActualWidth>=820;
        card.ChecklistCard.Visibility=simple?Visibility.Visible:Visibility.Collapsed;
        Grid.SetRow(card.ChecklistCard,wide?0:1);Grid.SetColumnSpan(card.ChecklistCard,wide?1:3);card.ChecklistCard.Margin=new Thickness(0,wide?0:16,0,0);card.ChecklistCard.MaxHeight=wide?double.PositiveInfinity:240;
        Grid.SetColumn(card.SettingsCard,simple&&wide?2:0);Grid.SetColumnSpan(card.SettingsCard,simple&&wide?1:3);
    }
    void BuildEmptyState(ServerCard card)
    {
        var news=card.Service=="announcements";var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MaxWidth=480};
        var ring=new Border{Width=64,Height=64,CornerRadius=new CornerRadius(32),HorizontalAlignment=HorizontalAlignment.Center,Child=AdminUi.Icon(news?"AdminStudentIcon":"AdminServerIcon",30,news?"News":"Blue")};ring.SetResourceReference(Border.BackgroundProperty,news?"NewsTint":"Pale");content.Children.Add(ring);
        var badge=AdminUi.Badge("미설치","Neutral");badge.HorizontalAlignment=HorizontalAlignment.Center;badge.Margin=new Thickness(0,14,0,0);content.Children.Add(badge);
        var title=AdminUi.Heading("아직 설치하지 않았습니다",22);title.TextAlignment=TextAlignment.Center;title.Margin=new Thickness(0,10,0,8);content.Children.Add(title);
        card.EmptyNote.TextAlignment=TextAlignment.Center;content.Children.Add(card.EmptyNote);
        if(news)
        {
            var list=new StackPanel();list.Children.Add(AdminUi.Group("설치 전에 준비할 것"));
            foreach(var item in new[]{"학부모 휴대폰이 학교 밖에서 접속할 주소(도메인 또는 터널)","학생·학부모 개인정보 수집·이용 동의 절차","학교장 승인 문서 (교내 업무 서버와 같은 문서 사용 가능)"})
            {var row=new DockPanel{Margin=new Thickness(0,0,0,6)};var mark=AdminUi.Icon("AdminCheckIcon",14,"News");mark.Margin=new Thickness(0,0,8,0);row.Children.Add(mark);row.Children.Add(AdminUi.Label(item,"Ink",13));list.Children.Add(row);}
            var box=new Border{Child=list,Padding=new Thickness(18,14,18,10),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,16,0,0)};box.SetResourceReference(Border.BackgroundProperty,"Subtle");content.Children.Add(box);
        }
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,18,0,0)};
        card.Activate.Content=AdminUi.IconLabel("AdminDownloadIcon","설치하기");card.Activate.SetResourceReference(StyleProperty,news?"NewsButton":"PrimaryButton");card.Activate.Margin=new Thickness(0,0,8,0);System.Windows.Automation.AutomationProperties.SetName(card.Activate,"설치하기");card.Activate.Click+=(_,_)=>ActivateService(card);buttons.Children.Add(card.Activate);
        var guide=new Button{Content="설치 안내 보기"};guide.Click+=(_,_)=>OpenGuide();buttons.Children.Add(guide);content.Children.Add(buttons);
        card.Empty.Child=content;Grid.SetRow(card.Empty,1);Grid.SetRowSpan(card.Empty,2);card.View.Children.Add(card.Empty);
    }
    static TextBlock FieldLabel(string label){var text=AdminUi.Label(label);text.Margin=new Thickness(0,12,0,6);return text;}
    static void Field(StackPanel panel,string label,TextBox box,string note)
    {
        System.Windows.Automation.AutomationProperties.SetName(box,label);
        panel.Children.Add(FieldLabel(label));panel.Children.Add(box);var hint=Note(note);hint.Margin=new Thickness(0,6,0,0);hint.FontSize=12;panel.Children.Add(hint);
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

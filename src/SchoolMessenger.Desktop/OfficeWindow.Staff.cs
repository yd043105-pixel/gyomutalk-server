using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    string? selectedStaff;string staffFilter="all";
    record StaffRow(JsonElement User,string Id,string Name,string Department,string Position,string Username,string Seat,bool Active,bool Pending,bool Admin,bool Broadcast,bool Calendar,bool Rooms,bool Electives);
    static string Value(JsonElement row,string key)=>row.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    static bool Flag(JsonElement row,string key)=>row.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.True;
    // Member directory: searchable table on the left, the selected teacher's settings on the right (Figma "구성원 · 교직원·권한").
    async Task StaffList()
    {
        var usersTask=api.Get<JsonElement>("api/admin/users");var permissionsTask=api.Get<JsonElement>("api/admin/office/permissions");var pendingTask=api.Get<JsonElement>("api/admin/registrations");var electivesTask=api.Get<JsonElement>("api/admin/elective-managers");
        var permissions=(await permissionsTask).EnumerateArray().ToDictionary(p=>Value(p,"userId"),p=>(Calendar:Flag(p,"calendarManager"),Rooms:Flag(p,"roomManager")));
        var electives=(await electivesTask).EnumerateArray().Where(e=>Flag(e,"enabled")).Select(e=>Value(e,"id")).ToHashSet();
        var staff=(await usersTask).EnumerateArray().Select(u=>{var id=Value(u,"id");permissions.TryGetValue(id,out var p);return new StaffRow(u,id,Value(u,"name"),Value(u,"department"),Value(u,"position"),Value(u,"username"),Value(u,"seatIp"),Flag(u,"active"),Flag(u,"activationRequired"),Flag(u,"isAdmin"),Flag(u,"canBroadcast"),p.Calendar,p.Rooms,electives.Contains(id));}).ToArray();
        var pending=(await pendingTask).GetArrayLength();
        Page(false);
        var heading=new DockPanel{Margin=new Thickness(0,0,0,16)};
        var add=new Button{Content=AdminUi.IconLabel("AdminPlusIcon","교직원 등록"),VerticalAlignment=VerticalAlignment.Bottom,ToolTip="교직원 사전 등록 · 가입 승인"};add.SetResourceReference(StyleProperty,"PrimaryButton");AutomationProperties.SetName(add,"교직원 등록 · 승인");add.Click+=async(_,_)=>await Run(Administration);DockPanel.SetDock(add,Dock.Right);heading.Children.Add(add);
        var introduction=new StackPanel();introduction.Children.Add(AdminUi.Heading("교직원·권한",22));var description=AdminUi.Label("교직원 계정과 학교 업무 권한을 한 곳에서 관리합니다.","Muted",13);description.Margin=new Thickness(0,4,0,0);introduction.Children.Add(description);heading.Children.Add(introduction);body.Children.Add(heading);
        // Toolbar: search and status filters; the sign-up filter opens the approval page.
        var toolbar=new DockPanel{Margin=new Thickness(0,0,0,16)};var count=AdminUi.Label("","Faint");count.Margin=new Thickness(2,8,0,0);DockPanel.SetDock(count,Dock.Bottom);toolbar.Children.Add(count);
        var tools=new WrapPanel();toolbar.Children.Add(tools);body.Children.Add(toolbar);
        var searchBox=new Grid{Width=240,Margin=new Thickness(0,0,8,6)};var search=new TextBox{Padding=new Thickness(32,6,8,6),ToolTip="이름 · 부서 · 아이디 · 좌석 IP 검색"};AutomationProperties.SetName(search,"교직원 검색");searchBox.Children.Add(search);
        var glass=AdminUi.Icon("AdminSearchIcon",15,"Faint");glass.HorizontalAlignment=HorizontalAlignment.Left;glass.Margin=new Thickness(10,0,0,0);glass.IsHitTestVisible=false;searchBox.Children.Add(glass);
        var hint=AdminUi.Label("이름 · 부서 · 좌석 IP 검색","Faint",12);hint.IsHitTestVisible=false;hint.Margin=new Thickness(33,0,0,0);hint.VerticalAlignment=VerticalAlignment.Center;searchBox.Children.Add(hint);tools.Children.Add(searchBox);
        var rows=new StackPanel();var panel=new Border();var chips=new List<Button>();
        void Chip(string filter,string label,int number,bool warn=false)
        {
            var content=new StackPanel{Orientation=Orientation.Horizontal};
            if(warn){var dot=new System.Windows.Shapes.Ellipse{Width=7,Height=7,Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"Warning");content.Children.Add(dot);}
            content.Children.Add(new TextBlock{Text=label,FontWeight=FontWeights.SemiBold});content.Children.Add(new TextBlock{Text=" "+number,Opacity=0.65});
            var chip=new Button{Content=content,Uid=filter,Tag=filter==staffFilter?"selected":null};chip.SetResourceReference(StyleProperty,"Chip");AutomationProperties.SetName(chip,label+" "+number+"명");
            chip.Click+=async(_,_)=>{if(filter=="pending"){await Run(Administration);return;}staffFilter=filter;foreach(var c in chips)c.Tag=c.Uid==staffFilter?"selected":null;Rows();};
            chips.Add(chip);tools.Children.Add(chip);
        }
        Chip("all","전체",staff.Length);Chip("active","사용 중",staff.Count(s=>s.Active&&!s.Pending));Chip("stopped","중지",staff.Count(s=>!s.Active));if(pending>0)Chip("pending","가입 신청",pending,true);
        // Table and detail panel; the panel drops below the table on narrow windows.
        var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition());content.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});content.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(340)});content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.Children.Add(content);
        var table=new StackPanel();
        Grid Columns(){var grid=new Grid();foreach(var width in new[]{new GridLength(1.3,GridUnitType.Star),new GridLength(1,GridUnitType.Star),new GridLength(1.4,GridUnitType.Star),new GridLength(88)})grid.ColumnDefinitions.Add(new ColumnDefinition{Width=width});grid.ColumnDefinitions[0].MinWidth=140;return grid;}
        var labels=Columns();var titles=new[]{"이름","부서 · 직책","업무 권한","상태"};for(var i=0;i<titles.Length;i++){var text=AdminUi.Label(titles[i],"Muted",11,true);Grid.SetColumn(text,i);labels.Children.Add(text);}
        var header=new Border{Child=labels,Padding=new Thickness(16,11,16,11),BorderThickness=new Thickness(0,0,0,1)};header.SetResourceReference(Border.BackgroundProperty,"Subtle");header.SetResourceReference(Border.BorderBrushProperty,"Line");table.Children.Add(header);table.Children.Add(rows);
        var tableCard=AdminUi.Card(table,0);tableCard.ClipToBounds=true;tableCard.VerticalAlignment=VerticalAlignment.Top;content.Children.Add(tableCard);
        var panelCard=AdminUi.Card(panel,20);panelCard.VerticalAlignment=VerticalAlignment.Top;content.Children.Add(panelCard);
        content.SizeChanged+=(_,_)=>{var wide=content.ActualWidth>=760;Grid.SetColumn(panelCard,wide?2:0);Grid.SetRow(panelCard,wide?0:1);Grid.SetColumnSpan(panelCard,wide?1:3);Grid.SetColumnSpan(tableCard,wide?1:3);panelCard.Margin=new Thickness(0,wide?0:16,0,0);};
        static Border Status(StaffRow s)=>s.Pending?AdminUi.Badge("활성화 대기","Warning"):s.Active?AdminUi.Badge("사용 중","Success"):AdminUi.Badge("중지","Neutral");
        void Rows()
        {
            rows.Children.Clear();var query=search.Text.Trim();hint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
            var visible=staff.Where(s=>staffFilter switch{"active"=>s.Active&&!s.Pending,"stopped"=>!s.Active,_=>true}).Where(s=>query.Length==0||(s.Name+" "+s.Department+" "+s.Position+" "+s.Username+" "+s.Seat).Contains(query,StringComparison.CurrentCultureIgnoreCase)).ToArray();
            foreach(var s in visible)
            {
                var selected=s.Id==selectedStaff;var row=Columns();
                var person=new DockPanel();var avatar=AdminUi.Avatar(s.Name);if(selected)avatar.SetResourceReference(Border.BackgroundProperty,"Surface");person.Children.Add(avatar);
                var names=new StackPanel{Margin=new Thickness(10,0,8,0),VerticalAlignment=VerticalAlignment.Center};var name=AdminUi.Label(s.Name,"Ink",13,true);name.TextTrimming=TextTrimming.CharacterEllipsis;name.TextWrapping=TextWrapping.NoWrap;names.Children.Add(name);names.Children.Add(AdminUi.Label(s.Seat.Length>0?"좌석 "+s.Seat:"좌석 미지정","Faint",11));person.Children.Add(names);row.Children.Add(person);
                var department=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};department.Children.Add(AdminUi.Label(s.Department,"Ink"));if(s.Position.Length>0)department.Children.Add(AdminUi.Label(s.Position,"Muted",11));Grid.SetColumn(department,1);row.Children.Add(department);
                var roles=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};
                foreach(var (on,label,tone) in new[]{(s.Admin,"관리자","Accent"),(s.Broadcast,"전체 공지","Neutral"),(s.Calendar,"학사일정","Neutral"),(s.Rooms,"특별실","Neutral"),(s.Electives&&!s.Admin,"수강신청","Neutral")}){if(!on)continue;var badge=AdminUi.Badge(label,tone,false);badge.Margin=new Thickness(0,2,6,2);if(selected)badge.SetResourceReference(Border.BackgroundProperty,"Surface");roles.Children.Add(badge);}
                Grid.SetColumn(roles,2);row.Children.Add(roles);var state=Status(s);Grid.SetColumn(state,3);row.Children.Add(state);
                var line=new Button{Content=row,Tag=selected?"selected":null};line.SetResourceReference(StyleProperty,"TableRow");AutomationProperties.SetName(line,s.Name+" 계정 설정");AutomationProperties.SetItemStatus(line,selected?"선택됨":"");
                line.Click+=(_,_)=>{selectedStaff=s.Id;Rows();Detail();};rows.Children.Add(line);
            }
            if(visible.Length==0){var none=AdminUi.Label("일치하는 교직원이 없습니다.","Muted",13);none.Margin=new Thickness(16,20,16,20);rows.Children.Add(none);}
            count.Text=visible.Length==staff.Length?$"전체 {staff.Length}명":$"{visible.Length}명 표시 · 전체 {staff.Length}명";
        }
        void Detail()
        {
            var s=staff.FirstOrDefault(x=>x.Id==selectedStaff);if(s is null){panel.Child=AdminUi.Label("교직원을 선택하세요.","Muted",13);return;}
            var stack=new StackPanel();panel.Child=stack;
            var top=new DockPanel{Margin=new Thickness(0,0,0,18)};var state=Status(s);DockPanel.SetDock(state,Dock.Right);top.Children.Add(state);top.Children.Add(AdminUi.Avatar(s.Name,44));
            var who=new StackPanel{Margin=new Thickness(12,0,8,0),VerticalAlignment=VerticalAlignment.Center};who.Children.Add(AdminUi.Heading(s.Name));who.Children.Add(AdminUi.Label(string.Join(" · ",new[]{"아이디 "+s.Username,s.Department,s.Position}.Where(v=>v.Trim().Length>0)),"Muted",11));top.Children.Add(who);stack.Children.Add(top);
            stack.Children.Add(AdminUi.Group("기본 정보"));
            var fields=new Grid();fields.ColumnDefinitions.Add(new ColumnDefinition());fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});fields.ColumnDefinitions.Add(new ColumnDefinition());stack.Children.Add(fields);
            TextBox Input(string label,string value,int index,int maxLength)
            {
                if(index%2==0)fields.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                var cell=new StackPanel{Margin=new Thickness(0,0,0,12)};var caption=AdminUi.Label(label,"Muted",12);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);
                var box=new TextBox{Text=value,MaxLength=maxLength};AutomationProperties.SetName(box,label);cell.Children.Add(box);Grid.SetRow(cell,index/2);Grid.SetColumn(cell,index%2==0?0:2);fields.Children.Add(cell);return box;
            }
            var name=Input("이름",s.Name,0,40);var position=Input("직책",s.Position,1,50);var department=Input("부서",s.Department,2,60);var seat=Input("좌석 IP",s.Seat,3,15);seat.ToolTip="교사 PC 자동 로그인용 IPv4 · 휴대폰 로그인에는 쓰지 않습니다. 비우면 연결 해제";
            var roles=AdminUi.Group("업무 권한");roles.Margin=new Thickness(0,8,0,0);stack.Children.Add(roles);
            CheckBox Switch(string title,string detail,bool value)
            {
                var text=new StackPanel();text.Children.Add(AdminUi.Label(title,"Ink",13,true));text.Children.Add(AdminUi.Label(detail,"Muted",11));
                var toggle=new CheckBox{Content=text,IsChecked=value};toggle.SetResourceReference(StyleProperty,"Switch");AutomationProperties.SetName(toggle,title);stack.Children.Add(toggle);return toggle;
            }
            var admin=Switch("시스템 관리자","관리자 프로그램에 로그인하고 계정을 관리합니다.",s.Admin);
            var broadcast=Switch("학교 전체 공지","전 교직원에게 공지 메시지를 보낼 수 있습니다.",s.Broadcast);
            var calendar=Switch("학사일정 담당","학사일정을 등록하고 변경합니다.",s.Calendar);
            var rooms=Switch("특별실 운영 담당","특별실을 추가하고 예약을 조정합니다.",s.Rooms);
            // Administrators manage electives by default; the server only stores the duty for other staff.
            var electives=Switch("수강신청 담당",s.Admin?"관리자는 기본으로 수강신청 업무를 담당합니다.":"수강신청 회차를 만들고 학생 신청 현황을 관리합니다.",s.Electives||s.Admin);electives.BorderThickness=new Thickness(0);electives.IsEnabled=!s.Admin;
            var actions=new DockPanel{Margin=new Thickness(0,18,0,0)};
            var reset=new Button{Content="계정 초기화",ToolTip="초기 비밀번호 12345678로 되돌리고 새 활성화 번호를 발급합니다."};reset.SetResourceReference(StyleProperty,"DangerButton");actions.Children.Add(reset);
            var save=new Button{Content="변경 저장",Margin=new Thickness(8,0,0,0)};save.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(save,Dock.Right);
            var toggleActive=new Button{Content=s.Active?"사용 중지":"다시 사용",Margin=new Thickness(8,0,0,0)};DockPanel.SetDock(toggleActive,Dock.Right);
            actions.Children.Add(save);actions.Children.Add(toggleActive);actions.Children.Add(new Border());stack.Children.Add(actions);
            save.Click+=async(_,_)=>await Run(async()=>
            {
                await api.Send<JsonElement>(HttpMethod.Patch,"api/admin/users/"+s.Id,new{name=name.Text,department=department.Text,position=position.Text,password="",isAdmin=admin.IsChecked==true,canBroadcast=broadcast.IsChecked==true,active=s.Active});
                if(calendar.IsChecked!=s.Calendar||rooms.IsChecked!=s.Rooms)await api.Send<JsonElement>(HttpMethod.Post,"api/admin/office/permissions",new OfficePermissionRequest(s.Id,calendar.IsChecked==true,rooms.IsChecked==true));
                if(admin.IsChecked!=true&&electives.IsChecked!=s.Electives)await api.Send<JsonElement>(HttpMethod.Post,"api/admin/elective-managers",new ElectiveManagerChange(s.Id,electives.IsChecked==true));
                if(seat.Text.Trim()!=s.Seat)await api.Send<JsonElement>(HttpMethod.Post,"api/admin/staff/"+s.Id+"/seat",new StaffSeatRequest(seat.Text.Trim()));
                await api.RefreshSession();await StaffList();error.Text=s.Name+" 님의 설정을 저장했습니다.";
            });
            toggleActive.Click+=async(_,_)=>await Run(async()=>
            {
                if(s.Active&&MessageBox.Show(InteractionOwner,s.Name+" 님의 계정을 중지할까요?\n모든 기기에서 바로 로그아웃되고, 다시 사용으로 되돌릴 수 있습니다.","계정 사용 중지",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                await api.Send<JsonElement>(HttpMethod.Patch,"api/admin/users/"+s.Id,new{name=s.Name,department=s.Department,position=s.Position,password="",isAdmin=s.Admin,canBroadcast=s.Broadcast,active=!s.Active});await api.RefreshSession();await StaffList();
            });
            reset.Click+=async(_,_)=>await Run(async()=>
            {
                if(MessageBox.Show(InteractionOwner,s.Name+" 님의 계정을 초기화할까요?\n\n아이디 "+s.Username+"와 초기 비밀번호 12345678로 바뀌고, 모든 기기에서 로그아웃됩니다. 새 활성화 번호로 다시 시작해야 합니다.","교직원 계정 초기화",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                var result=await api.Send<JsonElement>(HttpMethod.Post,"api/admin/staff/"+s.Id+"/reset",new StaffAccountRequest(s.Username,s.Name,s.Department,null,"12345678",UseSharedPassword:true));
                MessageBox.Show(InteractionOwner,"개별 전달할 활성화 번호:\n"+result.GetProperty("activationCode").GetString()+"\n유효 기간: 7일. 번호는 이 화면에서만 표시됩니다.","계정 초기화");await StaffList();
            });
        }
        if(staff.All(s=>s.Id!=selectedStaff))selectedStaff=staff.FirstOrDefault()?.Id;
        search.TextChanged+=(_,_)=>Rows();Rows();Detail();
    }
    async Task Audit()
    {
        var rows=await api.Get<JsonElement>("api/admin/office/audit");Page(false);
        Heading("운영 변경 기록","관리자가 바꾼 계정·권한·학교 설정의 최근 운영 변경 기록입니다. 최대 200건까지 보입니다.",null,Embedded?null:("교직원 등록 · 승인",Administration));
        var list=new StackPanel();
        foreach(var row in rows.EnumerateArray())
        {
            var line=new DockPanel();var time=AdminUi.Label(DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("createdAt").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm"),"Faint",11);time.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(time,Dock.Right);line.Children.Add(time);
            var text=new StackPanel{Margin=new Thickness(0,0,12,0)};text.Children.Add(AdminUi.Label(Value(row,"actor")+" · "+Value(row,"action"),"Ink",13,true));text.Children.Add(AdminUi.Label("대상 "+Value(row,"resourceId"),"Muted",11));line.Children.Add(text);
            var item=new Border{Child=line,Padding=new Thickness(16,10,16,10),BorderThickness=new Thickness(0,0,0,1)};item.SetResourceReference(Border.BorderBrushProperty,"Line");list.Children.Add(item);
        }
        if(rows.GetArrayLength()==0){var none=AdminUi.Label("아직 기록이 없습니다.","Muted",13);none.Margin=new Thickness(16,20,16,20);list.Children.Add(none);}
        var card=AdminUi.Card(list,0);card.ClipToBounds=true;body.Children.Add(card);
    }
    // Inside the administrator program the rail already links every page, so only the way back is shown.
    bool Embedded=>InteractionOwner is LocalServerWindow;
    void Heading(string title,string description,UIElement? action=null,(string Label,Func<Task> Open)? back=null)
    {
        if(back is{} link){var b=new Button{Content="← "+link.Label,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(-8,0,0,6)};b.SetResourceReference(StyleProperty,"SubtleButton");b.Click+=async(_,_)=>await Run(link.Open);body.Children.Add(b);}
        var heading=new DockPanel{Margin=new Thickness(0,0,0,16)};
        if(action is not null){DockPanel.SetDock(action,Dock.Right);if(action is FrameworkElement e)e.VerticalAlignment=VerticalAlignment.Bottom;heading.Children.Add(action);}
        var text=new StackPanel();text.Children.Add(AdminUi.Heading(title,22));var detail=AdminUi.Label(description,"Muted",13);detail.Margin=new Thickness(0,4,0,0);text.Children.Add(detail);heading.Children.Add(text);body.Children.Add(heading);
    }
    async Task Administration()
    {
        var pending=await api.Get<JsonElement>("api/admin/registrations");
        Page(false);
        Heading("교직원 등록 · 승인","새 교직원 계정을 미리 만들거나, 교직원이 보낸 가입 신청을 승인합니다.",null,("교직원 목록",StaffList));
        if(!Embedded)
        {
            // The teacher messenger has no rail, so its administrators get the other pages as a single row of links.
            var links=new WrapPanel{Margin=new Thickness(0,-6,0,16)};
            foreach(var (label,open) in new (string,Func<Task>)[]{("학사일정 · 특별실 예약",()=>Load(false)),("특별실 목록",async()=>{setup=await api.Get<JsonElement>("api/office/setup");await Rooms();}),("선택과목 분반",Sections),("학생·학부모",()=>{new PortalAdministrationWindow(InteractionOwner).Show();return Task.CompletedTask;}),("운영 변경 기록",Audit)})
            {var link=new Button{Content=label,Margin=new Thickness(0,0,6,6)};link.SetResourceReference(StyleProperty,"Chip");link.Click+=async(_,_)=>await Run(open);links.Children.Add(link);}
            body.Children.Add(links);
        }
        var recovery=new StackPanel();body.Children.Add(recovery);await RecoveryReview.Add(recovery,api,InteractionOwner,Administration);
        var content=new Grid();content.ColumnDefinitions.Add(new ColumnDefinition());content.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});content.ColumnDefinitions.Add(new ColumnDefinition());content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});body.Children.Add(content);
        // New account form.
        var form=new StackPanel();form.Children.Add(AdminUi.Heading("새 교직원 등록"));var note=AdminUi.Label("첫 로그인은 공통 초기 비밀번호(12345678)와 등록 후 받는 활성화 번호를 함께 넣어야 합니다. 활성화 번호는 본인에게만 전달하세요. 로그인하면 바로 개인 비밀번호로 바꿉니다.","Muted",12);note.Margin=new Thickness(0,4,0,16);form.Children.Add(note);
        var fields=new Grid();fields.ColumnDefinitions.Add(new ColumnDefinition());fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});fields.ColumnDefinitions.Add(new ColumnDefinition());form.Children.Add(fields);
        TextBox Input(string label,int index,int maxLength,string? hint=null)
        {
            if(index%2==0)fields.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var cell=new StackPanel{Margin=new Thickness(0,0,0,12)};var caption=AdminUi.Label(label,"Muted",12);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);
            var box=new TextBox{MaxLength=maxLength,ToolTip=hint};AutomationProperties.SetName(box,label);cell.Children.Add(box);Grid.SetRow(cell,index/2);Grid.SetColumn(cell,index%2==0?0:2);fields.Children.Add(cell);return box;
        }
        var name=Input("이름",0,40);var login=Input("로그인 아이디",1,40,"기본은 본인 이름입니다. 동명이인은 이름.2처럼 구분하세요.");name.TextChanged+=(_,_)=>login.Text=name.Text.Trim();
        var department=Input("부서",2,60);var position=Input("직책",3,50,"예: 교무부장, 담임");var ip=Input("좌석 IP · 교사 자리 PC 주소 · 선택",4,15,"교사 PC 자동 로그인용 IPv4입니다. 아이디가 아니며 휴대폰 로그인에는 쓰지 않습니다.");
        var register=new Button{Content="등록하고 활성화 번호 받기",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,4,0,0)};register.SetResourceReference(StyleProperty,"PrimaryButton");form.Children.Add(register);
        register.Click+=async(_,_)=>await Run(async()=>
        {
            var value=await api.Send<JsonElement>(HttpMethod.Post,"api/admin/staff",new StaffAccountRequest(login.Text.Trim(),name.Text,department.Text,ip.Text,"12345678",position.Text,true));
            MessageBox.Show(InteractionOwner,"개별 전달할 활성화 번호:\n"+value.GetProperty("activationCode").GetString()+"\n유효 기간: 7일. 번호는 이 화면에서만 표시됩니다.","교직원 등록");var added=name.Text.Trim();await Administration();error.Text=added+" 님을 등록했습니다.";
        });
        var formCard=AdminUi.Card(form);formCard.VerticalAlignment=VerticalAlignment.Top;content.Children.Add(formCard);
        // Sign-up requests waiting for approval.
        var requests=new StackPanel();var top=new DockPanel{Margin=new Thickness(0,0,0,12)};var count=AdminUi.Badge(pending.GetArrayLength()+"건",pending.GetArrayLength()>0?"Warning":"Neutral",false);DockPanel.SetDock(count,Dock.Right);top.Children.Add(count);top.Children.Add(AdminUi.Heading("가입 신청"));requests.Children.Add(top);
        foreach(var request in pending.EnumerateArray())
        {
            var row=new DockPanel{Margin=new Thickness(0,0,0,10)};var id=Value(request,"id");var who=Value(request,"name");
            var approve=new Button{Content="승인",Margin=new Thickness(6,0,0,0)};approve.SetResourceReference(StyleProperty,"PrimaryButton");AutomationProperties.SetName(approve,who+" 신청 승인");
            var reject=new Button{Content="반려",Margin=new Thickness(6,0,0,0)};AutomationProperties.SetName(reject,who+" 신청 반려");
            approve.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/registrations/"+id+"/approve");await Administration();error.Text=who+" 님의 가입을 승인했습니다.";});
            reject.Click+=async(_,_)=>await Run(async()=>{if(MessageBox.Show(InteractionOwner,who+" 님의 가입 신청을 반려할까요?","가입 신청 반려",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/admin/registrations/"+id+"/reject");await Administration();});
            foreach(var b in new[]{reject,approve}){DockPanel.SetDock(b,Dock.Right);row.Children.Add(b);}
            row.Children.Add(AdminUi.Avatar(who));var text=new StackPanel{Margin=new Thickness(10,0,8,0),VerticalAlignment=VerticalAlignment.Center};text.Children.Add(AdminUi.Label(who,"Ink",13,true));
            var created=request.TryGetProperty("createdAt",out var at)&&at.ValueKind==JsonValueKind.Number?DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M월 d일 신청"):"";
            text.Children.Add(AdminUi.Label(string.Join(" · ",new[]{Value(request,"department"),"아이디 "+Value(request,"username"),created}.Where(v=>v.Length>0)),"Muted",11));row.Children.Add(text);requests.Children.Add(row);
        }
        if(pending.GetArrayLength()==0)requests.Children.Add(AdminUi.Label("대기 중인 가입 신청이 없습니다. 교직원이 교사 메신저에서 ‘계정 신청’을 보내면 여기에 표시됩니다.","Muted",12));
        var requestCard=AdminUi.Card(requests);requestCard.VerticalAlignment=VerticalAlignment.Top;content.Children.Add(requestCard);
        content.SizeChanged+=(_,_)=>{var wide=content.ActualWidth>=760;Grid.SetColumn(requestCard,wide?2:0);Grid.SetRow(requestCard,wide?0:1);Grid.SetColumnSpan(formCard,wide?1:3);Grid.SetColumnSpan(requestCard,wide?1:3);requestCard.Margin=new Thickness(0,wide?0:16,0,0);};
    }
}

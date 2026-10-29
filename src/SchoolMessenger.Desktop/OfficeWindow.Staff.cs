using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    async Task StaffList()
    {
        body.Children.Clear();
        var heading=new DockPanel{Margin=new Thickness(0,0,0,20)};var add=new Button{Content="교직원 등록 · 승인",VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(16,2,0,0),Padding=new Thickness(14,10,14,10)};add.SetResourceReference(StyleProperty,"PrimaryButton");add.ToolTip="교직원 사전 등록 · 가입 승인 · 업무 담당 권한";add.Click+=async(_,_)=>await Run(Administration);DockPanel.SetDock(add,Dock.Right);heading.Children.Add(add);
        var introduction=new StackPanel();var title=Text("교직원·권한",24);title.FontWeight=FontWeights.Bold;title.Margin=new Thickness(0,0,0,8);introduction.Children.Add(title);var description=Text("계정 정보와 학교 업무 권한을 관리하세요.",12);description.Margin=new Thickness(0);description.SetResourceReference(TextBlock.ForegroundProperty,"Muted");introduction.Children.Add(description);heading.Children.Add(introduction);body.Children.Add(heading);
        var users=await api.Get<JsonElement>("api/admin/users");
        var searchGrid=new Grid{Margin=new Thickness(0,0,0,18)};var search=new TextBox{MinHeight=40};System.Windows.Automation.AutomationProperties.SetName(search,"교직원 검색");search.ToolTip="이름 · 부서 · 로그인 아이디 검색";searchGrid.Children.Add(search);var hint=new TextBlock{Text="이름 · 부서 · 로그인 아이디 검색",IsHitTestVisible=false,Margin=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center};hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");searchGrid.Children.Add(hint);body.Children.Add(searchGrid);
        var count=Text("",12);count.Margin=new Thickness(0,0,0,10);count.SetResourceReference(TextBlock.ForegroundProperty,"Muted");body.Children.Add(count);
        Grid Columns(){var grid=new Grid();foreach(var width in new[]{new GridLength(1.1,GridUnitType.Star),new GridLength(1.2,GridUnitType.Star),new GridLength(1,GridUnitType.Star),new GridLength(66),new GridLength(74)})grid.ColumnDefinitions.Add(new ColumnDefinition{Width=width});return grid;}
        TextBlock Cell(string value,bool muted=false){var text=new TextBlock{Text=value,FontSize=12,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,0,12,0),ToolTip=value};if(muted)text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");return text;}
        var labels=Columns();var titles=new[]{"이름","부서 · 직책","아이디","상태","관리"};for(var i=0;i<titles.Length;i++){var text=Cell(titles[i],true);text.FontSize=11;Grid.SetColumn(text,i);labels.Children.Add(text);}var header=new Border{Child=labels,Padding=new Thickness(14,12,14,12),CornerRadius=new CornerRadius(8,8,0,0)};header.SetResourceReference(Border.BackgroundProperty,"Soft");body.Children.Add(header);var list=new StackPanel();body.Children.Add(list);
        void Render()
        {
            list.Children.Clear();var query=search.Text.Trim();hint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
            var visible=0;foreach(var user in users.EnumerateArray())
            {
                var name=user.GetProperty("name").GetString()??"";var department=user.GetProperty("department").GetString()??"";var username=user.GetProperty("username").GetString()??"";
                if(query.Length>0&&!(name+" "+department+" "+username).Contains(query,StringComparison.CurrentCultureIgnoreCase))continue;
                visible++;var row=Columns();var person=Cell(name);person.FontWeight=FontWeights.SemiBold;row.Children.Add(person);var position=user.TryGetProperty("position",out var p)?p.GetString():"";var departmentCell=new StackPanel{VerticalAlignment=VerticalAlignment.Center};departmentCell.Children.Add(Cell(department));if(!string.IsNullOrWhiteSpace(position)){var detail=Cell(position,true);detail.FontSize=11;detail.Margin=new Thickness(0,4,12,0);departmentCell.Children.Add(detail);}Grid.SetColumn(departmentCell,1);row.Children.Add(departmentCell);
                var identifier=Cell(username,true);Grid.SetColumn(identifier,2);row.Children.Add(identifier);var active=user.GetProperty("active").GetBoolean();var state=Cell(active?"사용 중":"중지",true);state.FontSize=11;state.Margin=new Thickness(0);var badge=new Border{Child=state,Padding=new Thickness(8,4,8,4),CornerRadius=new CornerRadius(5),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};badge.SetResourceReference(Border.BackgroundProperty,active?"Pale":"Soft");if(active)state.SetResourceReference(TextBlock.ForegroundProperty,"Blue");Grid.SetColumn(badge,3);row.Children.Add(badge);
                var edit=new Button{Content="설정",VerticalAlignment=VerticalAlignment.Center,Padding=new Thickness(10,6,10,6),Margin=new Thickness(6,0,0,0)};System.Windows.Automation.AutomationProperties.SetName(edit,name+" 계정 설정");edit.Click+=async(_,_)=>await Run(()=>StaffSettings(user));Grid.SetColumn(edit,4);row.Children.Add(edit);
                var line=new Border{Child=row,Padding=new Thickness(14,14,14,14),MinHeight=66,BorderThickness=new Thickness(0,0,0,1)};line.SetResourceReference(Border.BorderBrushProperty,"Line");list.Children.Add(line);
            }
            if(list.Children.Count==0)list.Children.Add(Text("일치하는 교직원이 없습니다."));
            count.Text=query.Length==0?$"전체 {users.GetArrayLength()}명":$"검색 결과 {visible}명 · 전체 {users.GetArrayLength()}명";
        }
        search.TextChanged+=(_,_)=>Render();Render();
    }
    Task StaffSettings(JsonElement user)
    {
        body.Children.Clear();Button("교직원 목록으로",StaffList);var id=user.GetProperty("id").GetString()!;
        var identifier=Field("교내 계정 ID · 외부 교사 연결에 사용",id);identifier.IsReadOnly=true;
        var name=Field("이름",user.GetProperty("name").GetString()!);var department=Field("부서",user.GetProperty("department").GetString()!);var position=Field("직책 · 표시용, 권한과 별도로 관리",user.TryGetProperty("position",out var title)?title.GetString()??"":"");position.MaxLength=50;
        var active=new CheckBox{Content="계정 사용 허용",IsChecked=user.GetProperty("active").GetBoolean()};var admin=new CheckBox{Content="시스템 관리자",IsChecked=user.GetProperty("isAdmin").GetBoolean()};var broadcast=new CheckBox{Content="학교 전체 공지 발송 허용",IsChecked=user.GetProperty("canBroadcast").GetBoolean()};body.Children.Add(active);body.Children.Add(admin);body.Children.Add(broadcast);
        Button("계정 상태 저장 · 기존 세션 회수",async()=>{await api.Send<JsonElement>(HttpMethod.Patch,"api/admin/users/"+id,new{name=name.Text,department=department.Text,position=position.Text,password="",isAdmin=admin.IsChecked==true,canBroadcast=broadcast.IsChecked==true,active=active.IsChecked==true});await api.RefreshSession();await StaffList();});
        Button("좌석 · 초기화 관리",()=>StaffSeat(user));
        return Task.CompletedTask;
    }
    Task StaffSeat(JsonElement user)
    {
        body.Children.Clear();Button("교직원 목록으로",StaffList);var id=user.GetProperty("id").GetString()!;
        body.Children.Add(Text(user.GetProperty("name").GetString()+" · "+user.GetProperty("username").GetString(),20));
        var ip=Field("좌석 IPv4 · 비우면 연결 해제",user.TryGetProperty("seatIp",out var seat)&&seat.ValueKind==JsonValueKind.String?seat.GetString()!:"");
        Button("좌석 변경 · 계정과 업무 기록 유지",async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/staff/"+id+"/seat",new StaffSeatRequest(ip.Text));await StaffList();});
        body.Children.Add(Text("계정 초기화는 모든 기기의 세션을 회수합니다. 새 번호는 교사 본인에게 개별 전달하세요."));var login=Field("로그인 아이디 · 본인 이름, 동명이인은 이름.2",user.GetProperty("name").GetString()!);body.Children.Add(Text("이름 아이디와 초기 비밀번호 12345678로 초기화합니다. 새 활성화 번호 확인과 개인 비밀번호 설정이 필요합니다."));
        Button("계정 초기화 · 활성화 번호 재발급",async()=>
        {
            if(MessageBox.Show(InteractionOwner,"로그인 이름과 초기 비밀번호를 적용하고, 모든 기기의 세션을 회수한 뒤 활성화를 다시 요구할까요?","교직원 계정 초기화",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            var result=await api.Send<JsonElement>(HttpMethod.Post,"api/admin/staff/"+id+"/reset",new StaffAccountRequest(login.Text.Trim(),user.GetProperty("name").GetString()!,user.GetProperty("department").GetString()!,null,"12345678",UseSharedPassword:true));
            MessageBox.Show(InteractionOwner,"개별 전달할 활성화 번호:\n"+result.GetProperty("activationCode").GetString()+"\n유효 기간: 7일.","계정 초기화");await StaffList();
        });return Task.CompletedTask;
    }
    async Task Audit()
    {
        body.Children.Clear();Button("계정 관리로",Administration);body.Children.Add(Text("최근 운영 변경 기록 · 최대 200건",20));
        var rows=await api.Get<JsonElement>("api/admin/office/audit");foreach(var row in rows.EnumerateArray()){var time=DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("createdAt").GetInt64()).ToOffset(TimeSpan.FromHours(9));body.Children.Add(Text(time.ToString("yyyy-MM-dd HH:mm")+" · "+row.GetProperty("actor").GetString()+" · "+row.GetProperty("action").GetString()+"\n대상: "+row.GetProperty("resourceId").GetString(),12));}
    }
}

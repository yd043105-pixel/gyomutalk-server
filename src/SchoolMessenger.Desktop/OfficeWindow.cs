using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow:Window
{
    readonly Api api;readonly StackPanel body=new(){Margin=new Thickness(24)};readonly TextBlock error=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20,10,20,12)};
    bool busy;JsonElement setup;readonly bool administration;
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    Window InteractionOwner=>Window.GetWindow(body)??this;
    public OfficeWindow(MainWindow main,bool administration=false):this(main,main.Api!,administration){}
    internal OfficeWindow(Window owner,Api source,bool administration,string page="administration")
    {
        api=source;this.administration=administration;Owner=owner;Resources=owner.Resources;Title=administration?"교직원 · 학교 운영 관리":"학사일정 · 특별실 예약";Width=700;Height=760;MinHeight=500;MinWidth=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;var dock=new DockPanel();DockPanel.SetDock(error,Dock.Bottom);dock.Children.Add(error);surface.Child=new ScrollViewer{Content=body};surface.Margin=new Thickness(administration?0:16);dock.Children.Add(surface);Content=dock;Page();var initialized=false;dock.Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(async()=>{if(!administration){await Load(false);return;}switch(page){case "staff":await StaffList();break;case "sections":await Sections();break;case "rooms":setup=await api.Get<JsonElement>("api/office/setup");await Rooms();break;case "calendar":await Load(false);break;case "audit":await Audit();break;default:await Administration();break;}});};
    }
    readonly Border surface=new(){CornerRadius=new CornerRadius(10)};
    // Every page starts here: most pages sit on a card, the member directory draws its own table and panel.
    void Page(bool card=true)
    {
        body.Children.Clear();body.Margin=new Thickness(card?24:0);surface.BorderThickness=new Thickness(card?1:0);
        if(card){surface.SetResourceReference(Border.BackgroundProperty,"Surface");surface.SetResourceReference(Border.BorderBrushProperty,"Line");}else surface.Background=null;
    }
    static TextBlock Text(string text,int size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,6)};
    void Button(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,4,0,8),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    TextBox Field(string label,string value=""){body.Children.Add(Text(label));var box=new TextBox{Text=value};body.Children.Add(box);return box;}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;error.Text="";try{await action();}catch(Exception e){error.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    static string Date(JsonElement row,string key)=>DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty(key).GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M/d HH:mm");
    async Task Load(bool bookings)
    {
        setup=await api.Get<JsonElement>("api/office/setup");Page();Button("학사력 · 학교 일정",()=>Load(false));Button("특별실 예약",()=>Load(true));if(administration&&api.Session!.User!.IsAdmin)Button("교직원 계정 · 운영 권한",Administration);
        body.Children.Add(Text(bookings?"특별실 예약":"학사력 · 행사 · 학교 일정",22));
        var date=new DatePicker{SelectedDate=DateTime.UtcNow.AddHours(9).Date};body.Children.Add(date);var list=new StackPanel();
        async Task Refresh()
        {
            list.Children.Clear();var selected=date.SelectedDate??DateTime.UtcNow.AddHours(9).Date;var from=bookings?selected.Date:new DateTime(selected.Year,selected.Month,1);var to=bookings?from.AddDays(1):from.AddMonths(1);
            var rows=await api.Get<JsonElement>("api/office/"+(bookings?"bookings":"events")+"?from="+from.ToString("yyyy-MM-dd")+"&to="+to.ToString("yyyy-MM-dd"));
            foreach(var row in rows.EnumerateArray())
            {
                list.Children.Add(Text(row.GetProperty("title").GetString()!,18));list.Children.Add(Text(Date(row,"start")+" → "+Date(row,"end")+" · "+row.GetProperty("ownerName").GetString(),12));list.Children.Add(Text(bookings?row.GetProperty("roomName").GetString()!:row.GetProperty("description").GetString()!));
                var allowed=bookings?row.GetProperty("ownerId").GetString()==api.Session!.User!.Id||setup.GetProperty("roomManager").GetBoolean():setup.GetProperty("calendarManager").GetBoolean();
                if(allowed)
                {
                    var edit=new Button{Content="변경",Margin=new Thickness(0,4,0,4)};edit.Click+=async(_,_)=>await Run(()=>Editor(bookings,row));list.Children.Add(edit);
                    var cancel=new Button{Content="취소",Margin=new Thickness(0,4,0,10)};cancel.Click+=async(_,_)=>await Run(async()=>{if(MessageBox.Show(InteractionOwner,"이 항목을 취소할까요?","취소 확인",MessageBoxButton.YesNo)==MessageBoxResult.Yes){await api.Send<JsonElement>(HttpMethod.Post,"api/office/"+(bookings?"bookings":"events")+"/"+row.GetProperty("id").GetString()+"/cancel",new OfficeRevision(row.GetProperty("revision").GetInt32()));await Refresh();}});list.Children.Add(cancel);
                }
            }
            if(rows.GetArrayLength()==0)list.Children.Add(Text(bookings?"선택한 날짜에는 예약이 없습니다.":"선택한 달에는 일정이 없습니다."));
        }
        Button(bookings?"날짜 조회":"선택한 달 조회",Refresh);
        if(bookings||setup.GetProperty("calendarManager").GetBoolean())Button(bookings?"특별실 예약하기":"학교 일정 등록",()=>Editor(bookings,null));
        if(bookings&&setup.GetProperty("roomManager").GetBoolean())Button("특별실 관리",Rooms);
        body.Children.Add(list);await Refresh();
    }
    Task Editor(bool booking,JsonElement? existing)
    {
        if(booking)return BookingEditor(existing);
        Page();Button("목록으로",()=>Load(booking));var title=Field("제목",existing?.GetProperty("title").GetString()??"");var description=booking?null:Field("행사·일정 설명",existing?.GetProperty("description").GetString()??"");
        var start=existing is{} a?DateTimeOffset.FromUnixTimeMilliseconds(a.GetProperty("start").GetInt64()).ToOffset(TimeSpan.FromHours(9)):DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).AddHours(1);
        var end=existing is{} b?DateTimeOffset.FromUnixTimeMilliseconds(b.GetProperty("end").GetInt64()).ToOffset(TimeSpan.FromHours(9)):start.AddHours(1);
        body.Children.Add(Text("시작 날짜 · 한국 시간"));var first=new DatePicker{SelectedDate=start.Date};body.Children.Add(first);var firstTime=Field("시작 시각 · HH:mm",start.ToString("HH:mm"));
        body.Children.Add(Text("종료 날짜"));var last=new DatePicker{SelectedDate=end.Date};body.Children.Add(last);var lastTime=Field("종료 시각 · HH:mm",end.ToString("HH:mm"));
        var allDay=new CheckBox{Content="종일 · 종료 날짜는 마지막 날의 다음 날",IsChecked=!booking&&existing is{} e&&e.GetProperty("allDay").GetBoolean(),Margin=new Thickness(0,12,0,12)};if(!booking)body.Children.Add(allDay);
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).ToArray();var choices=rooms.Select(r=>new Choice(r.GetProperty("id").GetString()!,r.GetProperty("name").GetString()!)).ToArray();var room=new ComboBox{ItemsSource=choices,SelectedIndex=choices.Length>0?0:-1};if(booking){body.Children.Add(Text("특별실"));body.Children.Add(room);if(existing is{} old)room.SelectedItem=choices.FirstOrDefault(r=>r.Id==old.GetProperty("roomId").GetString());}
        var client=booking&&existing is{} previous?previous.GetProperty("clientId").GetString()!:Guid.NewGuid().ToString("N");
        Button("저장",async()=>
        {
            if(first.SelectedDate is not{} from||last.SelectedDate is not{} to)throw new InvalidOperationException("날짜를 선택하세요.");
            if(!TimeSpan.TryParseExact(firstTime.Text,"hh\\:mm",CultureInfo.InvariantCulture,out var startTime)||!TimeSpan.TryParseExact(lastTime.Text,"hh\\:mm",CultureInfo.InvariantCulture,out var endTime))throw new InvalidOperationException("시간은 HH:mm 형식입니다.");
            var begin=new DateTimeOffset(DateTime.SpecifyKind(from.Date,DateTimeKind.Unspecified).Add(!booking&&allDay.IsChecked==true?TimeSpan.Zero:startTime),TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz");var finish=new DateTimeOffset(DateTime.SpecifyKind(to.Date,DateTimeKind.Unspecified).Add(!booking&&allDay.IsChecked==true?TimeSpan.Zero:endTime),TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz");var revision=existing?.GetProperty("revision").GetInt32()??0;
            if(booking&&room.SelectedItem is not Choice)throw new InvalidOperationException("관리자가 특별실을 먼저 등록해야 합니다.");
            object request=booking?new OfficeBookingRequest(client,(room.SelectedItem as Choice??throw new InvalidOperationException("특별실을 선택하세요.")).Id,title.Text,begin,finish,revision):new OfficeEventRequest(client,title.Text,description!.Text,begin,finish,allDay.IsChecked==true,revision);
            await api.Send<JsonElement>(HttpMethod.Post,"api/office/"+(booking?"bookings":"events")+(existing is{} old?"/"+old.GetProperty("id").GetString():""),request);await Load(booking);
        });return Task.CompletedTask;
    }
    Task Rooms()
    {
        Page(false);
        Heading("특별실","교사가 예약하는 특별실을 추가하고, 이름·위치와 예약 허용 여부를 바꿉니다.",null,Embedded?null:("예약 목록",()=>Load(true)));
        // Two inputs side by side on a card, like the section editor.
        Grid Pair(StackPanel form,string first,string firstValue,string second,string secondValue,out TextBox a,out TextBox b)
        {
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});grid.ColumnDefinitions.Add(new ColumnDefinition());
            TextBox Cell(string label,string value,int column){var cell=new StackPanel{Margin=new Thickness(0,0,0,12)};var caption=AdminUi.Label(label,"Muted",12);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);var box=new TextBox{Text=value};System.Windows.Automation.AutomationProperties.SetName(box,label);cell.Children.Add(box);Grid.SetColumn(cell,column);grid.Children.Add(cell);return box;}
            a=Cell(first,firstValue,0);b=Cell(second,secondValue,2);form.Children.Add(grid);return grid;
        }
        var add=new StackPanel();add.Children.Add(AdminUi.Group("새 특별실"));Pair(add,"특별실 이름","","위치","",out var name,out var location);
        var create=new Button{Content="특별실 추가",HorizontalAlignment=HorizontalAlignment.Left};create.SetResourceReference(StyleProperty,"PrimaryButton");create.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/office/rooms",new OfficeRoomRequest(name.Text,location.Text));setup=await api.Get<JsonElement>("api/office/setup");await Rooms();});add.Children.Add(create);
        var addCard=AdminUi.Card(add);addCard.Margin=new Thickness(0,0,0,16);body.Children.Add(addCard);
        var rooms=setup.GetProperty("rooms").EnumerateArray().ToArray();
        body.Children.Add(AdminUi.Group("등록된 특별실 "+rooms.Length+"곳"));
        foreach(var row in rooms)
        {
            var form=new StackPanel();Pair(form,"이름",row.GetProperty("name").GetString()!,"위치",row.GetProperty("location").GetString()!,out var roomName,out var roomLocation);roomName.MaxLength=80;roomLocation.MaxLength=100;
            var footer=new DockPanel();var save=new Button{Content="저장"};DockPanel.SetDock(save,Dock.Right);footer.Children.Add(save);
            var active=new CheckBox{Content="새 예약 받기 · 꺼도 지난 예약 기록은 남습니다",IsChecked=row.GetProperty("active").GetBoolean(),VerticalAlignment=VerticalAlignment.Center};footer.Children.Add(active);form.Children.Add(footer);
            save.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/office/rooms/"+row.GetProperty("id").GetString(),new OfficeRoomRequest(roomName.Text,roomLocation.Text,active.IsChecked==true,row.GetProperty("revision").GetInt32()));setup=await api.Get<JsonElement>("api/office/setup");await Rooms();});
            var card=AdminUi.Card(form);card.Margin=new Thickness(0,0,0,12);body.Children.Add(card);
        }
        if(rooms.Length==0){var none=AdminUi.Label("아직 등록한 특별실이 없습니다.","Muted",13);body.Children.Add(AdminUi.Card(none));}
        return Task.CompletedTask;
    }
}

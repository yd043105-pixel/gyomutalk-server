using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        api=source;this.administration=administration;Owner=owner;Resources=owner.Resources;Title=administration?"교직원 · 학교 운영 관리":"교무톡 · 학사일정 · 특별실";Width=960;Height=780;MinHeight=500;MinWidth=700;FontSize=13;Style=(Style)FindResource("SchoolWindow");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;var dock=new DockPanel();DockPanel.SetDock(error,Dock.Bottom);dock.Children.Add(error);surface.Child=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};dock.SetResourceReference(BackgroundProperty,"Surface");dock.Children.Add(surface);Content=dock;Page();var initialized=false;dock.Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(async()=>{if(!administration){await Load(false);return;}switch(page){case "staff":await StaffList();break;case "sections":await Sections();break;case "rooms":setup=await api.Get<JsonElement>("api/office/setup");await Rooms();break;case "calendar":await Load(false);break;case "audit":await Audit();break;default:await Administration();break;}});};
    }
    readonly Border surface=new(){CornerRadius=new CornerRadius(10)};
    // Every page starts here: most pages sit on a card, the member directory draws its own table and panel.
    void Page(bool card=true)
    {
        body.Children.Clear();body.Margin=new Thickness(card?24:0);surface.BorderThickness=new Thickness(card?1:0);
        if(card){surface.SetResourceReference(Border.BackgroundProperty,"Surface");surface.SetResourceReference(Border.BorderBrushProperty,"Line");}else surface.Background=null;
    }
    static TextBlock Text(string text,int size=14)=>new(){Text=text,FontSize=size,FontWeight=size>=20?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,6)};
    void Button(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,4,0,8),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    TextBox Field(string label,string value=""){body.Children.Add(Text(label));var box=new TextBox{Text=value};body.Children.Add(box);return box;}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;error.Text="";try{await action();}catch(Exception e){error.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    static string Date(JsonElement row,string key)=>DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty(key).GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M/d HH:mm");
    DateTime officeDate=DateTime.UtcNow.AddHours(9).Date;
    async Task Load(bool bookings)
    {
        setup=await api.Get<JsonElement>("api/office/setup");Page(false);body.Margin=new Thickness(Embedded?0:24,Embedded?0:16,Embedded?0:24,Embedded?0:16);
        var head=new DockPanel();var canAdd=bookings||setup.GetProperty("calendarManager").GetBoolean();
        if(canAdd){var add=new Button{Content=bookings?"특별실 예약하기":"학교 일정 등록"};add.SetResourceReference(StyleProperty,"PrimaryButton");add.Click+=async(_,_)=>await Run(()=>Editor(bookings,null));DockPanel.SetDock(add,Dock.Right);head.Children.Add(add);}
        if(bookings&&setup.GetProperty("roomManager").GetBoolean()){var rooms=new Button{Content="특별실 관리",Margin=new Thickness(0,0,8,0)};rooms.Click+=async(_,_)=>await Run(Rooms);DockPanel.SetDock(rooms,Dock.Right);head.Children.Add(rooms);}
        head.Children.Add(AdminUi.Heading("학사일정 · 특별실",22));body.Children.Add(head);
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(-4,4,0,0)};void Tab(string label,bool on,bool target){var t=new Button{Content=label,Tag=on?"selected":null};t.SetResourceReference(StyleProperty,"TabButton");t.Click+=async(_,_)=>await Run(()=>Load(target));tabs.Children.Add(t);}
        Tab("학사일정",!bookings,false);Tab("특별실 예약",bookings,true);body.Children.Add(tabs);var rule=new Border{Height=1,Margin=new Thickness(Embedded?0:-24,0,Embedded?0:-24,12)};rule.SetResourceReference(Border.BackgroundProperty,"Line");body.Children.Add(rule);
        var nav=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};body.Children.Add(nav);
        Button Step(string text,string name,Func<DateTime> next){var b=new Button{Content=text,Width=text.Length==1?32:double.NaN,FontSize=text.Length==1?18:13,Padding=text.Length==1?new Thickness(0):new Thickness(12,6,12,6),HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip=name};b.SetResourceReference(StyleProperty,"SubtleButton");System.Windows.Automation.AutomationProperties.SetName(b,name);b.Click+=async(_,_)=>await Run(async()=>{officeDate=next();await Load(bookings);});return b;}
        var title=AdminUi.Heading(bookings?officeDate.ToString("M월 d일 dddd",Korean):officeDate.ToString("yyyy년 M월",Korean));title.VerticalAlignment=VerticalAlignment.Center;title.Margin=new Thickness(4,0,4,0);
        nav.Children.Add(Step("‹","이전",()=>bookings?officeDate.AddDays(-1):officeDate.AddMonths(-1)));nav.Children.Add(title);nav.Children.Add(Step("›","다음",()=>bookings?officeDate.AddDays(1):officeDate.AddMonths(1)));nav.Children.Add(Step("오늘","오늘",()=>DateTime.UtcNow.AddHours(9).Date));
        if(bookings)await BookingGrid();else await MonthGrid();
    }
    static readonly CultureInfo Korean=CultureInfo.GetCultureInfo("ko-KR");
    static DateTime KoreaTime(JsonElement row,string key)=>DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty(key).GetInt64()).ToOffset(TimeSpan.FromHours(9)).DateTime;
    async Task MonthGrid()
    {
        var first=new DateTime(officeDate.Year,officeDate.Month,1);var rows=(await api.Get<JsonElement>("api/office/events?from="+first.ToString("yyyy-MM-dd")+"&to="+first.AddMonths(1).ToString("yyyy-MM-dd"))).EnumerateArray().ToArray();
        var manager=setup.GetProperty("calendarManager").GetBoolean();var today=DateTime.UtcNow.AddHours(9).Date;var start=first.AddDays(-(int)first.DayOfWeek);var weeks=(int)Math.Ceiling(((first.AddMonths(1)-start).TotalDays)/7);
        var grid=new Grid();for(var d=0;d<7;d++)grid.ColumnDefinitions.Add(new ColumnDefinition());grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});for(var w=0;w<weeks;w++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto,MinHeight=88});
        string[] names=["일","월","화","수","목","금","토"];for(var d=0;d<7;d++){var label=AdminUi.Label(names[d],d==0?"Danger":"Muted",12);label.HorizontalAlignment=HorizontalAlignment.Center;var cell=new Border{Child=label,Padding=new Thickness(0,8,0,8)};cell.SetResourceReference(Border.BackgroundProperty,"Subtle");Grid.SetColumn(cell,d);grid.Children.Add(cell);}
        for(var w=0;w<weeks;w++)for(var d=0;d<7;d++)
        {
            var date=start.AddDays(w*7+d);var inMonth=date.Month==first.Month;var stack=new StackPanel{Margin=new Thickness(8)};var cell=new Border{Child=stack,BorderThickness=new Thickness(0,1,0,0)};cell.SetResourceReference(Border.BorderBrushProperty,"Line");if(date==today)cell.SetResourceReference(Border.BackgroundProperty,"Pale");Grid.SetRow(cell,w+1);Grid.SetColumn(cell,d);grid.Children.Add(cell);
            if(!inMonth)continue;
            stack.Children.Add(AdminUi.Label(date.Day.ToString(),date==today?"Blue":d==0?"Danger":"Muted",12,date==today));
            foreach(var row in rows.Where(r=>{var from=KoreaTime(r,"start").Date;var end=KoreaTime(r,"end");var to=(end.TimeOfDay==TimeSpan.Zero&&end.Date>from?end.AddDays(-1):end).Date;return date>=from&&date<=to;}))
            {
                var text=new TextBlock{Text=row.GetProperty("title").GetString(),FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis};text.SetResourceReference(TextBlock.ForegroundProperty,"Blue");
                var tip=(row.GetProperty("allDay").GetBoolean()?"종일":Date(row,"start")+" ~ "+Date(row,"end"))+" · "+row.GetProperty("ownerName").GetString()+(row.GetProperty("description").GetString() is{Length:>0} about?"\n"+about:"");
                var chip=new Border{Child=text,Padding=new Thickness(4),Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(4),ToolTip=tip};chip.SetResourceReference(Border.BackgroundProperty,date==today?"Surface":"Pale");
                if(manager){chip.Cursor=System.Windows.Input.Cursors.Hand;var picked=row;chip.MouseLeftButtonUp+=async(_,_)=>await Run(()=>Editor(false,picked));}
                stack.Children.Add(chip);
            }
        }
        var frame=new Border{Child=grid,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true};frame.SetResourceReference(Border.BorderBrushProperty,"Line");body.Children.Add(frame);
        if(manager){var note=AdminUi.Label("일정을 누르면 변경하거나 취소할 수 있습니다.","Faint",11);note.Margin=new Thickness(0,8,0,0);body.Children.Add(note);}
    }
    async Task BookingGrid()
    {
        var rows=(await api.Get<JsonElement>("api/office/bookings?from="+officeDate.ToString("yyyy-MM-dd")+"&to="+officeDate.AddDays(1).ToString("yyyy-MM-dd"))).EnumerateArray().ToArray();
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).ToArray();
        if(rooms.Length==0){body.Children.Add(AdminUi.Empty("아직 등록된 특별실이 없습니다","관리자가 특별실을 등록하면 여기에서 예약할 수 있습니다."));return;}
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(120)});for(var p=1;p<=7;p++)grid.ColumnDefinitions.Add(new ColumnDefinition());for(var r=0;r<=rooms.Length;r++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        void Head(string text,int column){var label=AdminUi.Label(text,"Muted",12);label.HorizontalAlignment=column==0?HorizontalAlignment.Left:HorizontalAlignment.Center;var cell=new Border{Child=label,Padding=new Thickness(12,8,12,8)};cell.SetResourceReference(Border.BackgroundProperty,"Subtle");Grid.SetColumn(cell,column);grid.Children.Add(cell);}
        Head("특별실",0);for(var p=1;p<=7;p++)Head(p+"교시",p);
        var me=setup.GetProperty("userId").GetString();var manage=setup.GetProperty("roomManager").GetBoolean();
        for(var r=0;r<rooms.Length;r++)
        {
            var id=rooms[r].GetProperty("id").GetString()!;var name=AdminUi.Label(rooms[r].GetProperty("name").GetString()!,"Ink",13,true);var nameCell=new Border{Child=name,Padding=new Thickness(12,16,12,16),BorderThickness=new Thickness(0,1,0,0)};nameCell.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetRow(nameCell,r+1);grid.Children.Add(nameCell);
            for(var p=1;p<=7;p++)
            {
                var period=p;var booked=rows.Where(b=>b.GetProperty("roomId").GetString()==id).Where(b=>{var lessons=SchoolDay.Lessons(b.TryGetProperty("grade",out var g)&&g.ValueKind==JsonValueKind.Number&&g.GetInt32() is >=1 and <=3?g.GetInt32():1,7);var lesson=lessons.FirstOrDefault(l=>l.Number==period);if(lesson is null)return false;var from=KoreaTime(b,"start").ToString("HH:mm");var to=KoreaTime(b,"end").ToString("HH:mm");return string.CompareOrdinal(from,lesson.End)<0&&string.CompareOrdinal(to,lesson.Start)>0;}).Cast<JsonElement?>().FirstOrDefault();
                var mine=booked is{} own&&(own.GetProperty("ownerId").GetString()==me||manage);
                var cell=new Button{Content=booked is{} b2?b2.GetProperty("ownerName").GetString():"비어 있음",FontSize=11,FontWeight=FontWeights.Normal,Margin=new Thickness(4),Padding=new Thickness(2,10,2,10),BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Center,IsEnabled=booked is null||mine,ToolTip=booked is{} b3?b3.GetProperty("title").GetString()+" · "+b3.GetProperty("ownerName").GetString():"이 시간으로 예약"};
                if(booked is not null){cell.SetResourceReference(Control.BackgroundProperty,"Pale");cell.SetResourceReference(Control.ForegroundProperty,"Blue");}else{cell.Background=Brushes.Transparent;cell.SetResourceReference(Control.ForegroundProperty,"Faint");}
                System.Windows.Automation.AutomationProperties.SetName(cell,rooms[r].GetProperty("name").GetString()+" "+p+"교시 "+(booked is null?"비어 있음":"예약됨"));
                var found=booked;cell.Click+=async(_,_)=>await Run(()=>found is{} existing?BookingEditor(existing):BookingEditor(null,id,period,officeDate));
                var host=new Border{Child=cell,BorderThickness=new Thickness(0,1,0,0)};host.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetRow(host,r+1);Grid.SetColumn(host,p);grid.Children.Add(host);
            }
        }
        var frame=new Border{Child=grid,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true};frame.SetResourceReference(Border.BorderBrushProperty,"Line");body.Children.Add(frame);
        var note=AdminUi.Label("빈 칸을 누르면 그 시간으로 예약 창이 열립니다. 본인 예약을 누르면 변경하거나 취소할 수 있습니다.","Faint",11);note.Margin=new Thickness(0,8,0,0);body.Children.Add(note);
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
            TextBox Cell(string label,string value,int column){var cell=new StackPanel{Margin=new Thickness(0,0,0,12)};var caption=AdminUi.Label(label,"Muted",13);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);var box=new TextBox{Text=value};System.Windows.Automation.AutomationProperties.SetName(box,label);cell.Children.Add(box);Grid.SetColumn(cell,column);grid.Children.Add(cell);return box;}
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
        if(rooms.Length==0){var none=AdminUi.Label("아직 등록한 특별실이 없습니다.","Muted",14);body.Children.Add(AdminUi.Card(none));}
        return Task.CompletedTask;
    }
}

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
    readonly Func<string,bool> feature=_=>true;
    internal bool Administrative=>administration;
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    Window InteractionOwner=>Window.GetWindow(body)??this;
    public OfficeWindow(MainWindow main,bool administration=false):this(main,main.Api!,administration){}
    internal OfficeWindow(Window owner,Api source,bool administration,string page="administration")
    {
        api=source;this.administration=administration;if(owner is MainWindow main){main.RegisterWorkWindow(this);feature=main.Feature;}else Owner=owner;
        // The administrator pages are drawn with the administrator program's styles; opened from the teacher program they bring those styles along.
        if(administration&&owner is not LocalServerWindow){Resources=new ResourceDictionary();Resources.MergedDictionaries.Add(owner.Resources);Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/AdminTheme.xaml",UriKind.Relative)});}else Resources=owner.Resources;
        Title=administration?"교직원 · 학교 운영 관리":"교무톡 · 학사일정 · 특별실";Width=960;Height=780;MinHeight=500;MinWidth=700;FontSize=13;Style=(Style)FindResource("SchoolWindow");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;var dock=new DockPanel();DockPanel.SetDock(error,Dock.Bottom);dock.Children.Add(error);surface.Child=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};if(owner is LocalServerWindow)error.Margin=new Thickness(0,10,0,0);else dock.SetResourceReference(BackgroundProperty,administration?"Soft":"Surface");dock.Children.Add(surface);Content=dock;Page();var initialized=false;dock.Loaded+=async(_,_)=>{if(initialized)return;initialized=true;await Run(async()=>{if(!administration){await Load(false);return;}switch(page){case "staff":await StaffList();break;case "rooms":setup=await api.Get<JsonElement>("api/office/setup");await Rooms();break;case "calendar":await Load(false);break;case "audit":await Audit();break;default:await Administration();break;}});};
    }
    readonly Border surface=new(){CornerRadius=new CornerRadius(10)};
    // Every page starts here: most pages sit on a card, the member directory draws its own table and panel.
    void Page(bool card=true)
    {
        // A form keeps to a readable width on the left; a list or a table takes the page, with the same margin in every host.
        body.Children.Clear();column=null;body.Width=card?560:double.NaN;body.HorizontalAlignment=card?HorizontalAlignment.Left:HorizontalAlignment.Stretch;
        body.Margin=card?new Thickness(24):Embedded?new Thickness(0,0,6,0):new Thickness(24,16,24,16);surface.BorderThickness=new Thickness(card&&Embedded?1:0);
        if(card){surface.SetResourceReference(Border.BackgroundProperty,"Surface");surface.SetResourceReference(Border.BorderBrushProperty,"Line");}else surface.Background=null;
    }
    // 22 is a page title, 12 a note under a field, anything else the caption above a field.
    static TextBlock Text(string text,int size=14){var block=size>=20?AdminUi.Heading(text,22):AdminUi.Label(text,"Muted",12);block.Margin=size>=20?new Thickness(0,6,0,2):size==12?new Thickness(0,10,0,4):new Thickness(0,14,0,6);return block;}
    void Button(string label,Func<Task> action){var b=AdminUi.Tone(new Button{Content=label,Margin=new Thickness(0,4,0,8),HorizontalAlignment=HorizontalAlignment.Left},label);b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    // Fields go down the page, or side by side in the two halves of Pair.
    Panel? column;
    T Put<T>(string label,T input)where T:FrameworkElement{var panel=column??body;panel.Children.Add(Text(label));panel.Children.Add(input);System.Windows.Automation.AutomationProperties.SetName(input,label);return input;}
    TextBox Field(string label,string value="")=>Put(label,new TextBox{Text=value});
    void Pair(Action left,Action right)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});grid.ColumnDefinitions.Add(new ColumnDefinition());
        var a=new StackPanel();var b=new StackPanel();Grid.SetColumn(b,2);grid.Children.Add(a);grid.Children.Add(b);body.Children.Add(grid);column=a;left();column=b;right();column=null;
    }
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;error.Text="";try{await action();}catch(Exception e){error.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    static string Date(JsonElement row,string key)=>DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty(key).GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M/d HH:mm");
    DateTime officeDate=DateTime.UtcNow.AddHours(9).Date;
    async Task Load(bool bookings)
    {
        // A school that uses only the calendar or only the rooms gets that one page without tabs.
        var both=administration||feature("calendar")&&feature("rooms");if(!both)bookings=feature("rooms");
        setup=await api.Get<JsonElement>("api/office/setup");Page(false);if(!Embedded)body.Margin=new Thickness(24,16,24,16);
        var head=new DockPanel();var canAdd=bookings?setup.GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("active").GetBoolean()):setup.GetProperty("calendarManager").GetBoolean();
        if(canAdd){var add=new Button{Content=bookings?"특별실 예약하기":"학교 일정 등록",VerticalAlignment=VerticalAlignment.Bottom};add.SetResourceReference(StyleProperty,"PrimaryButton");add.Click+=async(_,_)=>await Run(()=>Editor(bookings,null));DockPanel.SetDock(add,Dock.Right);head.Children.Add(add);}
        if(bookings&&setup.GetProperty("roomManager").GetBoolean()){var rooms=new Button{Content="특별실 관리",Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Bottom};rooms.Click+=async(_,_)=>await Run(Rooms);DockPanel.SetDock(rooms,Dock.Right);head.Children.Add(rooms);}
        head.Children.Add(Embedded?AdminUi.PageTitle(bookings?"특별실 예약":"학사일정",bookings?"교직원이 예약한 특별실을 날짜별로 확인하고 조정합니다.":"학교 일정을 등록하고 바꿉니다. 교직원 메신저의 학사일정에 표시됩니다."):AdminUi.Heading(both?"학사일정 · 특별실":bookings?"특별실 예약":"학사일정",22));body.Children.Add(head);
        var rule=new Border{Height=1,Margin=new Thickness(Embedded?0:-24,0,Embedded?0:-24,12)};
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(-4,Embedded?10:4,0,0)};void Tab(string label,bool on,bool target){var t=new Button{Content=label,Tag=on?"selected":null};t.SetResourceReference(StyleProperty,"TabButton");t.Click+=async(_,_)=>await Run(()=>Load(target));tabs.Children.Add(t);}
        Tab("학사일정",!bookings,false);Tab("특별실 예약",bookings,true);if(both)body.Children.Add(tabs);else rule.Margin=new Thickness(Embedded?0:-24,10,Embedded?0:-24,12);rule.SetResourceReference(Border.BackgroundProperty,"Line");body.Children.Add(rule);
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
            var date=start.AddDays(w*7+d);var inMonth=date.Month==first.Month;var stack=new StackPanel{Margin=new Thickness(8)};var cell=new Border{Child=stack,BorderThickness=new Thickness(0,1,0,0)};cell.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetRow(cell,w+1);Grid.SetColumn(cell,d);grid.Children.Add(cell);
            if(!inMonth)continue;
            if(date==today){var number=AdminUi.Label(date.Day.ToString(),"OnAccent",12,true);number.HorizontalAlignment=HorizontalAlignment.Center;number.VerticalAlignment=VerticalAlignment.Center;
                var circle=new Border{Width=24,Height=24,CornerRadius=new CornerRadius(12),Child=number,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(-5,-4,0,0),ToolTip="오늘"};circle.SetResourceReference(Border.BackgroundProperty,"Blue");stack.Children.Add(circle);}
            else stack.Children.Add(AdminUi.Label(date.Day.ToString(),d==0?"Danger":d==6?"Blue":"Muted",12));
            foreach(var row in rows.Where(r=>{var from=KoreaTime(r,"start").Date;var end=KoreaTime(r,"end");var to=(end.TimeOfDay==TimeSpan.Zero&&end.Date>from?end.AddDays(-1):end).Date;return date>=from&&date<=to;}))
            {
                var title=row.GetProperty("title").GetString()??"";var text=new TextBlock{Text=title,FontSize=11,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=Avatar.Ink(title)};
                var tip=(row.GetProperty("allDay").GetBoolean()?"종일":Date(row,"start")+" ~ "+Date(row,"end"))+" · "+row.GetProperty("ownerName").GetString()+(row.GetProperty("description").GetString() is{Length:>0} about?"\n"+about:"");
                var chip=new Border{Child=text,Padding=new Thickness(6,3,6,3),Margin=new Thickness(0,4,0,0),CornerRadius=new CornerRadius(6),ToolTip=tip,Background=Avatar.Back(title)};
                if(manager){chip.Cursor=System.Windows.Input.Cursors.Hand;var picked=row;chip.MouseLeftButtonUp+=async(_,_)=>await Run(()=>Editor(false,picked));}
                stack.Children.Add(chip);
            }
        }
        var frame=new Border{Child=grid,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true};frame.SetResourceReference(Border.BorderBrushProperty,"Line");frame.SetResourceReference(Border.BackgroundProperty,"Surface");body.Children.Add(frame);
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
        var frame=new Border{Child=grid,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true};frame.SetResourceReference(Border.BorderBrushProperty,"Line");frame.SetResourceReference(Border.BackgroundProperty,"Surface");body.Children.Add(frame);
        var note=AdminUi.Label("빈 칸을 누르면 그 시간으로 예약 창이 열립니다. 본인 예약을 누르면 변경하거나 취소할 수 있습니다.","Faint",11);note.Margin=new Thickness(0,8,0,0);body.Children.Add(note);
    }
    Task Editor(bool booking,JsonElement? existing)
    {
        if(booking)return BookingEditor(existing);
        Page();Button("목록으로",()=>Load(false));body.Children.Add(Text(existing is null?"학교 일정 등록":"학교 일정 변경",22));
        var title=Field("제목",existing?.GetProperty("title").GetString()??"");title.MaxLength=200;var description=Field("설명 (선택)",existing?.GetProperty("description").GetString()??"");
        var whole=existing is{} e&&e.GetProperty("allDay").GetBoolean();
        var start=existing is{} a?DateTimeOffset.FromUnixTimeMilliseconds(a.GetProperty("start").GetInt64()).ToOffset(TimeSpan.FromHours(9)):DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).AddHours(1);
        var end=existing is{} b?DateTimeOffset.FromUnixTimeMilliseconds(b.GetProperty("end").GetInt64()).ToOffset(TimeSpan.FromHours(9)):start.AddHours(1);
        // An all-day event is stored up to the midnight after its last day; people pick the last day itself.
        var first=new DatePicker{SelectedDate=start.Date};var last=new DatePicker{SelectedDate=whole?end.Date.AddDays(-1):end.Date};TextBox firstTime=null!,lastTime=null!;
        Pair(()=>Put("시작 날짜",first),()=>firstTime=Field("시작 시각 (예: 09:00)",start.ToString("HH:mm")));
        Pair(()=>Put("끝나는 날짜",last),()=>lastTime=Field("끝나는 시각 (예: 16:30)",end.ToString("HH:mm")));
        var allDay=new CheckBox{Content="하루 종일 (시각 없이 날짜만)",IsChecked=whole,Margin=new Thickness(0,14,0,6)};body.Children.Add(allDay);
        void Times(){firstTime.IsEnabled=lastTime.IsEnabled=allDay.IsChecked!=true;}
        allDay.Click+=(_,_)=>Times();Times();
        var client=Guid.NewGuid().ToString("N");
        Button("저장",async()=>
        {
            if(first.SelectedDate is not{} from||last.SelectedDate is not{} to)throw new InvalidOperationException("날짜를 선택하세요.");
            var day=allDay.IsChecked==true;TimeSpan startTime=default,endTime=default;
            if(!day&&(!TimeSpan.TryParseExact(firstTime.Text.Trim(),"hh\\:mm",CultureInfo.InvariantCulture,out startTime)||!TimeSpan.TryParseExact(lastTime.Text.Trim(),"hh\\:mm",CultureInfo.InvariantCulture,out endTime)))throw new InvalidOperationException("시각은 09:00처럼 적어 주세요.");
            string Stamp(DateTime date,TimeSpan time)=>new DateTimeOffset(DateTime.SpecifyKind(date.Date,DateTimeKind.Unspecified).Add(time),TimeSpan.FromHours(9)).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
            var request=new OfficeEventRequest(client,title.Text,description.Text,Stamp(from,startTime),Stamp(day?to.AddDays(1):to,endTime),day,existing?.GetProperty("revision").GetInt32()??0);
            await api.Send<JsonElement>(HttpMethod.Post,"api/office/events"+(existing is{} old?"/"+old.GetProperty("id").GetString():""),request);await Load(false);
        });return Task.CompletedTask;
    }
    Task Rooms()
    {
        Page(false);
        Heading("특별실","교사가 예약하는 특별실을 추가하고, 이름·위치와 예약 허용 여부를 바꿉니다.",null,Embedded?null:("예약 목록",()=>Load(true)));
        static TextBox Box(string label,string value,int max){var box=new TextBox{Text=value,MaxLength=max};System.Windows.Automation.AutomationProperties.SetName(box,label);return box;}
        // New room: name, location and the button on one line.
        var add=new Grid();foreach(var width in new[]{new GridLength(1,GridUnitType.Star),new GridLength(12),new GridLength(1,GridUnitType.Star),new GridLength(12),GridLength.Auto})add.ColumnDefinitions.Add(new ColumnDefinition{Width=width});
        StackPanel Cell(string label,TextBox box,int column){var cell=new StackPanel();var caption=AdminUi.Label(label,"Muted",13);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);cell.Children.Add(box);Grid.SetColumn(cell,column);add.Children.Add(cell);return cell;}
        var name=Box("특별실 이름","",80);var location=Box("위치","",100);Cell("특별실 이름",name,0);Cell("위치 (선택)",location,2);
        var create=new Button{Content="특별실 추가",VerticalAlignment=VerticalAlignment.Bottom,MinHeight=34};create.SetResourceReference(StyleProperty,"PrimaryButton");Grid.SetColumn(create,4);add.Children.Add(create);
        create.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/office/rooms",new OfficeRoomRequest(name.Text,location.Text));setup=await api.Get<JsonElement>("api/office/setup");await Rooms();});
        var addPanel=new StackPanel();addPanel.Children.Add(AdminUi.Group("새 특별실"));addPanel.Children.Add(add);
        var addCard=AdminUi.Card(addPanel);addCard.Margin=new Thickness(0,0,0,16);body.Children.Add(addCard);
        // Registered rooms: one line each, edited in place.
        var rooms=setup.GetProperty("rooms").EnumerateArray().ToArray();
        if(rooms.Length==0){body.Children.Add(AdminUi.EmptyCard("아직 등록한 특별실이 없습니다","위에서 특별실을 추가하면 교직원이 예약할 수 있습니다."));return Task.CompletedTask;}
        Grid Columns(){var grid=new Grid();foreach(var width in new[]{new GridLength(1,GridUnitType.Star),new GridLength(12),new GridLength(1,GridUnitType.Star),new GridLength(12),new GridLength(120),new GridLength(12),new GridLength(64)})grid.ColumnDefinitions.Add(new ColumnDefinition{Width=width});return grid;}
        var table=new StackPanel();var labels=Columns();
        foreach(var (text,column) in new[]{("이름",0),("위치",2),("새 예약 받기",4)}){var label=AdminUi.Label(text,"Muted",12,true);Grid.SetColumn(label,column);labels.Children.Add(label);}
        var header=new Border{Child=labels,Padding=new Thickness(16,10,16,10),BorderThickness=new Thickness(0,0,0,1)};header.SetResourceReference(Border.BackgroundProperty,"Subtle");header.SetResourceReference(Border.BorderBrushProperty,"Line");table.Children.Add(header);
        for(var i=0;i<rooms.Length;i++)
        {
            var row=rooms[i];var line=Columns();var title=row.GetProperty("name").GetString()!;
            var roomName=Box(title+" 이름",title,80);line.Children.Add(roomName);
            var roomLocation=Box(title+" 위치",row.GetProperty("location").GetString()!,100);Grid.SetColumn(roomLocation,2);line.Children.Add(roomLocation);
            var active=new CheckBox{IsChecked=row.GetProperty("active").GetBoolean(),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left,BorderThickness=new Thickness(0),Padding=new Thickness(0),ToolTip="끄면 새 예약을 받지 않습니다. 지난 예약 기록은 남습니다."};
            if(Embedded)active.SetResourceReference(StyleProperty,"Switch");
            System.Windows.Automation.AutomationProperties.SetName(active,title+" 새 예약 받기");Grid.SetColumn(active,4);line.Children.Add(active);
            var save=new Button{Content="저장",VerticalAlignment=VerticalAlignment.Center};System.Windows.Automation.AutomationProperties.SetName(save,title+" 저장");Grid.SetColumn(save,6);line.Children.Add(save);
            save.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/office/rooms/"+row.GetProperty("id").GetString(),new OfficeRoomRequest(roomName.Text,roomLocation.Text,active.IsChecked==true,row.GetProperty("revision").GetInt32()));setup=await api.Get<JsonElement>("api/office/setup");await Rooms();error.Text=roomName.Text.Trim()+" 설정을 저장했습니다.";});
            var item=new Border{Child=line,Padding=new Thickness(16,10,16,10),BorderThickness=new Thickness(0,0,0,i==rooms.Length-1?0:1)};item.SetResourceReference(Border.BorderBrushProperty,"Line");table.Children.Add(item);
        }
        var count=AdminUi.Group("등록된 특별실 "+rooms.Length+"곳");body.Children.Add(count);
        var card=AdminUi.Card(table,0);card.ClipToBounds=true;body.Children.Add(card);
        var note=AdminUi.Label("이름이나 위치를 고친 뒤 그 줄의 ‘저장’을 누르세요.","Faint",12);note.Margin=new Thickness(0,8,0,0);body.Children.Add(note);
        return Task.CompletedTask;
    }
}

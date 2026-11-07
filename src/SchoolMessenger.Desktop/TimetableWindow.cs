using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class TimetableWindow:Window
{
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(16)};
    readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};

    readonly ComboBox scope=new(){MinWidth=220};
    bool busy;
    readonly ScrollViewer pageScroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly Border scheduleFooter = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(16,6,16,6), BorderThickness = new Thickness(0,1,0,0) };
    JsonElement setup;
    string? candidate,requestId;
    readonly Dictionary<string,ComboBox> classLinks=[],teacherLinks=[];
    DatePicker? start,end;
    public TimetableWindow(MainWindow main,string? selectedDate=null)
    { StatusTone.Watch(feedback);
        api=main.Api!;main.RegisterWorkWindow(this);Resources=main.Resources;Title="교무톡 · 시간표";Width=960;Height=740;MinWidth=700;MinHeight=520;FontSize=13;
        Style=(Style)FindResource("SchoolWindow");WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var page=new DockPanel();page.SetResourceReference(Panel.BackgroundProperty,"Surface");
        scheduleFooter.SetResourceReference(Border.BackgroundProperty,"Subtle");scheduleFooter.SetResourceReference(Border.BorderBrushProperty,"Line");
        DockPanel.SetDock(scheduleFooter,Dock.Bottom);page.Children.Add(scheduleFooter);pageScroll.Content=body;page.Children.Add(pageScroll);Content=page;
        if(DateTime.TryParse(selectedDate,out var day)){selected=day;weekly=false;}
        Loaded+=async(_,_)=>await Run(Load);
    }
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    static string TeacherLabel(JsonElement value)=>value.GetProperty("name").GetString()+" · "+value.GetProperty("department").GetString()+" · "+value.GetProperty("username").GetString();
    static TextBlock Text(string value,int size=14)=>new(){Text=value,FontSize=size,FontWeight=size>=20?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
    void Action(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,0,8,10),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=scheduleFooter.IsEnabled=false;feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;body.IsEnabled=scheduleFooter.IsEnabled=true;}}
    void ResetBody(){body.Children.Clear();scheduleFooter.Visibility=Visibility.Collapsed;scheduleFooter.Child=null;}
    DateTime selected=DateTime.UtcNow.AddHours(9).Date;bool weekly=true;StackPanel view=new();
    FrameworkElement Head()
    {
        var head=new StackPanel{Margin=new Thickness(0,0,0,8)};
        head.Children.Add(AdminUi.PageTitle("시간표","내 수업과 학급별 시간표"));
        var filters=new DockPanel{Margin=new Thickness(0,8,0,0)};
        scope.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(scope,Dock.Right);if(scope.Parent is Panel old)old.Children.Remove(scope);filters.Children.Add(scope);
        System.Windows.Automation.AutomationProperties.SetName(scope,"조회할 시간표");
        var row=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};filters.Children.Add(row);head.Children.Add(filters);
        var mode=new StackPanel{Orientation=Orientation.Horizontal};var pair=new Border{Child=mode,Padding=new Thickness(4),CornerRadius=new CornerRadius(6)};pair.SetResourceReference(Border.BackgroundProperty,"Soft");row.Children.Add(pair);
        Button Mode(string label,bool on,bool week){var b=new Button{Content=label,MinHeight=28,Padding=new Thickness(12,2,12,2),BorderThickness=new Thickness(on?1:0),Background=on?null:Brushes.Transparent};if(on)b.SetResourceReference(Control.BackgroundProperty,"Surface");else b.SetResourceReference(Control.ForegroundProperty,"Muted");b.Click+=async(_,_)=>await Run(async()=>{weekly=week;await Load();});return b;}
        mode.Children.Add(Mode("하루",!weekly,false));mode.Children.Add(Mode("한 주",weekly,true));
        Button Step(string label,int days,string name){var b=new Button{Content=label,Width=32,FontSize=18,Margin=new Thickness(4,0,0,0),Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Center,ToolTip=name};b.SetResourceReference(StyleProperty,"SubtleButton");b.Click+=async(_,_)=>await Run(async()=>{selected=selected.AddDays(days);await Load();});System.Windows.Automation.AutomationProperties.SetName(b,name);return b;}
        var monday=selected.AddDays(-((int)selected.DayOfWeek+6)%7);var when=weekly?monday.ToString("M.d")+" ~ "+monday.AddDays(4).ToString("M.d"):selected.ToString("M월 d일 dddd",Korean);
        var label=AdminUi.Label(when,"Ink",13,true);label.VerticalAlignment=VerticalAlignment.Center;label.Margin=new Thickness(4,0,4,0);
        row.Children.Add(Step("‹",weekly?-7:-1,"이전"));row.Children.Add(label);row.Children.Add(Step("›",weekly?7:1,"다음"));
        var today=new Button{Content="오늘",Margin=new Thickness(4,0,0,0)};today.SetResourceReference(StyleProperty,"SubtleButton");today.Click+=async(_,_)=>await Run(async()=>{selected=DateTime.UtcNow.AddHours(9).Date;await Load();});row.Children.Add(today);
        return head;
    }
    async Task Load()
    {
        setup=await api.Get<JsonElement>("api/timetable/setup");ResetBody();pageScroll.ScrollToTop();
        // A homeroom teacher's own class comes right after their own lessons.
        var ownClasses=setup.GetProperty("myClasses").EnumerateArray().Select(c=>c.GetString()).ToArray();var rooms=setup.GetProperty("classes").EnumerateArray().Select(r=>(Id:r.GetProperty("id").GetString()!,Name:r.GetProperty("name").GetString()!)).ToArray();
        var choices=new[]{new Choice("mine","내 수업")}.Concat(rooms.Where(r=>ownClasses.Contains(r.Id)).Select(r=>new Choice(r.Id,"우리 반 · "+r.Name))).Concat(rooms.Where(r=>!ownClasses.Contains(r.Id)).Select(r=>new Choice(r.Id,r.Name))).ToArray();
        var previous=(scope.SelectedItem as Choice)?.Id;scope.SelectionChanged-=ScopeChanged;scope.ItemsSource=choices;scope.SelectedItem=choices.FirstOrDefault(c=>c.Id==previous)??choices[0];scope.SelectionChanged+=ScopeChanged;
        body.Children.Add(Head());body.Children.Add(feedback);
        var notices=await api.Get<TimetableNotice[]>("api/timetable/notices");
        var unread=notices.Where(n=>n.ReadAt is null).OrderBy(n=>n.Date,StringComparer.Ordinal).ToArray();
        if(unread.Length>1){var all=new Button{Content="알림 모두 확인",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8)};all.SetResourceReference(StyleProperty,"SubtleButton");all.Click+=async(_,_)=>await Run(async()=>{foreach(var n in unread)await api.Send<JsonElement>(HttpMethod.Post,$"api/timetable/notices/{n.Revision}/read");await Load();});body.Children.Add(all);}
        foreach(var n in unread.Take(5))
        {
            var note=new Button{Content=n.Title,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8),ToolTip="누르면 그 날짜의 시간표를 봅니다."};note.SetResourceReference(Control.BackgroundProperty,"ChangeTint");note.SetResourceReference(Control.ForegroundProperty,"ChangeInk");note.BorderThickness=new Thickness(0);
            note.Click+=async(_,_)=>await Run(async()=>{selected=DateTime.Parse(n.Date);weekly=false;var choice=choices.FirstOrDefault(c=>n.ClassIds.Contains(c.Id));if(choice is not null)scope.SelectedItem=choice;await api.Send<JsonElement>(HttpMethod.Post,$"api/timetable/notices/{n.Revision}/read");await Load();});body.Children.Add(note);
        }
        var hasTimetable = await ShowTimetable();
        AddScheduleActions(hasTimetable);
        if(setup.GetProperty("canManage").GetBoolean()||setup.GetProperty("isAdmin").GetBoolean())
        {
            var tools=new WrapPanel{Margin=new Thickness(0,4,0,8)};body.Children.Add(tools);
            void Tool(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,0,4,0)};b.SetResourceReference(StyleProperty,"SubtleButton");b.Click+=async(_,_)=>await Run(action);tools.Children.Add(b);}
            if(CanManageSchedule){if(hasTimetable)Tool("시간표 올리기",Choose);Tool("게시 이력 · 취소",History);}
            if(setup.GetProperty("isAdmin").GetBoolean())Tool("시간표 담당자 · 담임 연결",Administration);
        }
        var pending=setup.GetProperty("pending").GetInt32();if(pending>0)body.Children.Add(AdminUi.Label("앱 전달 대기 "+pending+"건 · 외부 연결이 복구되면 재시도합니다.","Muted",12));
        if(setup.GetProperty("error").ValueKind==JsonValueKind.String)body.Children.Add(AdminUi.Label(setup.GetProperty("error").GetString()!,"Warning",12));
    }
    async void ScopeChanged(object sender,SelectionChangedEventArgs e){if(!busy)await Run(Load);}
    static readonly System.Globalization.CultureInfo Korean=System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
    async Task<bool> ShowTimetable()
    {
        if(view is not null)body.Children.Remove(view);view=new StackPanel();body.Children.Add(view);
        var choice=scope.SelectedItem as Choice??new Choice("mine","내 수업");var mine=choice.Id=="mine";
        var room=mine?default:setup.GetProperty("classes").EnumerateArray().FirstOrDefault(r=>r.GetProperty("id").GetString()==choice.Id);var grade=!mine&&room.ValueKind==JsonValueKind.Object?room.GetProperty("grade").GetInt32():1;
        var first=weekly?selected.AddDays(-((int)selected.DayOfWeek+6)%7):selected;var count=weekly?7:1;var days=new List<TimetableDay>();long? revision=null;
        for(var i=0;i<count;i++)
        {
            var day=await api.Get<TimetableDay>("api/timetable/day?date="+first.AddDays(i).ToString("yyyy-MM-dd")+(mine?"&mine=true":"&classId="+choice.Id));
            if(revision is not null&&revision!=day.Revision)throw new InvalidOperationException("조회 중 시간표가 갱신되었습니다. 다시 조회하세요.");revision=day.Revision;days.Add(day);
        }
        printDays = days.ToArray();
        printTitle = choice.Name + " 시간표";
        var lessons=days.SelectMany(d=>d.Slots).Where(s=>s.Subject.Length>0).ToArray();var published=days.Max(d=>d.PublishedAt);
        if (published == 0 && lessons.Length == 0)
        {
            Button? upload = null;
            if (CanManageSchedule)
            {
                upload = new Button { Content = "시간표 올리기" };
                upload.SetResourceReference(StyleProperty, "PrimaryButton");
                upload.Click += async (_, _) => await Run(Choose);
            }
            view.Children.Add(AdminUi.EmptyCard("아직 올라온 시간표가 없습니다",
                CanManageSchedule ? "나이스에서 받은 시간표 파일을 올리면 교직원이 여기서 볼 수 있습니다." : "시간표 담당자가 올리면 여기에 표시됩니다.", upload));
            return false;
        }
        if(days.Any(d=>d.MissingClasses.Length>0))view.Children.Add(AdminUi.Label("일부 학급의 적용 가능한 기초시간표가 없습니다.","Warning",12));
        var periods=SchoolDay.Lessons(grade is >=1 and <=3?grade:1,7).ToList();foreach(var extra in lessons.Select(s=>s.Period).Distinct().Where(p=>p>7).Order())periods.Add(SchoolDay.Lessons(grade is >=1 and <=3?grade:1).FirstOrDefault(p=>p.Number==extra)??new SchoolPeriod(extra,extra+"교시","","",true));
        TextBlock Lesson(TimetableSlot s,double size)
        {
            // The teacher sits right beside the subject, as in the NEIS tables; my own lessons say the class instead.
            var line=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=size};line.SetResourceReference(TextBlock.ForegroundProperty,s.Changed?"ChangeInk":"Blue");
            line.Inlines.Add(new System.Windows.Documents.Run(s.Subject){FontWeight=FontWeights.SemiBold});if(!mine&&s.TeacherName.Length>0)line.Inlines.Add(new System.Windows.Documents.Run(" "+string.Join('⁠',s.TeacherName.ToCharArray())){FontSize=size-1,FontWeight=FontWeights.Normal});return line;
        }
        string? Before(TimetableSlot s)=>s.Changed?"변경 전: "+(s.BeforeSubject.Length==0?"수업 없음":s.BeforeSubject+" · "+s.BeforeTeacher):null;
        if(weekly)
        {
            var shown=days.Select((d,i)=>(Day:d,Date:first.AddDays(i))).Where(x=>x.Date.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday)||x.Day.Slots.Any(s=>s.Subject.Length>0)).ToArray();
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(72)});foreach(var _ in shown)grid.ColumnDefinitions.Add(new ColumnDefinition());for(var r=0;r<=periods.Count;r++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var today=DateTime.UtcNow.AddHours(9).Date;
            void Place(UIElement e,int r,int c){Grid.SetRow(e,r);Grid.SetColumn(e,c);grid.Children.Add(e);}
            var corner=new Border{Padding=new Thickness(12,8,12,8)};corner.SetResourceReference(Border.BackgroundProperty,"Subtle");corner.Child=AdminUi.Label("교시","Muted",12);Place(corner,0,0);
            for(var c=0;c<shown.Length;c++)
            {
                var isToday=shown[c].Date==today;var head=AdminUi.Label(shown[c].Date.ToString("ddd M.d",Korean)+(isToday?" · 오늘":""),isToday?"Blue":"Muted",12,isToday);head.HorizontalAlignment=HorizontalAlignment.Center;
                var cell=new Border{Child=head,Padding=new Thickness(4,8,4,8)};cell.SetResourceReference(Border.BackgroundProperty,isToday?"Pale":"Subtle");Place(cell,0,c+1);
            }
            for(var r=0;r<periods.Count;r++)
            {
                var side=new StackPanel{Margin=new Thickness(10,2,6,2)};side.Children.Add(AdminUi.Label(periods[r].Label.Split(' ')[0],"Ink",12,true));if(periods[r].Start.Length>0)side.Children.Add(AdminUi.Label(periods[r].Start,"Muted",11));
                var sideCell=new Border{Child=side,BorderThickness=new Thickness(0,1,0,0)};sideCell.SetResourceReference(Border.BorderBrushProperty,"Line");Place(sideCell,r+1,0);
                for(var c=0;c<shown.Length;c++)
                {
                    var slots=shown[c].Day.Slots.Where(s=>s.Period==periods[r].Number&&s.Subject.Length>0).ToArray();var cell=new StackPanel{Margin=new Thickness(3,0,3,0)};
                    foreach(var s in slots)
                    {
                        var content=new StackPanel();content.Children.Add(Lesson(s,13));if(mine||s.Changed)content.Children.Add(AdminUi.Label((mine?s.ClassName:"")+(s.Changed?(mine?" · ":"")+"변경":""),s.Changed?"ChangeInk":"Muted",11));
                        var card=new Border{Child=content,Padding=new Thickness(5,0,5,0),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,0,1),ToolTip=Before(s)};card.SetResourceReference(Border.BackgroundProperty,s.Changed?"ChangeTint":"Pale");cell.Children.Add(card);
                    }
                    var host=new Border{Child=cell,BorderThickness=new Thickness(0,1,0,0),MinHeight=34};host.SetResourceReference(Border.BorderBrushProperty,"Line");if(shown[c].Date==today)host.SetResourceReference(Border.BackgroundProperty,"Subtle");Place(host,r+1,c+1);
                }
            }
            var frame=new Border{Child=grid,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),ClipToBounds=true};frame.SetResourceReference(Border.BorderBrushProperty,"Line");view.Children.Add(frame);
        }
        else
        {
            var today=days[0].Slots.Where(s=>s.Subject.Length>0).OrderBy(s=>s.Period).ToArray();
            if(today.Length==0)view.Children.Add(AdminUi.Empty(mine?"이 날은 내 수업이 없습니다":"이 날은 수업이 없습니다","다른 날짜로 이동하거나 한 주 보기로 확인하세요."));
            foreach(var s in today)
            {
                var period=periods.FirstOrDefault(p=>p.Number==s.Period);var row=new DockPanel();var left=new StackPanel{Width=112};left.Children.Add(AdminUi.Label(period?.Label.Split(' ')[0]??s.Period+"교시","Ink",13,true));if(period is{Start.Length:>0})left.Children.Add(AdminUi.Label(period.Start+" ~ "+period.End,"Faint",11));DockPanel.SetDock(left,Dock.Left);row.Children.Add(left);
                if(s.Changed){var changed=AdminUi.Label("변경","ChangeInk",12,true);changed.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(changed,Dock.Right);row.Children.Add(changed);}
                var middle=new StackPanel();middle.Children.Add(Lesson(s,13));if(mine)middle.Children.Add(AdminUi.Label(s.ClassName,"Muted",12));if(Before(s) is{} before)middle.Children.Add(AdminUi.Label(before,"ChangeInk",11));row.Children.Add(middle);
                var card=AdminUi.Card(row,16);card.Margin=new Thickness(0,0,0,8);if(s.Changed)card.SetResourceReference(Border.BackgroundProperty,"ChangeTint");view.Children.Add(card);
            }
            if(!mine&&grade is >=1 and <=3)view.Children.Add(AdminUi.Label(string.Join(" · ",SchoolDay.Schedule(grade).Where(p=>!p.Lesson).Select(p=>p.Label)),"Faint",11));
        }
        if(published>0){var note=AdminUi.Label("마지막 게시 "+DateTimeOffset.FromUnixTimeMilliseconds(published).ToOffset(TimeSpan.FromHours(9)).ToString("M.d HH:mm")+(mine&&weekly?" · 표시 시간은 학년별 일과에 따릅니다":""),"Faint",11);note.Margin=new Thickness(0,8,0,0);view.Children.Add(note);}
        return true;
    }
    readonly List<(string Name,JsonElement Value)> imports=[];
    public async Task Refresh(){if(view.Parent==body)await Run(Load);}
    Task Choose() => Choose([]);
    async Task Choose(string[] approvalIds)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="나이스에서 받은 시간표 파일 선택",Filter="시간표 엑셀 (*.xlsx)|*.xlsx",Multiselect=true};if(dialog.ShowDialog(this)!=true)return;
        if (dialog.FileNames.Length > 20)
            throw new InvalidOperationException("시간표 엑셀은 한 번에 20개까지 선택할 수 있습니다.");
        var selectedFiles = new List<(string Name, FileStream Stream)>();
        try
        {
            // Check every local file before creating any import drafts on the server.
            foreach (var path in dialog.FileNames)
                selectedFiles.Add((Path.GetFileName(path), OpenTimetableFile(path)));
            publishingSwaps = approvalIds;
            imports.Clear();
            foreach (var (name, stream) in selectedFiles)
            {
                feedback.Text = name + " 읽는 중…";
                using var form = new MultipartFormDataContent();
                form.Add(new StreamContent(stream), "file", name);
                try
                {
                    imports.Add((name, await api.Send<JsonElement>(HttpMethod.Post, "api/timetable/import", content: form)));
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(name + ": " + e.Message, e);
                }
            }
        }
        finally
        {
            foreach (var (_, stream) in selectedFiles) stream.Dispose();
        }
        feedback.Text="";candidate=null;requestId=null;teacherLinks.Clear();pageScroll.ScrollToTop();
        ResetBody();body.Children.Add(AdminUi.Heading("시간표 올리기",22));var intro=AdminUi.Label("올린 파일을 확인하세요. 기초시간표에서 달라지는 수업은 노란색으로 표시됩니다.");intro.Margin=new Thickness(0,8,0,16);body.Children.Add(intro);body.Children.Add(feedback);
        var files=new StackPanel();var hasBase=false;
        foreach(var (name,value) in imports)
        {
            var book=value.GetProperty("workbook");var kind=book.GetProperty("kind").GetString();var cells=book.GetProperty("cells").EnumerateArray().ToArray();hasBase|=kind=="base";
            var grades=string.Join("·",cells.Select(c=>c.GetProperty("grade").GetInt32()).Distinct().Order());var dates=cells.Select(c=>c.TryGetProperty("date",out var d)&&d.ValueKind==JsonValueKind.String?d.GetString()!:"").Where(d=>d.Length>0).Distinct().Order().ToArray();
            var range=dates.Length==0?"":dates.Length==1?dates[0]:dates[0]+" ~ "+dates[^1];
            var what=kind=="base"?"기초시간표":kind=="changes"?(cells.Length==0?"변경 수업 · 이번 파일에는 변경이 없습니다":"변경 수업 "+cells.Length+"건 · "+range):"강의실별 수업 · "+range;
            var row=new StackPanel{Margin=new Thickness(0,0,0,12)};row.Children.Add(AdminUi.Label(name,"Ink",13,true));row.Children.Add(AdminUi.Label((grades.Length>0?grades+"학년 · ":"")+what,"Muted",12));files.Children.Add(row);
        }
        body.Children.Add(AdminUi.Card(files,16));
        start=new DatePicker();end=new DatePicker();
        if(hasBase)
        {
            var first=imports.First(i=>i.Value.GetProperty("workbook").GetProperty("kind").GetString()=="base").Value.GetProperty("workbook");var year=first.GetProperty("year").GetInt32();var one=first.GetProperty("semester").GetInt32()==1;
            start.SelectedDate=new DateTime(year,one?3:8,1);end.SelectedDate=one?new DateTime(year,7,31):new DateTime(year+1,2,28);
            var dates=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,16,0,0)};var a=new StackPanel{Width=180,Margin=new Thickness(0,0,12,0)};a.Children.Add(AdminUi.Label("기초시간표 적용 시작일","Muted",12));a.Children.Add(start);var b=new StackPanel{Width=180};b.Children.Add(AdminUi.Label("적용 종료일","Muted",12));b.Children.Add(end);dates.Children.Add(a);dates.Children.Add(b);body.Children.Add(dates);
        }
        var choices=setup.GetProperty("teachers").EnumerateArray().Select(r=>new Choice(r.GetProperty("id").GetString()!,TeacherLabel(r))).ToArray();
        var people=imports.SelectMany(i=>i.Value.GetProperty("teachers").EnumerateArray()).DistinctBy(t=>t.GetProperty("source").GetString()).ToArray();
        var alike=people.Where(t=>t.GetProperty("candidates").GetArrayLength()>1).ToArray();
        if(alike.Length>0)
        {
            var pick=AdminUi.Group("이름이 같은 계정이 있어 직접 골라 주세요");pick.Margin=new Thickness(0,16,0,8);body.Children.Add(pick);
            foreach(var t in alike){var label=t.GetProperty("source").GetString()!;var ids=t.GetProperty("candidates").EnumerateArray().Select(x=>x.GetString()).ToArray();var box=new ComboBox{ItemsSource=choices.Where(c=>ids.Contains(c.Id)).ToArray(),Margin=new Thickness(0,4,0,8),Width=320,HorizontalAlignment=HorizontalAlignment.Left};teacherLinks.Add(label,box);body.Children.Add(AdminUi.Label(label,"Ink",13));body.Children.Add(box);}
        }
        var none=people.Where(t=>t.GetProperty("candidates").GetArrayLength()==0).Select(t=>t.GetProperty("source").GetString()).ToArray();
        if(none.Length>0){var note=AdminUi.Label($"아직 계정이 없는 선생님 {none.Length}명은 이름만 표시되고, 계정이 만들어지면 이름으로 연결됩니다: "+string.Join(", ",none),"Muted",12);note.Margin=new Thickness(0,16,0,0);body.Children.Add(note);}
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};body.Children.Add(actions);
        var cancel=new Button{Content="취소",Margin=new Thickness(0,0,8,0)};cancel.SetResourceReference(StyleProperty,"SubtleButton");cancel.Click+=async(_,_)=>await Run(Load);actions.Children.Add(cancel);
        var next=new Button{Content="변경 내용 확인"};next.SetResourceReference(StyleProperty,"PrimaryButton");next.Click+=async(_,_)=>await Run(Preview);actions.Children.Add(next);
    }
    static FileStream OpenTimetableFile(string path)
    {
        try
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} 파일을 읽을 수 없습니다. 엑셀이나 파일을 사용 중인 프로그램에서 닫은 뒤 다시 올려주세요.", e);
        }
    }
    async Task Preview()
    {
        var ids=imports.Select(i=>i.Value.GetProperty("draftId").GetString()!).ToArray();
        var links=teacherLinks.Where(c=>c.Value.SelectedItem is Choice).Select(c=>new TimetableLink(c.Key,((Choice)c.Value.SelectedItem).Id)).ToArray();
        var value=await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/preview",new TimetablePreviewRequest(ids[0],start?.SelectedDate?.ToString("yyyy-MM-dd")??"",end?.SelectedDate?.ToString("yyyy-MM-dd")??"",[],links,ids));
        candidate=value.GetProperty("candidateId").GetString();requestId=Guid.NewGuid().ToString("N");pageScroll.ScrollToTop();
        ResetBody();body.Children.Add(AdminUi.Heading(publishingSwaps.Length == 0 ? "게시 전 확인" : $"교체 신청 {publishingSwaps.Length}건 승인·게시",22));var count=value.GetProperty("changedCount").GetInt32();
        var description=count>0?$"달라지는 수업 {count}개 · 게시하면 관련 선생님과 담임 선생님께 알림이 갑니다."
            :publishingSwaps.Length>0?"지금 시간표와 달라지는 수업이 없습니다. 게시하면 관련 선생님께 승인 알림이 갑니다."
            :"지금 시간표와 달라지는 수업이 없습니다. 게시해도 알림은 가지 않습니다.";
        var intro=AdminUi.Label(description);intro.Margin=new Thickness(0,8,0,16);body.Children.Add(intro);body.Children.Add(feedback);
        var list=new StackPanel();var shown=0;
        foreach(var c in value.GetProperty("changes").EnumerateArray())
        {
            if(++shown>40)break;var line=new StackPanel{Margin=new Thickness(0,0,0,10)};string Lesson(string subject,string teacher)=>c.GetProperty(subject).GetString() is{Length:>0} s?s+"("+c.GetProperty(teacher).GetString()+")":"수업 없음";
            line.Children.Add(AdminUi.Label($"{c.GetProperty("date").GetString()} · {c.GetProperty("className").GetString()} {c.GetProperty("period")}교시","Ink",13,true));line.Children.Add(AdminUi.Label(Lesson("beforeSubject","beforeTeacher")+" → "+Lesson("subject","teacher"),"Muted",12));list.Children.Add(line);
        }
        if(count>40)list.Children.Add(AdminUi.Label($"외 {count-40}개","Muted",12));if(count>0)body.Children.Add(AdminUi.Card(list,16));
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};body.Children.Add(actions);
        var cancel=new Button{Content="취소",Margin=new Thickness(0,0,8,0)};cancel.SetResourceReference(StyleProperty,"SubtleButton");cancel.Click+=async(_,_)=>await Run(Load);actions.Children.Add(cancel);
        var publish=new Button{Content=publishingSwaps.Length == 0 ? "게시" : "승인·시간표 게시"};publish.SetResourceReference(StyleProperty,"PrimaryButton");publish.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/publish",new TimetableCommit(candidate!,requestId!,publishingSwaps));await Load();feedback.Text="게시했습니다.";});actions.Children.Add(publish);
    }
    async Task History()
    {
        var rows=await api.Get<JsonElement[]>("api/timetable/history");ResetBody();body.Children.Add(Text("시간표 게시 이력",24));body.Children.Add(feedback);Action("조회 화면으로",Load);
        foreach(var r in rows){body.Children.Add(Text($"#{r.GetProperty("revision")} {r.GetProperty("fileName").GetString()}\n{r.GetProperty("start").GetString()} ~ {r.GetProperty("end").GetString()}"));if(r.TryGetProperty("approvedSwapCount",out var approvals)&&approvals.GetInt32()>0)body.Children.Add(Text($"교체 신청 {approvals.GetInt32()}건 승인·게시",12));else if(!r.GetProperty("withdrawn").GetBoolean())Action("이 게시본 취소",async()=>{if(MessageBox.Show(this,"일자별 표 취소 시 기초시간표로 돌아갑니다. 취소할까요?","시간표 취소",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/"+r.GetProperty("id").GetString()+"/cancel",new TimetableCommit("",Guid.NewGuid().ToString("N")));await History();});}
    }
    async Task Administration()
    {
        var users=await api.Get<JsonElement[]>("api/admin/timetable-managers");ResetBody();body.Children.Add(Text("시간표 담당자 · 담임 연결",24));body.Children.Add(Text("담임은 시간표의 자율활동 담당 선생님으로 자동 연결됩니다. 다르면 여기에서 바꾸세요.",13));body.Children.Add(feedback);Action("조회 화면으로",Load);
        foreach(var user in users){var check=new CheckBox{Content=TeacherLabel(user)+" · 시간표 관리",IsChecked=user.GetProperty("enabled").GetBoolean(),IsEnabled=!user.GetProperty("isAdmin").GetBoolean(),Margin=new Thickness(0,0,0,8)};body.Children.Add(check);Action("이 담당자 권한 저장",()=>api.Send<JsonElement>(HttpMethod.Post,"api/admin/timetable-managers",new{userId=user.GetProperty("id").GetString(),enabled=check.IsChecked==true}));}
        var teachers=setup.GetProperty("teachers").EnumerateArray().Select(t=>new Choice(t.GetProperty("id").GetString()!,TeacherLabel(t))).ToArray();
        foreach(var room in setup.GetProperty("classes").EnumerateArray()){body.Children.Add(Text(room.GetProperty("name").GetString()+" 담임"));var box=new ComboBox{ItemsSource=teachers};var id=room.GetProperty("id").GetString();var home=setup.GetProperty("homerooms").EnumerateArray().FirstOrDefault(h=>h.GetProperty("classId").GetString()==id);if(home.ValueKind==JsonValueKind.Object)box.SelectedItem=teachers.FirstOrDefault(t=>t.Id==home.GetProperty("teacherId").GetString());body.Children.Add(box);Action("이 학급 담임 저장",async()=>{if(box.SelectedItem is not Choice teacher)throw new InvalidOperationException("담임을 선택하세요.");await api.Send<JsonElement>(HttpMethod.Post,"api/admin/timetable-homerooms",new{classId=id,teacherId=teacher.Id});feedback.Text="담임 연결을 저장했습니다.";});}
    }
}

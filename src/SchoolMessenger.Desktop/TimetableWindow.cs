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

public sealed class TimetableWindow:Window
{
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(24)};
    readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
    
    readonly ComboBox scope=new(){MinWidth=220};
    bool busy;
    JsonElement setup;
    string? draft,candidate,requestId;
    JsonElement imported;
    readonly Dictionary<string,ComboBox> classLinks=[],teacherLinks=[];
    DatePicker? start,end;
    public TimetableWindow(MainWindow main,string? selectedDate=null)
    { StatusTone.Watch(feedback);
        api=main.Api!;Owner=main;Resources=main.Resources;Title="교무톡 · 시간표";Width=960;Height=740;MinWidth=700;MinHeight=520;FontSize=13;
        Style=(Style)FindResource("SchoolWindow");WindowStartupLocation=WindowStartupLocation.CenterOwner;var page=new Border{Child=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};page.SetResourceReference(Border.BackgroundProperty,"Surface");Content=page;
        if(DateTime.TryParse(selectedDate,out var day)){selected=day;weekly=false;}
        Loaded+=async(_,_)=>await Run(Load);
    }
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    static string TeacherLabel(JsonElement value)=>value.GetProperty("name").GetString()+" · "+value.GetProperty("department").GetString()+" · "+value.GetProperty("username").GetString();
    static TextBlock Text(string value,int size=14)=>new(){Text=value,FontSize=size,FontWeight=size>=20?FontWeights.Bold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
    void Action(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,0,8,10),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    DateTime selected=DateTime.UtcNow.AddHours(9).Date;bool weekly=true;StackPanel view=new();
    FrameworkElement Head()
    {
        var head=new StackPanel{Margin=new Thickness(0,0,0,12)};var title=new DockPanel();
        scope.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(scope,Dock.Right);if(scope.Parent is Panel old)old.Children.Remove(scope);title.Children.Add(scope);title.Children.Add(AdminUi.Heading("시간표",22));head.Children.Add(title);
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};head.Children.Add(row);
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
        setup=await api.Get<JsonElement>("api/timetable/setup");body.Children.Clear();
        var choices=new[]{new Choice("mine","내 수업")}.Concat(setup.GetProperty("classes").EnumerateArray().Select(r=>new Choice(r.GetProperty("id").GetString()!,r.GetProperty("name").GetString()!))).ToArray();
        var previous=(scope.SelectedItem as Choice)?.Id;scope.SelectionChanged-=ScopeChanged;scope.ItemsSource=choices;scope.SelectedItem=choices.FirstOrDefault(c=>c.Id==previous)??choices[0];scope.SelectionChanged+=ScopeChanged;
        body.Children.Add(Head());body.Children.Add(feedback);
        var notices=await api.Get<TimetableNotice[]>("api/timetable/notices");
        foreach(var n in notices.Where(n=>n.ReadAt is null))
        {
            var note=new Button{Content=n.Title+" · "+n.Date,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,8),ToolTip="누르면 그 날짜의 시간표를 봅니다."};note.SetResourceReference(Control.BackgroundProperty,"WarningTint");note.SetResourceReference(Control.ForegroundProperty,"Warning");note.BorderThickness=new Thickness(0);
            note.Click+=async(_,_)=>await Run(async()=>{selected=DateTime.Parse(n.Date);weekly=false;var choice=choices.FirstOrDefault(c=>n.ClassIds.Contains(c.Id));if(choice is not null)scope.SelectedItem=choice;await api.Send<JsonElement>(HttpMethod.Post,$"api/timetable/notices/{n.Revision}/read");await Load();});body.Children.Add(note);
        }
        await Show();
        if(setup.GetProperty("canManage").GetBoolean()||setup.GetProperty("isAdmin").GetBoolean())
        {
            var tools=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(tools);
            void Tool(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,0,4,0)};b.SetResourceReference(StyleProperty,"SubtleButton");b.Click+=async(_,_)=>await Run(action);tools.Children.Add(b);}
            if(setup.GetProperty("canManage").GetBoolean()){Tool("기초시간표 올리기",()=>Choose("base"));Tool("일자별 시간표 올리기",()=>Choose("daily"));Tool("게시 이력 · 취소",History);}
            if(setup.GetProperty("isAdmin").GetBoolean())Tool("담당자 권한 · 담임 연결",Administration);
        }
        var pending=setup.GetProperty("pending").GetInt32();if(pending>0)body.Children.Add(AdminUi.Label("앱 전달 대기 "+pending+"건 · 외부 연결이 복구되면 재시도합니다.","Muted",12));
        if(setup.GetProperty("error").ValueKind==JsonValueKind.String)body.Children.Add(AdminUi.Label(setup.GetProperty("error").GetString()!,"Warning",12));
    }
    async void ScopeChanged(object sender,SelectionChangedEventArgs e){if(!busy)await Run(Load);}
    static readonly System.Globalization.CultureInfo Korean=System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
    async Task Show()
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
        var lessons=days.SelectMany(d=>d.Slots).Where(s=>s.Subject.Length>0).ToArray();var published=days.Max(d=>d.PublishedAt);
        if(published==0&&lessons.Length==0){view.Children.Add(AdminUi.Empty("아직 올라온 시간표가 없습니다","수업 담당자가 시간표를 올리면 여기에 표시됩니다."));return;}
        if(days.Any(d=>d.MissingClasses.Length>0))view.Children.Add(AdminUi.Label("일부 학급의 적용 가능한 기초시간표가 없습니다.","Warning",12));
        var periods=SchoolDay.Lessons(grade is >=1 and <=3?grade:1,7).ToList();foreach(var extra in lessons.Select(s=>s.Period).Distinct().Where(p=>p>7).Order())periods.Add(SchoolDay.Lessons(grade is >=1 and <=3?grade:1).FirstOrDefault(p=>p.Number==extra)??new SchoolPeriod(extra,extra+"교시","","",true));
        string Second(TimetableSlot s)=>mine?s.ClassName:s.TeacherName;
        string? Before(TimetableSlot s)=>s.Changed?"변경 전: "+(s.BeforeSubject.Length==0?"수업 없음":s.BeforeSubject+" · "+s.BeforeTeacher):null;
        if(weekly)
        {
            var shown=days.Select((d,i)=>(Day:d,Date:first.AddDays(i))).Where(x=>x.Date.DayOfWeek is not(DayOfWeek.Saturday or DayOfWeek.Sunday)||x.Day.Slots.Any(s=>s.Subject.Length>0)).ToArray();
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(88)});foreach(var _ in shown)grid.ColumnDefinitions.Add(new ColumnDefinition());for(var r=0;r<=periods.Count;r++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
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
                var side=new StackPanel{Margin=new Thickness(12,8,8,8)};side.Children.Add(AdminUi.Label(periods[r].Label.Split(' ')[0],"Ink",12,true));if(periods[r].Start.Length>0)side.Children.Add(AdminUi.Label(periods[r].Start,"Faint",11));
                var sideCell=new Border{Child=side,BorderThickness=new Thickness(0,1,0,0)};sideCell.SetResourceReference(Border.BorderBrushProperty,"Line");Place(sideCell,r+1,0);
                for(var c=0;c<shown.Length;c++)
                {
                    var slots=shown[c].Day.Slots.Where(s=>s.Period==periods[r].Number&&s.Subject.Length>0).ToArray();var cell=new StackPanel{Margin=new Thickness(4)};
                    foreach(var s in slots)
                    {
                        var content=new StackPanel();content.Children.Add(AdminUi.Label(s.Subject,s.Changed?"Warning":"Blue",13,true));content.Children.Add(AdminUi.Label(Second(s)+(s.Changed?" · 변경":""),"Muted",11));
                        var card=new Border{Child=content,Padding=new Thickness(8),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,0,0,2),ToolTip=Before(s)};card.SetResourceReference(Border.BackgroundProperty,s.Changed?"WarningTint":"Pale");cell.Children.Add(card);
                    }
                    var host=new Border{Child=cell,BorderThickness=new Thickness(0,1,0,0),MinHeight=60};host.SetResourceReference(Border.BorderBrushProperty,"Line");if(shown[c].Date==today)host.SetResourceReference(Border.BackgroundProperty,"Subtle");Place(host,r+1,c+1);
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
                if(s.Changed){var changed=AdminUi.Badge("변경","Warning",false);DockPanel.SetDock(changed,Dock.Right);row.Children.Add(changed);}
                var middle=new StackPanel();middle.Children.Add(AdminUi.Label(s.Subject+" · "+Second(s),"Ink",13,true));if(Before(s) is{} before)middle.Children.Add(AdminUi.Label(before,"Warning",11));row.Children.Add(middle);
                var card=AdminUi.Card(row,16);card.Margin=new Thickness(0,0,0,8);view.Children.Add(card);
            }
            if(!mine&&grade is >=1 and <=3)view.Children.Add(AdminUi.Label(string.Join(" · ",SchoolDay.Schedule(grade).Where(p=>!p.Lesson).Select(p=>p.Label)),"Faint",11));
        }
        if(published>0){var note=AdminUi.Label("마지막 게시 "+DateTimeOffset.FromUnixTimeMilliseconds(published).ToOffset(TimeSpan.FromHours(9)).ToString("M.d HH:mm")+(mine&&weekly?" · 표시 시간은 학년별 일과에 따릅니다":""),"Faint",11);note.Margin=new Thickness(0,8,0,0);view.Children.Add(note);}
    }
    async Task Choose(string kind)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Filter="시간표 엑셀 (*.xlsx)|*.xlsx",Multiselect=false};if(dialog.ShowDialog(this)!=true)return;
        using var form=new MultipartFormDataContent();form.Add(new StringContent(kind),"kind");form.Add(new StreamContent(File.OpenRead(dialog.FileName)),"file",Path.GetFileName(dialog.FileName));
        imported=await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/import",content:form);draft=imported.GetProperty("draftId").GetString();candidate=null;requestId=null;
        body.Children.Clear();body.Children.Add(Text("시간표 연결 확인",24));body.Children.Add(feedback);Action("조회 화면으로",Load);
        var book=imported.GetProperty("workbook");body.Children.Add(Text($"{book.GetProperty("year")}학년도 {book.GetProperty("semester")}학기 · {(kind=="base"?"기초시간표":book.GetProperty("date").GetString())}"));
        start=new DatePicker();end=new DatePicker();if(kind=="base"){body.Children.Add(Text("적용 시작일"));body.Children.Add(start);body.Children.Add(Text("적용 종료일"));body.Children.Add(end);}
        classLinks.Clear();teacherLinks.Clear();
        AddLinks("학급 연결",imported.GetProperty("classes"),setup.GetProperty("classes"),classLinks);
        AddLinks("교직원 계정 연결 · 동명이인은 직접 선택",imported.GetProperty("teachers"),setup.GetProperty("teachers"),teacherLinks);
        Action("변경 전후 미리보기",Preview);
    }
    void AddLinks(string heading,JsonElement sources,JsonElement options,Dictionary<string,ComboBox> links)
    {
        body.Children.Add(Text(heading,19));var choices=options.EnumerateArray().Select(r=>new Choice(r.GetProperty("id").GetString()!,r.TryGetProperty("department",out _)?TeacherLabel(r):r.GetProperty("name").GetString()!)).ToArray();
        foreach(var source in sources.EnumerateArray())
        {
            var label=source.GetProperty("source").GetString()!;body.Children.Add(Text(label));var box=new ComboBox{ItemsSource=choices,Margin=new Thickness(0,0,0,12)};
            var suggested=source.GetProperty("candidates").EnumerateArray().ToArray();if(suggested.Length==1)box.SelectedItem=choices.FirstOrDefault(c=>c.Id==suggested[0].GetString());links.Add(label,box);body.Children.Add(box);
        }
    }
    async Task Preview()
    {
        TimetableLink[] Links(Dictionary<string,ComboBox> controls)=>controls.Select(c=>new TimetableLink(c.Key,(c.Value.SelectedItem as Choice)?.Id??"")).ToArray();
        var value=await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/preview",new TimetablePreviewRequest(draft!,start?.SelectedDate?.ToString("yyyy-MM-dd")??"",end?.SelectedDate?.ToString("yyyy-MM-dd")??"",Links(classLinks),Links(teacherLinks)));
        candidate=value.GetProperty("candidateId").GetString();requestId=Guid.NewGuid().ToString("N");
        body.Children.Clear();body.Children.Add(Text("게시 전 변경 확인",24));body.Children.Add(feedback);var batch=value.GetProperty("batch");body.Children.Add(Text(batch.GetProperty("start").GetString()+" ~ "+batch.GetProperty("end").GetString()+" · 변경 "+value.GetProperty("changedCount")+"개"));
        foreach(var c in value.GetProperty("changes").EnumerateArray())body.Children.Add(Text($"{c.GetProperty("date").GetString()} · {c.GetProperty("className").GetString()} {c.GetProperty("period")}교시\n{c.GetProperty("beforeSubject").GetString()} ({c.GetProperty("beforeTeacher").GetString()}) → {c.GetProperty("subject").GetString()} ({c.GetProperty("teacher").GetString()})"));
        body.Children.Add(Text("일자별 표의 빈 교시는 수업 없음으로 반영됩니다. 게시 후 변경된 학급과 교사에게 알림이 등록됩니다."));
        Action("확인하고 게시",async()=>{if(MessageBox.Show(this,"이 시간표를 게시할까요?","시간표 게시",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/publish",new TimetableCommit(candidate!,requestId!));await Load();feedback.Text="게시했습니다. 앱 전달 상태를 확인하세요.";});Action("게시하지 않고 돌아가기",Load);
    }
    async Task History()
    {
        var rows=await api.Get<JsonElement[]>("api/timetable/history");body.Children.Clear();body.Children.Add(Text("시간표 게시 이력",24));body.Children.Add(feedback);Action("조회 화면으로",Load);
        foreach(var r in rows){body.Children.Add(Text($"#{r.GetProperty("revision")} {r.GetProperty("fileName").GetString()}\n{r.GetProperty("start").GetString()} ~ {r.GetProperty("end").GetString()}"));if(!r.GetProperty("withdrawn").GetBoolean())Action("이 게시본 취소",async()=>{if(MessageBox.Show(this,"일자별 표 취소 시 기초시간표로 돌아갑니다. 취소할까요?","시간표 취소",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/timetable/"+r.GetProperty("id").GetString()+"/cancel",new TimetableCommit("",Guid.NewGuid().ToString("N")));await History();});}
    }
    async Task Administration()
    {
        var users=await api.Get<JsonElement[]>("api/admin/timetable-managers");body.Children.Clear();body.Children.Add(Text("수업 담당자 권한 · 담임 연결",24));body.Children.Add(feedback);Action("조회 화면으로",Load);
        foreach(var user in users){var check=new CheckBox{Content=TeacherLabel(user)+" · 시간표 관리",IsChecked=user.GetProperty("enabled").GetBoolean(),IsEnabled=!user.GetProperty("isAdmin").GetBoolean(),Margin=new Thickness(0,0,0,8)};body.Children.Add(check);Action("이 담당자 권한 저장",()=>api.Send<JsonElement>(HttpMethod.Post,"api/admin/timetable-managers",new{userId=user.GetProperty("id").GetString(),enabled=check.IsChecked==true}));}
        var teachers=setup.GetProperty("teachers").EnumerateArray().Select(t=>new Choice(t.GetProperty("id").GetString()!,TeacherLabel(t))).ToArray();
        foreach(var room in setup.GetProperty("classes").EnumerateArray()){body.Children.Add(Text(room.GetProperty("name").GetString()+" 담임"));var box=new ComboBox{ItemsSource=teachers};var id=room.GetProperty("id").GetString();var home=setup.GetProperty("homerooms").EnumerateArray().FirstOrDefault(h=>h.GetProperty("classId").GetString()==id);if(home.ValueKind==JsonValueKind.Object)box.SelectedItem=teachers.FirstOrDefault(t=>t.Id==home.GetProperty("teacherId").GetString());body.Children.Add(box);Action("이 학급 담임 저장",async()=>{if(box.SelectedItem is not Choice teacher)throw new InvalidOperationException("담임을 선택하세요.");await api.Send<JsonElement>(HttpMethod.Post,"api/admin/timetable-homerooms",new{classId=id,teacherId=teacher.Id});feedback.Text="담임 연결을 저장했습니다.";});}
    }
}

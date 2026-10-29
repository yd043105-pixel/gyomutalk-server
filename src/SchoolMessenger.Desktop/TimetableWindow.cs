using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed class TimetableWindow:Window
{
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(24)};
    readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
    readonly DatePicker date=new(){SelectedDate=DateTime.UtcNow.AddHours(9).Date};
    readonly ComboBox scope=new(){MinWidth=220};
    bool busy;
    JsonElement setup;
    string? draft,candidate,requestId;
    JsonElement imported;
    readonly Dictionary<string,ComboBox> classLinks=[],teacherLinks=[];
    DatePicker? start,end;
    public TimetableWindow(MainWindow main,string? selectedDate=null)
    {
        api=main.Api!;Owner=main;Resources=main.Resources;Title="교무톡 · 시간표";Width=800;Height=780;MinWidth=650;MinHeight=520;
        Style=(Style)FindResource("SchoolWindow");WindowStartupLocation=WindowStartupLocation.CenterOwner;Content=new ScrollViewer{Content=body};
        if(DateTime.TryParse(selectedDate,out var selected))date.SelectedDate=selected;
        Loaded+=async(_,_)=>await Run(Load);
    }
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    static string TeacherLabel(JsonElement value)=>value.GetProperty("name").GetString()+" · "+value.GetProperty("department").GetString()+" · "+value.GetProperty("username").GetString();
    static TextBlock Text(string value,int size=14)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
    void Action(string label,Func<Task> action){var b=new Button{Content=label,Margin=new Thickness(0,0,8,10),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=async(_,_)=>await Run(action);body.Children.Add(b);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    async Task Load()
    {
        setup=await api.Get<JsonElement>("api/timetable/setup");body.Children.Clear();body.Children.Add(Text("오늘의 수업과 변경 시간표",24));body.Children.Add(feedback);body.Children.Add(date);
        var choices=new[]{new Choice("mine","내 수업")}.Concat(setup.GetProperty("classes").EnumerateArray().Select(r=>new Choice(r.GetProperty("id").GetString()!,r.GetProperty("name").GetString()!))).ToArray();
        scope.ItemsSource=choices;scope.SelectedIndex=0;body.Children.Add(scope);Action("선택한 날짜 보기",()=>Show(false));Action("선택한 주 보기",()=>Show(true));
        var notices=await api.Get<TimetableNotice[]>("api/timetable/notices");
        foreach(var n in notices.Where(n=>n.ReadAt is null))Action(n.Title+" · "+n.Date,async()=>{date.SelectedDate=DateTime.Parse(n.Date);var choice=choices.FirstOrDefault(c=>n.ClassIds.Contains(c.Id));if(choice is not null)scope.SelectedItem=choice;await Show(false);await api.Send<JsonElement>(HttpMethod.Post,$"api/timetable/notices/{n.Revision}/read");});
        if(setup.GetProperty("canManage").GetBoolean()){Action("학기 기초시간표 올리기",()=>Choose("base"));Action("일자별 시간표 올리기",()=>Choose("daily"));Action("게시 이력 · 취소",History);}
        if(setup.GetProperty("isAdmin").GetBoolean())Action("수업 담당자 권한 · 담임 연결",Administration);
        var pending=setup.GetProperty("pending").GetInt32();if(pending>0)body.Children.Add(Text("앱 전달 대기 "+pending+"건 · 외부 연결이 복구되면 재시도합니다."));
        if(setup.GetProperty("error").ValueKind==JsonValueKind.String)body.Children.Add(Text(setup.GetProperty("error").GetString()!));
        await Show(false);
    }
    StackPanel? view;
    async Task Show(bool weekly)
    {
        if(date.SelectedDate is not{} selected)throw new InvalidOperationException("조회할 날짜를 선택하세요.");
        if(view is not null)body.Children.Remove(view);view=new StackPanel{Margin=new Thickness(0,16,0,0)};body.Children.Add(view);
        var choice=scope.SelectedItem as Choice??new Choice("mine","내 수업");var first=weekly?selected.AddDays(-((int)selected.DayOfWeek+6)%7):selected;
        long? revision=null;
        for(var i=0;i<(weekly?7:1);i++)
        {
            var day=await api.Get<TimetableDay>("api/timetable/day?date="+first.AddDays(i).ToString("yyyy-MM-dd")+(choice.Id=="mine"?"&mine=true":"&classId="+choice.Id));
            if(revision is not null&&revision!=day.Revision)throw new InvalidOperationException("조회 중 시간표가 갱신되었습니다. 다시 조회하세요.");revision=day.Revision;
            view.Children.Add(Text(day.Date+" · "+first.AddDays(i).ToString("dddd"),19));
            if(choice.Id!="mine")
            {
                var room=setup.GetProperty("classes").EnumerateArray().FirstOrDefault(r=>r.GetProperty("id").GetString()==choice.Id);
                if(room.ValueKind==JsonValueKind.Object&&room.GetProperty("grade").GetInt32() is >=1 and <=3)
                    view.Children.Add(Text(string.Join("\n",SchoolDay.Schedule(room.GetProperty("grade").GetInt32()).Where(p=>!p.Lesson).Select(p=>p.Label)),12));
            }
            if(day.MissingClasses.Length>0)view.Children.Add(Text("일부 학급의 적용 가능한 기초시간표가 없습니다."));
            foreach(var s in day.Slots.Where(s=>s.Period<=10||s.Subject.Length>0))
            {
                var label=s.Subject.Length==0?"수업 없음":s.Subject+" · "+s.TeacherName;
                var room=setup.GetProperty("classes").EnumerateArray().FirstOrDefault(r=>r.GetProperty("id").GetString()==s.ClassId);var grade=room.ValueKind==JsonValueKind.Object?room.GetProperty("grade").GetInt32():0;
                view.Children.Add(Text($"{SchoolDay.Label(grade,s.Period)}  {s.ClassName}  {label}"+(s.Changed?$"\n변경 전: {(s.BeforeSubject.Length==0?"수업 없음":s.BeforeSubject+" · "+s.BeforeTeacher)}":"")));
            }
            if(day.Slots.Length==0)view.Children.Add(Text("등록된 내 수업이 없습니다."));
            if(day.PublishedAt>0)view.Children.Add(Text("마지막 게시: "+DateTimeOffset.FromUnixTimeMilliseconds(day.PublishedAt).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm"),12));
        }
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

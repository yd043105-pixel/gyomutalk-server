using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed class ElectiveWindow:Window
{
    readonly Api api;readonly StackPanel body=new(){Margin=new Thickness(22)};readonly TextBlock error=new(){Margin=new Thickness(20,8,20,12),TextWrapping=TextWrapping.Wrap};bool busy,isAdmin;
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    record Choice(string Id,string Name){public override string ToString()=>Name;}
    sealed record CourseEditor(string Id,TextBox Name,ComboBox Type,TextBox Credits,TextBox Area,ListBox Teachers);
    sealed record GroupEditor(string Id,TextBox Name,TextBox Required,List<CourseEditor> Courses);
    TextBox? currentTitle,currentTerm;List<GroupEditor> currentGroups=[];Dictionary<int,CheckBox> currentGrades=new();
    sealed record StudentRow(string ClassName,string Name,string Status,int Credits,string Courses);
    public ElectiveWindow(MainWindow main)
    {
        api=main.Api!;Owner=main;Resources=main.Resources;Title="교육과정 · 수강신청 관리";Width=960;Height=780;MinHeight=550;MinWidth=760;WindowStartupLocation=WindowStartupLocation.CenterOwner;Style=(Style)FindResource("SchoolWindow");
        var dock=new DockPanel();DockPanel.SetDock(error,Dock.Bottom);dock.Children.Add(error);dock.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=dock;Loaded+=async(_,_)=>await Run(List);
    }
    static TextBlock Text(string value,int size=14)=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,5)};
    static TextBox Field(Panel panel,string label,string value=""){panel.Children.Add(Text(label));var field=new TextBox{Text=value,MinWidth=100};panel.Children.Add(field);return field;}
    static TextBox[] Fields(Panel parent,params (string Label,string Value)[] values)
    {
        var row=new Grid();parent.Children.Add(row);var fields=new List<TextBox>();
        for(var i=0;i<values.Length;i++){row.ColumnDefinitions.Add(new ColumnDefinition());var panel=new StackPanel{Margin=new Thickness(i==0?0:6,0,i==values.Length-1?0:6,0)};Grid.SetColumn(panel,i);row.Children.Add(panel);fields.Add(Field(panel,values[i].Label,values[i].Value));}return fields.ToArray();
    }
    void Button(Panel panel,string title,Func<Task> action){var button=new Button{Content=title,Margin=new Thickness(0,5,8,5),HorizontalAlignment=HorizontalAlignment.Left};button.Click+=async(_,_)=>await Run(action);panel.Children.Add(button);}
    async Task Run(Func<Task> action){if(busy)return;busy=true;body.IsEnabled=false;error.Text="";try{await action();}catch(Exception e){error.Text=e.Message;}finally{busy=false;body.IsEnabled=true;}}
    public static string State(string value)=>value switch{"draft"=>"초안","open"=>"공개","closed"=>"마감","finalized"=>"분반 확정",_=>value};
    static string Time(long value)=>DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm");
    static long ParseTime(string text){if(!DateTime.TryParseExact(text.Trim(),"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw new InvalidOperationException("한국 시각으로 yyyy-MM-dd HH:mm 형식으로 입력하세요.");return new DateTimeOffset(DateTime.SpecifyKind(date,DateTimeKind.Unspecified),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();}
    async Task List()
    {
        var access=await api.Get<JsonElement>("api/elective-management/access");isAdmin=access.GetProperty("isAdmin").GetBoolean();body.Children.Clear();body.Children.Add(Text("다음 학기의 수업을 설계하세요",22));body.Children.Add(Text("공통과목 · 일반선택 · 진로선택 · 융합선택  /  정원 제한 없음"));
        if(isAdmin)Button(body,"수강신청 담당자 권한",Managers);
        if(!access.GetProperty("canManage").GetBoolean()){body.Children.Add(Text("시스템 관리자에게 수강신청 담당자 권한을 요청하세요."));return;}
        Button(body,"새 신청 회차",()=>Editor(null));Button(body,"새로고침",List);
        var rounds=await api.Get<ElectiveRound[]>("api/elective-management");
        if(rounds.Length==0)body.Children.Add(Text("아직 신청 회차가 없습니다. 새 회차에서 대상·기간·과목을 설정하세요."));
        foreach(var round in rounds){var card=new StackPanel{Margin=new Thickness(0,8,0,12)};card.Children.Add(Text(round.Title+" · "+round.Term,17));card.Children.Add(Text("대상 "+string.Join(" · ",round.Grades.Select(g=>g+"학년 전체"))));card.Children.Add(Text(State(round.State)+" · "+Time(round.StartsAt)+" ~ "+Time(round.EndsAt)+" (한국 시각)"));Button(card,"설정·신청 현황",()=>Detail(round.Id));body.Children.Add(card);}
    }
    async Task Managers()
    {
        var users=await api.Get<JsonElement[]>("api/admin/elective-managers");body.Children.Clear();Button(body,"회차 목록",List);body.Children.Add(Text("수강신청 업무 담당자",20));body.Children.Add(Text("지정한 담당자는 전체 회차 설정과 대상 학생의 신청 현황을 볼 수 있습니다."));
        foreach(var user in users){var enabled=user.GetProperty("enabled").GetBoolean();var id=user.GetProperty("id").GetString()!;var check=new CheckBox{Content=user.GetProperty("name").GetString()+" · "+user.GetProperty("department").GetString(),IsChecked=enabled,Margin=new Thickness(0,7,0,3),IsEnabled=!user.GetProperty("isAdmin").GetBoolean()};body.Children.Add(check);if(check.IsEnabled)Button(body,"이 교직원 권한 저장",async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/elective-managers",new ElectiveManagerChange(id,check.IsChecked==true));await Managers();});}
    }
    void ChooseTeachers(ListBox target,Button summary,Choice[] teachers)
    {
        var window=new Window{Owner=this,Title="과목 담당 교사",Width=410,Height=440,Resources=Resources,Style=(Style)FindResource("SchoolWindow"),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var dock=new DockPanel{Margin=new Thickness(18)};window.Content=dock;var label=Text("담당 교사를 선택하세요 · 여러 명 선택 가능",12);DockPanel.SetDock(label,Dock.Top);dock.Children.Add(label);
        var apply=new Button{Content="선택 적용",Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(apply,Dock.Bottom);dock.Children.Add(apply);
        var choices=new StackPanel();var picked=target.SelectedItems.Cast<Choice>().Select(t=>t.Id).ToHashSet();dock.Children.Add(new ScrollViewer{Content=choices});
        foreach(var teacher in teachers){var check=new CheckBox{Content=teacher.Name,IsChecked=picked.Contains(teacher.Id),Margin=new Thickness(0,6,0,6)};check.Checked+=(_,_)=>picked.Add(teacher.Id);check.Unchecked+=(_,_)=>picked.Remove(teacher.Id);choices.Children.Add(check);}
        apply.Click+=(_,_)=>{target.SelectedItems.Clear();foreach(var teacher in teachers.Where(t=>picked.Contains(t.Id)))target.SelectedItems.Add(teacher);summary.Content="교사 "+picked.Count+"명";window.Close();};window.ShowDialog();
    }
    async Task Editor(ElectiveRound? round)
    {
        var setup=await api.Get<JsonElement>("api/elective-management/setup");var grades=setup.GetProperty("grades").EnumerateArray().Select(g=>g.GetInt32()).ToArray();var teachers=setup.GetProperty("teachers").EnumerateArray().Select(t=>new Choice(t.GetProperty("id").GetString()!,t.GetProperty("name").GetString()!)).ToArray();
        body.Children.Clear();Button(body,"저장하지 않고 목록으로",List);body.Children.Add(Text(round is null?"수강신청 개설":"신청 초안 편집",23));body.Children.Add(Text("과목과 학점을 먼저 구성한 뒤 학생에게 공개하세요. 공개 후에는 이 설정을 변경할 수 없습니다.",12));
        var form=new StackPanel();body.Children.Add(form);var names=Fields(form,("회차명",round?.Title??""),("학년도·학기",round?.Term??(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Year+"학년도")));var title=names[0];var term=names[1];currentTitle=title;currentTerm=term;
        var tomorrow=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date.AddDays(1);
        var dates=Fields(form,("신청 시작 · 한국 시각 yyyy-MM-dd HH:mm",round is null?tomorrow.ToString("yyyy-MM-dd")+" 09:00":Time(round.StartsAt)),("신청 종료 · 한국 시각 yyyy-MM-dd HH:mm",round is null?tomorrow.AddDays(7).ToString("yyyy-MM-dd")+" 17:00":Time(round.EndsAt)));var start=dates[0];var end=dates[1];
        var limits=Fields(form,("최소 신청 학점 · 0은 제한 없음",(round?.MinCredits??0).ToString()),("최대 신청 학점 · 0은 제한 없음",(round?.MaxCredits??0).ToString()));
        form.Children.Add(Text("대상 학년"));var targets=new Dictionary<int,CheckBox>();var scopes=new WrapPanel();form.Children.Add(scopes);foreach(var grade in grades){var check=new CheckBox{Content=grade+"학년 전체",IsChecked=round?.Grades.Contains(grade)==true,Margin=new Thickness(0,5,20,5)};targets[grade]=check;scopes.Children.Add(check);}
        form.Children.Add(Text("현재 재학 학년 기준입니다. 선택한 학년의 모든 반이 대상이며, 3학년은 제외합니다.",11));
        var groups=new List<GroupEditor>();currentGroups=groups;currentGrades=targets;var groupPanel=new StackPanel();form.Children.Add(groupPanel);
        void AddGroup(ElectiveGroup? original)
        {
            var panel=new StackPanel{Margin=new Thickness(0,2,0,2)};var border=new Border{Padding=new Thickness(14),Margin=new Thickness(0,14,0,0),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Child=panel};border.SetResourceReference(Border.BorderBrushProperty,"Line");border.SetResourceReference(Border.BackgroundProperty,"Surface");groupPanel.Children.Add(border);
            var groupFields=Fields(panel,("선택 묶음 이름",original?.Name??("선택군 "+(groups.Count+1))),("학생이 선택할 과목 수 · 공통과목 제외",(original?.Required??1).ToString()));var name=groupFields[0];var required=groupFields[1];var courses=new List<CourseEditor>();var group=new GroupEditor(original?.Id??Guid.NewGuid().ToString("N"),name,required,courses);groups.Add(group);
            panel.Children.Add(Text("공통과목은 자동 포함합니다. 공통과목만 있는 묶음은 선택 개수를 0으로 설정하세요.",11));
            Grid Row(){var row=new Grid{Margin=new Thickness(0,5,0,0)};foreach(var width in new[]{2.3,1.5,.65,1.2,1.4,.5})row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(width,GridUnitType.Star)});return row;}
            var headings=Row();var labels=new[]{"과목명","과목 유형","학점","교과 · 선택","담당 교사",""};for(var i=0;i<labels.Length;i++){var label=Text(labels[i],11);Grid.SetColumn(label,i);headings.Children.Add(label);}panel.Children.Add(headings);
            var rows=new StackPanel();panel.Children.Add(rows);
            void AddCourse(ElectiveCourse? course)
            {
                var entry=Row();rows.Children.Add(entry);
                var courseName=new TextBox{Text=course?.Name??"",MaxLength=100};var type=new ComboBox{ItemsSource=ElectiveRules.Types.Select(t=>new Choice(t,ElectiveRules.TypeName(t))).ToArray(),SelectedValuePath="Id",SelectedValue=course?.Type??"general",FontSize=11};
                var credits=new TextBox{Text=course?.Credits>0?course.Credits.ToString():"",MaxLength=2,ToolTip="학교 교육과정에 맞는 학점을 입력하세요 · 1~20"};var area=new TextBox{Text=course?.Area??"",MaxLength=30};
                var select=new ListBox{ItemsSource=teachers,SelectionMode=SelectionMode.Multiple};foreach(var teacher in teachers.Where(t=>course?.TeacherIds.Contains(t.Id)==true))select.SelectedItems.Add(teacher);
                var choose=new Button{Content="교사 "+select.SelectedItems.Count+"명",FontSize=11,Padding=new Thickness(6),ToolTip="담당 교사 선택"};choose.Click+=(_,_)=>ChooseTeachers(select,choose,teachers);
                select.SelectionChanged+=(_,_)=>choose.Content="교사 "+select.SelectedItems.Count+"명";
                var remove=new Button{Content="×",ToolTip="과목 삭제",Padding=new Thickness(4)};
                var controls=new FrameworkElement[]{courseName,type,credits,area,choose,remove};for(var i=0;i<controls.Length;i++){controls[i].Margin=new Thickness(0,0,5,0);Grid.SetColumn(controls[i],i);entry.Children.Add(controls[i]);}
                var item=new CourseEditor(course?.Id??Guid.NewGuid().ToString("N"),courseName,type,credits,area,select);courses.Add(item);remove.Click+=(_,_)=>{courses.Remove(item);rows.Children.Remove(entry);};
            }
            if(original is null){AddCourse(null);AddCourse(null);}else foreach(var course in original.Courses)AddCourse(course);
            Button(panel,"과목 추가",()=>{AddCourse(null);return Task.CompletedTask;});Button(panel,"이 선택 묶음 제외",()=>{groups.Remove(group);groupPanel.Children.Remove(border);return Task.CompletedTask;});
        }
        if(round is null)AddGroup(null);else foreach(var group in round.Groups)AddGroup(group);Button(form,"선택 묶음 추가",()=>{AddGroup(null);return Task.CompletedTask;});
        ElectiveRoundRequest? pending=null;var client=Guid.NewGuid().ToString("N");
        Button(body,"초안 저장",async()=>
        {
            if(pending is null)
            {
                var choices=new List<ElectiveGroup>();foreach(var group in groups){if(!int.TryParse(group.Required.Text,out var count))throw new InvalidOperationException("선택 개수는 숫자로 입력하세요.");var subjects=new List<ElectiveCourse>();foreach(var c in group.Courses){if(!int.TryParse(c.Credits.Text,out var credit)||credit is <1 or >20)throw new InvalidOperationException("과목마다 1~20 범위의 학점을 입력하세요.");subjects.Add(new(c.Id,c.Name.Text.Trim(),c.Teachers.SelectedItems.Cast<Choice>().Select(t=>t.Id).ToArray(),c.Type.SelectedValue as string??"",credit,c.Area.Text.Trim()));}choices.Add(new(group.Id,group.Name.Text.Trim(),count,subjects.ToArray()));}
                if(!int.TryParse(limits[0].Text,out var minimum)||!int.TryParse(limits[1].Text,out var maximum))throw new InvalidOperationException("신청 학점 조건은 숫자로 입력하세요.");
                var selectedGrades=targets.Where(r=>r.Value.IsChecked==true).Select(r=>r.Key).ToArray();if(selectedGrades.Length==0)throw new InvalidOperationException("대상 학년을 선택하세요.");
                pending=new(client,title.Text.Trim(),term.Text.Trim(),selectedGrades,ParseTime(start.Text),ParseTime(end.Text),choices.ToArray(),round?.Revision??0,minimum,maximum);form.IsEnabled=false;
            }
            try{await api.Send<JsonElement>(HttpMethod.Post,"api/elective-management"+(round is null?"":"/"+round.Id),pending);await List();}
            catch(ApiRequestException e)when((int)e.StatusCode<500&&(int)e.StatusCode!=409){pending=null;form.IsEnabled=true;throw;}
        });
    }
    async Task Detail(string id)
    {
        var report=await api.Get<ElectiveReport>("api/elective-management/"+id);var round=report.Round;body.Children.Clear();Button(body,"회차 목록",List);Button(body,"현황 새로고침",()=>Detail(id));body.Children.Add(Text(round.Title,22));body.Children.Add(Text(round.Term+" · "+State(round.State)+" · 정원 제한 없음"));body.Children.Add(Text("대상 "+string.Join(" · ",round.Grades.Select(g=>g+"학년 전체"))));body.Children.Add(Text(Time(round.StartsAt)+" ~ "+Time(round.EndsAt)+" (한국 시각)"));
        body.Children.Add(Text($"신청 {report.Students.Count(s=>s.Submitted)} / {report.Students.Length}명   ·   미신청 {report.Students.Count(s=>!s.Submitted)}명",20));
        body.Children.Add(Text($"신청 학점: 최소 {round.MinCredits} · "+(round.MaxCredits==0?"상한 없음":$"최대 {round.MaxCredits}")+"  /  공통과목은 필수 포함",12));
        foreach(var group in round.Groups)
        {
            var heading=Text(group.Name+" · 선택과목 "+group.Required+"개",15);heading.FontWeight=FontWeights.SemiBold;body.Children.Add(heading);
            foreach(var course in group.Courses)body.Children.Add(Text(ElectiveRules.TypeName(course.Type)+"  |  "+course.Name+"  |  "+(course.Credits>0?course.Credits+"학점":"학점 미설정")+"  |  신청 "+report.Students.Count(s=>s.Submitted&&s.CourseIds.Contains(course.Id))+"명",12));
        }
        if(report.ExcludedApplications>0)body.Children.Add(Text("전학·학급 이동·계정 중지 등으로 현재 대상에서 제외된 기존 신청 "+report.ExcludedApplications+"건은 확정 명부에 포함되지 않습니다."));
        if(round.State=="draft"){Button(body,"초안 편집",()=>Editor(round));Transition("신청 공개","open","기간과 대상 학년을 확인했나요? 공개하면 과목·선택 규칙을 편집할 수 없습니다.");}
        if(round.State=="open")Transition("신청 마감","close","지금 마감하면 학생의 신청·변경·취소가 중단됩니다. 마감할까요?");
        if(round.State=="closed"||round.State=="open"&&round.EndsAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())Transition("신청 결과를 분반 명부로 확정","finalize","현재 유효한 신청 전체를 정원 제한 없이 새 분반 명부로 확정합니다. 미신청자는 자동 배정하지 않습니다. 진행할까요?");
        if(round.State=="finalized")body.Children.Add(Text("분반 "+report.Sections.Length+"개를 확정했습니다. 담당 교사는 기존 학교 공지에서 해당 분반을 선택할 수 있습니다."));
        var names=round.Groups.SelectMany(g=>g.Courses.Select(c=>(c.Id,Name:c.Name))).ToDictionary(c=>c.Id,c=>c.Name);
        body.Children.Add(Text("학생별 신청 현황 · 오른쪽 입력란에서 이름 검색",12));
        var filters=new Grid{Margin=new Thickness(0,10,0,10)};filters.ColumnDefinitions.Add(new ColumnDefinition());filters.ColumnDefinitions.Add(new ColumnDefinition());filters.ColumnDefinitions.Add(new ColumnDefinition());
        var status=new ComboBox{ItemsSource=new[]{"전체 학생","신청 완료","미신청"},SelectedIndex=0,Margin=new Thickness(0,0,8,0)};
        var classes=new[]{"전체 학급"}.Concat(report.Students.Select(s=>s.ClassName).Distinct()).ToArray();var roomFilter=new ComboBox{ItemsSource=classes,SelectedIndex=0,Margin=new Thickness(0,0,8,0)};
        var search=new TextBox{ToolTip="학생 이름 검색"};filters.Children.Add(status);Grid.SetColumn(roomFilter,1);filters.Children.Add(roomFilter);Grid.SetColumn(search,2);filters.Children.Add(search);body.Children.Add(filters);
        var table=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,Height=260,CanUserAddRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,SelectionMode=DataGridSelectionMode.Single,RowHeight=34,FontSize=12,Background=Brushes.White};
        foreach(var column in new[]{("학급","ClassName",120d),("이름","Name",110d),("신청 상태","Status",100d),("총 학점","Credits",80d),("선택 과목","Courses",0d)})table.Columns.Add(new DataGridTextColumn{Header=column.Item1,Binding=new System.Windows.Data.Binding(column.Item2),Width=column.Item3>0?new DataGridLength(column.Item3):new DataGridLength(1,DataGridLengthUnitType.Star),MinWidth=column.Item3>0?column.Item3:200});
        body.Children.Add(table);
        ElectiveStudent[] Filtered()=>report.Students.Where(s=>(status.SelectedIndex==0||(status.SelectedIndex==1)==s.Submitted)&&(roomFilter.SelectedIndex==0||s.ClassName==classes[roomFilter.SelectedIndex])&&s.Name.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        void Filter()=>table.ItemsSource=Filtered().Select(s=>new StudentRow(s.ClassName,s.Name,s.Submitted?"신청 완료":"미신청",s.Submitted?ElectiveRules.Credits(round.Groups,s.CourseIds):0,string.Join(" · ",s.CourseIds.Select(c=>names.GetValueOrDefault(c,"확인 필요"))))).ToArray();
        status.SelectionChanged+=(_,_)=>Filter();roomFilter.SelectionChanged+=(_,_)=>Filter();search.TextChanged+=(_,_)=>Filter();Filter();
        Button(body,"현재 표 CSV 저장 · 학급/신청 상태 필터 적용",()=>{Export(report with{Students=Filtered()});return Task.CompletedTask;});
        void Transition(string label,string action,string confirmation)
        {var request=new ElectiveTransition(Guid.NewGuid().ToString("N"),round.Revision,action);Button(body,label,async()=>{if(MessageBox.Show(this,confirmation,"수강신청",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<JsonElement>(HttpMethod.Post,"api/elective-management/"+id+"/state",request);await Detail(id);});}
    }
    static void Export(ElectiveReport report)
    {
        var picker=new SaveFileDialog{Filter="CSV 파일|*.csv",FileName="수강신청현황.csv"};if(picker.ShowDialog()!=true)return;
        static string Cell(string value){if(value.TrimStart().StartsWith('=')||value.TrimStart().StartsWith('+')||value.TrimStart().StartsWith('-')||value.TrimStart().StartsWith('@'))value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";}
        var names=report.Round.Groups.SelectMany(g=>g.Courses.Select(c=>(c.Id,Name:ElectiveRules.TypeName(c.Type)+" · "+c.Name+" ("+c.Credits+"학점)"))).ToDictionary(c=>c.Id,c=>c.Name);var rows=new List<string>{"학급,이름,상태,총학점,선택과목"};rows.AddRange(report.Students.Select(s=>string.Join(",",new[]{s.ClassName,s.Name,s.Submitted?"신청":"미신청",s.Submitted?ElectiveRules.Credits(report.Round.Groups,s.CourseIds).ToString():"0",string.Join(" / ",s.CourseIds.Select(c=>names.GetValueOrDefault(c,"확인 필요")))}.Select(Cell))));System.IO.File.WriteAllLines(picker.FileName,rows,new UTF8Encoding(true));
    }
    internal async Task<string[]> Verify(Action<Window,string> snapshot,string directory)
    {
        for(var n=0;n<150&&busy;n++)await Task.Delay(100);if(busy)throw new InvalidOperationException("수강신청 UI 로딩 시간 초과");
        await Run(()=>Editor(null));if(error.Text.Length>0)throw new InvalidOperationException(error.Text);
        if(currentTitle is null||currentGroups.Count==0)throw new InvalidOperationException("수강신청 과목 입력 화면 누락");
        var title="2027학년도 선택과목 신청 · 시연 "+Guid.NewGuid().ToString("N")[..4];currentTitle.Text=title;currentTerm!.Text="2027-1";currentGroups[0].Name.Text="탐구 선택";
        var subjects=currentGroups.Single().Courses;subjects[0].Name.Text="물리학";subjects[1].Name.Text="융합과학 탐구";subjects[0].Area.Text=subjects[1].Area.Text="과학";subjects[0].Type.SelectedValue="career";subjects[1].Type.SelectedValue="convergence";
        currentGrades.Values.First().IsChecked=true;foreach(var subject in subjects){subject.Teachers.SelectedIndex=0;subject.Credits.Text="3";}
        UpdateLayout();snapshot(this,System.IO.Path.Combine(directory,"elective-editor.png"));
        var save=body.Children.OfType<Button>().Single(b=>Equals(b.Content,"초안 저장"));save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        for(var n=0;n<150&&busy;n++)await Task.Delay(100);if(busy||error.Text.Length>0)throw new InvalidOperationException("수강신청 UI 저장 실패: "+error.Text);
        var created=(await api.Get<ElectiveRound[]>("api/elective-management")).Single(r=>r.Title==title);if(created.Groups.Single().Courses.Length!=2||!created.Grades.SequenceEqual(new[]{1}))throw new InvalidOperationException("수강신청 UI 저장 내용 불일치");
        await Run(()=>Detail(created.Id));if(error.Text.Length>0)throw new InvalidOperationException(error.Text);
        var grid=body.Children.OfType<DataGrid>().Single();var filters=body.Children.OfType<Grid>().Single();var status=filters.Children.OfType<ComboBox>().First();
        status.SelectedIndex=1;if(grid.Items.Count!=0)throw new InvalidOperationException("신청 완료 필터 오류");status.SelectedIndex=2;if(grid.Items.Count==0)throw new InvalidOperationException("미신청 학생 목록 누락");status.SelectedIndex=0;
        var search=filters.Children.OfType<TextBox>().Single();search.Text="없는 가상 학생 8a1f";if(grid.Items.Count!=0)throw new InvalidOperationException("이름 검색 오류");search.Clear();
        UpdateLayout();snapshot(this,System.IO.Path.Combine(directory,"elective-status.png"));
        if(created.Groups.Single().Courses.Any(c=>c.Credits!=3)||created.Groups.Single().Courses[1].Type!="convergence")throw new InvalidOperationException("과목 유형·학점 저장 실패");
        return ["Native curriculum editor configures course types, credits and teachers without capacities","Native draft save preserves course metadata through both authorized servers","Native status shows credit totals, applicants/non-applicants and filterable student table"];
    }
}

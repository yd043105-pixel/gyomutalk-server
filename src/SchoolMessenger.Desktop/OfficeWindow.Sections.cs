using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    // Elective sections group student accounts, which live only on the student-parent (school news) server.
    async Task Sections()
    {
        var directory=await api.Get<JsonElement>("api/admin/sections/setup");Page(false);
        if(!directory.GetProperty("connected").GetBoolean())
        {
            Heading("선택과목 분반","선택과목 분반을 만들고 수강 학생을 정하면, 교사가 분반 단위로 학생·학부모에게 안내를 보낼 수 있습니다.");
            var empty=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,MaxWidth=460,Margin=new Thickness(0,24,0,24)};
            var icon=AdminUi.Icon("AdminStudentIcon",28,"Faint");icon.Margin=new Thickness(0,0,0,12);empty.Children.Add(icon);
            var title=AdminUi.Heading("학생·학부모 소식 서버가 필요합니다",16);title.HorizontalAlignment=HorizontalAlignment.Center;empty.Children.Add(title);
            var detail=AdminUi.Label("학생 계정은 학생·학부모 소식 서버에만 있어서, 이 서버가 설치·실행되어 있어야 분반을 만들 수 있습니다. 학생·학부모 서비스를 쓰지 않는 학교라면 이 메뉴는 쓰지 않아도 됩니다.","Muted",14);detail.TextAlignment=TextAlignment.Center;detail.Margin=new Thickness(0,8,0,16);empty.Children.Add(detail);
            if(InteractionOwner is LocalServerWindow manager){var open=new Button{Content="학생·학부모 소식 서버 보기",HorizontalAlignment=HorizontalAlignment.Center};open.SetResourceReference(StyleProperty,"NewsButton");open.Click+=(_,_)=>manager.ShowServer("announcements");empty.Children.Add(open);}
            body.Children.Add(AdminUi.Card(empty,32));return;
        }
        var add=new Button{Content=AdminUi.IconLabel("AdminPlusIcon","분반 만들기")};add.SetResourceReference(StyleProperty,"PrimaryButton");AutomationProperties.SetName(add,"분반 만들기");add.Click+=async(_,_)=>await Run(()=>SectionEditor(directory,null));
        Heading("선택과목 분반 · 수강 명부","분반마다 담당 교사와 수강 학생을 정합니다. 교사는 분반 단위로 학생·학부모에게 안내를 보냅니다.",add);
        var rows=new StackPanel();var sections=directory.GetProperty("sections").EnumerateArray().ToArray();
        foreach(var section in sections)
        {
            var row=new DockPanel();var members=section.GetProperty("members").GetArrayLength();var teachers=section.GetProperty("teacherIds").GetArrayLength();
            var size=AdminUi.Badge(members==0?"수강생 없음":"수강생 "+members+"명",members==0?"Warning":"Neutral",false);size.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(size,Dock.Right);row.Children.Add(size);
            var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};text.Children.Add(AdminUi.Label(Value(section,"name"),"Ink",14,true));text.Children.Add(AdminUi.Label(Value(section,"term")+" · 담당 교사 "+teachers+"명","Muted",12));row.Children.Add(text);
            var line=new Button{Content=row};line.SetResourceReference(StyleProperty,"TableRow");AutomationProperties.SetName(line,Value(section,"name")+" 분반");line.Click+=async(_,_)=>await Run(()=>SectionEditor(directory,section));rows.Children.Add(line);
        }
        if(sections.Length==0){var none=AdminUi.Label("아직 만든 분반이 없습니다. ‘분반 만들기’로 시작하세요.","Muted",14);none.Margin=new Thickness(16,20,16,20);rows.Children.Add(none);}
        var card=AdminUi.Card(rows,0);card.ClipToBounds=true;body.Children.Add(card);
    }
    Task SectionEditor(JsonElement directory,JsonElement? existing)
    {
        Page(false);
        Heading(existing is{} current?Value(current,"name"):"새 분반","명부를 아직 받지 않았다면 학생 없이 분반만 먼저 만들 수 있습니다. 수강생이 0명인 분반에는 안내를 보낼 수 없습니다.",null,("분반 목록",Sections));
        var form=new StackPanel();
        var fields=new Grid();fields.ColumnDefinitions.Add(new ColumnDefinition());fields.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(12)});fields.ColumnDefinitions.Add(new ColumnDefinition());form.Children.Add(fields);
        TextBox Input(string label,string value,int column){var cell=new StackPanel{Margin=new Thickness(0,0,0,16)};var caption=AdminUi.Label(label,"Muted",13);caption.Margin=new Thickness(0,0,0,6);cell.Children.Add(caption);var box=new TextBox{Text=value};AutomationProperties.SetName(box,label);cell.Children.Add(box);Grid.SetColumn(cell,column);fields.Children.Add(cell);return box;}
        var name=Input("분반 이름",existing?.GetProperty("name").GetString()??"",0);var term=Input("학년도 · 학기",existing?.GetProperty("term").GetString()??DateTime.UtcNow.AddHours(9).Year+" 2학기",2);
        var selectedTeachers=existing?.GetProperty("teacherIds").EnumerateArray().Select(v=>v.GetString()).ToArray()??[];var selectedStudents=existing?.GetProperty("members").EnumerateArray().Select(v=>v.GetProperty("studentId").GetString()).ToArray()??[];
        Dictionary<string,CheckBox> Choices(string title,string role,string[] selected,Func<JsonElement,string> label)
        {
            var heading=AdminUi.Group(title);heading.Margin=new Thickness(0,4,0,8);form.Children.Add(heading);
            var grid=new UniformGrid{Columns=3,Margin=new Thickness(0,0,0,12)};var boxes=new Dictionary<string,CheckBox>();
            foreach(var person in directory.GetProperty("people").EnumerateArray().Where(p=>p.GetProperty("role").GetString()==role)){var id=person.GetProperty("id").GetString()!;var check=new CheckBox{Content=label(person),IsChecked=selected.Contains(id),Margin=new Thickness(0,3,8,3)};boxes[id]=check;grid.Children.Add(check);}
            if(boxes.Count==0)form.Children.Add(AdminUi.Label(role=="teacher"?"학생·학부모 서버에 연결된 교사 계정이 없습니다.":"승인된 학생 계정이 없습니다. ‘학생·학부모’에서 가입을 먼저 승인하세요.","Muted",13));else form.Children.Add(grid);
            return boxes;
        }
        var teachers=Choices("담당 교사","teacher",selectedTeachers,p=>p.GetProperty("name").GetString()!);
        var students=Choices("수강 학생 · 학교가 승인한 계정","student",selectedStudents,p=>p.GetProperty("name").GetString()+" · "+p.GetProperty("id").GetString());
        var actions=new DockPanel{Margin=new Thickness(0,8,0,0)};
        var save=new Button{Content="분반 · 수강 명부 저장",Margin=new Thickness(8,0,0,0)};save.SetResourceReference(StyleProperty,"PrimaryButton");DockPanel.SetDock(save,Dock.Right);actions.Children.Add(save);
        var import=new Button{Content="학생 ID 명부 불러오기"};var template=new Button{Content="명부 양식 저장",Margin=new Thickness(8,0,0,0)};template.SetResourceReference(StyleProperty,"SubtleButton");actions.Children.Add(import);actions.Children.Add(template);actions.Children.Add(new Border());form.Children.Add(actions);
        import.Click+=async(_,_)=>await Run(()=>
        {
            var open=new OpenFileDialog{Filter="학생 ID 명부|*.txt;*.csv"};if(open.ShowDialog(InteractionOwner)!=true)return Task.CompletedTask;
            var ids=File.ReadAllLines(open.FileName).Select(line=>line.Trim().Trim('"')).Where(line=>line.Length>0&&line!="studentId").ToArray();if(ids.Length>1000||ids.Any(id=>!students.ContainsKey(id))||ids.Distinct().Count()!=ids.Length)throw new InvalidOperationException("파일은 studentId 한 열 또는 학생 ID 한 줄씩이어야 합니다. 명부에 없는 ID나 중복이 있습니다.");
            foreach(var entry in students)entry.Value.IsChecked=ids.Contains(entry.Key);error.Text="수강생 "+ids.Length+"명을 선택했습니다. 저장 전 확인하세요.";return Task.CompletedTask;
        });
        template.Click+=async(_,_)=>await Run(()=>{var file=new SaveFileDialog{Filter="CSV|*.csv",FileName="분반-수강생-ID-양식.csv"};if(file.ShowDialog(InteractionOwner)==true)File.WriteAllText(file.FileName,"studentId\r\n");return Task.CompletedTask;});
        save.Click+=async(_,_)=>await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/sections"+(existing is{} section?"/"+section.GetProperty("id").GetString():""),new{name=name.Text,term=term.Text,teacherIds=teachers.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray(),studentIds=students.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray(),revision=existing?.GetProperty("revision").GetInt32()??0});await Sections();});
        body.Children.Add(AdminUi.Card(form));return Task.CompletedTask;
    }
}

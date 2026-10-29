using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    async Task Sections()
    {
        var directory=await api.Get<JsonElement>("api/admin/sections/setup");body.Children.Clear();Button("계정 관리로",Administration);
        if(!directory.GetProperty("connected").GetBoolean()){body.Children.Add(Text("외부 학교 소식 서버 연결이 필요합니다."));return;}
        body.Children.Add(Text("선택과목 분반 · 수강 명부",22));Button("분반 만들기",()=>SectionEditor(directory,null));
        foreach(var section in directory.GetProperty("sections").EnumerateArray())Button(section.GetProperty("term").GetString()+" · "+section.GetProperty("name").GetString()+" · "+section.GetProperty("members").GetArrayLength()+"명",()=>SectionEditor(directory,section));
    }
    Task SectionEditor(JsonElement directory,JsonElement? existing)
    {
        body.Children.Clear();Button("분반 목록으로",Sections);var name=Field("분반 이름",existing?.GetProperty("name").GetString()??"");var term=Field("학년도·학기",existing?.GetProperty("term").GetString()??DateTime.UtcNow.AddHours(9).Year+" 2학기");
        body.Children.Add(Text("명부를 아직 받지 않았다면 학생을 선택하지 않고 분반만 준비할 수 있습니다. 수강생 0명인 분반에는 발송할 수 없습니다."));
        var teachers=new Dictionary<string,CheckBox>();var students=new Dictionary<string,CheckBox>();
        var selectedTeachers=existing?.GetProperty("teacherIds").EnumerateArray().Select(v=>v.GetString()).ToArray()??[];var selectedStudents=existing?.GetProperty("members").EnumerateArray().Select(v=>v.GetProperty("studentId").GetString()).ToArray()??[];
        body.Children.Add(Text("담당 교사",18));foreach(var person in directory.GetProperty("people").EnumerateArray().Where(p=>p.GetProperty("role").GetString()=="teacher")){var id=person.GetProperty("id").GetString()!;var check=new CheckBox{Content=person.GetProperty("name").GetString(),IsChecked=selectedTeachers.Contains(id),Margin=new Thickness(0,3,0,3)};teachers[id]=check;body.Children.Add(check);}
        Button("학생 ID 명부 불러오기 · 텍스트/CSV",()=>
        {
            var open=new OpenFileDialog{Filter="학생 ID 명부|*.txt;*.csv"};if(open.ShowDialog(InteractionOwner)!=true)return Task.CompletedTask;
            var ids=File.ReadAllLines(open.FileName).Select(line=>line.Trim().Trim('"')).Where(line=>line.Length>0&&line!="studentId").ToArray();if(ids.Length>1000||ids.Any(id=>!students.ContainsKey(id))||ids.Distinct().Count()!=ids.Length)throw new InvalidOperationException("파일은 studentId 한 열 또는 학생 ID 한 줄씩이어야 합니다. 명부에 없는 ID나 중복이 있습니다.");
            foreach(var entry in students)entry.Value.IsChecked=ids.Contains(entry.Key);error.Text="수강생 "+ids.Length+"명을 선택했습니다. 저장 전 확인하세요.";return Task.CompletedTask;
        });
        Button("학생 ID 명부 양식 저장",()=>{var save=new SaveFileDialog{Filter="CSV|*.csv",FileName="분반-수강생-ID-양식.csv"};if(save.ShowDialog(InteractionOwner)==true)File.WriteAllText(save.FileName,"studentId\r\n");return Task.CompletedTask;});
        body.Children.Add(Text("수강 학생 · 학교 승인 계정",18));foreach(var person in directory.GetProperty("people").EnumerateArray().Where(p=>p.GetProperty("role").GetString()=="student")){var id=person.GetProperty("id").GetString()!;var check=new CheckBox{Content=person.GetProperty("name").GetString()+" · "+id,IsChecked=selectedStudents.Contains(id),Margin=new Thickness(0,3,0,3)};students[id]=check;body.Children.Add(check);}
        Button("분반 · 수강 명부 저장",async()=>{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/sections"+(existing is{} section?"/"+section.GetProperty("id").GetString():""),new{name=name.Text,term=term.Text,teacherIds=teachers.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray(),studentIds=students.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray(),revision=existing?.GetProperty("revision").GetInt32()??0});await Sections();});return Task.CompletedTask;
    }
}

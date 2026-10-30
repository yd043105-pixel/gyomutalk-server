using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    (SimpleSetup Setup,string Thumbprint,string Review) PrepareSimple(ServerCard c)
    {
        var school=c.School.Text.Trim();var admin=c.AdminUsername.Text.Trim();
        if(school.Length is <2 or >60||school.Any(char.IsControl))throw new InvalidOperationException("학교 이름을 2~60자로 입력하세요.");
        if(!Regex.IsMatch(admin,"^[A-Za-z0-9._-]{3,32}$"))throw new InvalidOperationException("관리자 아이디는 영문·숫자·._- 3~32자로 입력하세요.");
        if(c.ApprovalSha256.Length==0||c.ReviewedAt is not{} reviewed||!File.Exists(c.Approval.Text))throw new InvalidOperationException("학교장 승인(내부결재) 문서를 선택하세요.");
        if(reviewed.AddYears(1)<=DateTimeOffset.Now)throw new InvalidOperationException("승인 기록이 1년이 지나 만료되었습니다. 올해 승인 문서를 다시 선택하세요.");
        if(c.Backup.Text.Trim().Length==0)c.Backup.Text=SimpleOperation.DefaultBackup(c.SimpleData.Text,c.Service);
        SimpleOperation.EnsurePortFree(SimpleOperation.Port(c.Service));
        Directory.CreateDirectory(c.SimpleData.Text);Directory.CreateDirectory(c.Backup.Text);
        if(c.SchoolId.Length==0)c.SchoolId=SimpleOperation.NewSchoolId();
        var setup=new SimpleSetup(c.Service,school,admin,c.SimpleData.Text,c.Backup.Text,c.Approval.Text,c.ApprovalSha256,reviewed,c.SchoolId);
        var (thumbprint,fingerprint)=SimpleOperation.Certificate(directory,c.Service);c.Fingerprint=fingerprint;
        // This program made the certificate, so its own administrator sign-in trusts it without a code check.
        var port=SimpleOperation.Port(c.Service);foreach(var address in SimpleOperation.Addresses(c.Service).Append("https://localhost:"+port).Append("https://127.0.0.1:"+port))CertificatePins.Trust(address,fingerprint);
        SimpleOperation.EnableDiscovery(c.SimpleData.Text,c.Service);
        SaveSettings();
        return (setup,thumbprint,SimpleOperation.WriteReview(directory,setup));
    }
    void ChooseApproval(ServerCard c)
    {
        var dialog=new OpenFileDialog{Title="학교장 승인(내부결재) 문서 선택",Filter="승인 문서 (*.pdf;*.hwp;*.hwpx;*.png;*.jpg)|*.pdf;*.hwp;*.hwpx;*.png;*.jpg|모든 파일 (*.*)|*.*",CheckFileExists=true};
        if(dialog.ShowDialog(this)!=true)return;
        try{var (path,hash)=SimpleOperation.KeepApproval(directory,c.Service,dialog.FileName);c.Approval.Text=path;c.ApprovalSha256=hash;c.ReviewedAt=DateTimeOffset.Now;SaveSettings();Refresh();}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){MessageBox.Show(this,e.Message,"승인 문서");}
    }
    void ActivateService(ServerCard c)
    {
        if(c.Service=="announcements"&&MessageBox.Show(this,"학생·학부모 소식 서버를 이 PC에서 사용할까요?\n\n학부모 휴대폰처럼 학교 밖에서 접속하려면 외부 접속(Cloudflare Tunnel 등) 설정이 추가로 필요합니다. 가능하면 교내 업무 서버와 다른 PC에서 운영하세요.","학생·학부모 소식 서버 설치",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        c.Activated=true;SaveSettings();SelectCard(c);Refresh();
    }
    void BuildChecklist(ServerCard c)
    {
        var items=new List<(bool Done,string Title,string Detail,string? Action,Action? Run)>();
        if(c.ReviewedAt is{} reviewed){var expires=reviewed.AddYears(1);var fresh=expires>DateTimeOffset.Now.AddDays(30);items.Add((fresh,"학교장 승인 문서",expires.ToString("yyyy년 M월 d일")+"까지 유효 · 이 PC에만 보관",fresh?null:"새 문서",()=>ChooseApproval(c)));}
        else items.Add((false,"학교장 승인 문서","학교장 승인(내부결재) 문서를 선택하세요.","문서 선택",()=>ChooseApproval(c)));
        items.Add((true,"HTTPS 인증서",c.Fingerprint.Length>0?"이 서버 전용 인증서 · 연결 확인 코드 "+CertificatePins.Code(c.Fingerprint):"처음 시작할 때 자동으로 만들어집니다.",null,null));
        items.Add((true,"교사 PC 자동검색","켜짐 · 교직원망에서 UDP 45983이 허용되어야 합니다.",null,null));
        var backup=c.Backup.Text.Trim().Length>0?c.Backup.Text:SimpleOperation.DefaultBackup(c.SimpleData.Text,c.Service);var same=SimpleOperation.SameDrive(c.SimpleData.Text,backup);
        items.Add((!same,"백업 위치",same?"서버와 같은 드라이브에 있습니다. 외장 드라이브를 권장합니다.":backup,"위치 변경",()=>ChooseBackup(c)));
        items.Add((c.Ups.IsChecked==true,"정전 대비(UPS)",c.Ups.IsChecked==true?"UPS 설치·시험을 확인했습니다.":"정전 때 서버 PC가 안전하게 꺼지도록 UPS를 연결하세요.",c.Ups.IsChecked==true?null:"확인함",()=>{c.Ups.IsChecked=true;SaveSettings();Refresh();}));
        items.Add((true,"Windows 방화벽","설치할 때 같은 학교망(로컬 서브넷)의 접속만 허용했습니다.",null,null));
        // Rebuilt only when something changed so a click is never lost to the 3-second refresh.
        var key=string.Join("|",items.Select(i=>i.Done+i.Title+i.Detail));if(key==c.ChecklistKey)return;c.ChecklistKey=key;
        c.Checklist.Children.Clear();var open=items.Count(i=>!i.Done);AdminUi.SetBadge(c.ChecklistBadge,open==0?"모두 확인됨":open+"건 확인 필요",open==0?"Success":"Warning");
        for(var i=0;i<items.Count;i++)
        {
            var item=items[i];var row=new DockPanel();var line=new Border{Child=row,Padding=new Thickness(0,10,0,10),BorderThickness=new Thickness(0,0,0,i==items.Count-1?0:1)};line.SetResourceReference(Border.BorderBrushProperty,"Line");
            var mark=new Border{Width=24,Height=24,CornerRadius=new CornerRadius(12),Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Top,Child=AdminUi.Icon(item.Done?"AdminCheckIcon":"AdminAlertIcon",item.Done?13:14,item.Done?"Success":"Warning")};mark.SetResourceReference(Border.BackgroundProperty,item.Done?"SuccessTint":"WarningTint");row.Children.Add(mark);
            if(item.Action is{} label&&item.Run is{} run){var action=new Button{Content=label,VerticalAlignment=VerticalAlignment.Center};action.SetResourceReference(StyleProperty,"SubtleButton");action.Click+=(_,_)=>run();DockPanel.SetDock(action,Dock.Right);row.Children.Add(action);}
            var text=new StackPanel();text.Children.Add(AdminUi.Label(item.Title,"Ink",13,true));var detail=AdminUi.Label(item.Detail,"Muted",11);detail.Margin=new Thickness(0,2,0,0);text.Children.Add(detail);row.Children.Add(text);
            c.Checklist.Children.Add(line);
        }
    }
    void ChooseBackup(ServerCard c)
    {
        var dialog=new OpenFolderDialog{Title="백업 위치 선택 · 외장 드라이브 권장"};if(dialog.ShowDialog(this)!=true)return;
        c.Backup.Text=Path.Combine(dialog.FolderName,"GyomutalkBackup",c.Service);SaveSettings();Refresh();
    }
}

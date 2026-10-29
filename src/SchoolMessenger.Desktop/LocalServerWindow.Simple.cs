using System;
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
        c.Checklist.Children.Clear();
        void Item(bool done,string text){var line=Text((done?"✓  ":"!  ")+text,12);if(done)line.SetResourceReference(TextBlock.ForegroundProperty,"Ink");else line.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA5,0x3A,0x38));c.Checklist.Children.Add(line);}
        if(c.ReviewedAt is{} reviewed){var expires=reviewed.AddYears(1);Item(expires>DateTimeOffset.Now.AddDays(30),"학교장 승인 기록 · "+expires.ToString("yyyy-MM-dd")+"까지 유효");}else Item(false,"학교장 승인(내부결재) 문서를 선택하세요.");
        var backup=c.Backup.Text.Trim().Length>0?c.Backup.Text:SimpleOperation.DefaultBackup(c.SimpleData.Text,c.Service);
        Item(!SimpleOperation.SameDrive(c.SimpleData.Text,backup),SimpleOperation.SameDrive(c.SimpleData.Text,backup)?"백업이 서버와 같은 드라이브에 있습니다. 외장 드라이브를 연결한 뒤 ‘자세한 설정’에서 백업 위치를 바꾸세요.":"백업 위치 · "+backup);
        Item(c.Ups.IsChecked==true,"정전 대비 UPS 확인");
        Item(true,"교사 PC 자동검색 · 교직원망에서 UDP 45983이 허용되어야 합니다.");
        Item(false,"처음 서버를 시작할 때 Windows 방화벽 창이 뜨면 ‘개인 네트워크’에 허용하세요.");
        Item(false,"오류·장애 알림 메일은 선택 사항입니다. 필요하면 운영 안내의 ‘알림 메일’을 참고하세요.");
    }
}

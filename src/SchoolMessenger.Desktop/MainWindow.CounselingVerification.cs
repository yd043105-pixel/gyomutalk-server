using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    async Task VerifyCounselingUi()
    {
        var output=Environment.GetEnvironmentVariable("YY_COUNSELING_UI_OUTPUT")??Path.Combine(LocalDirectory,"counseling-checks");Directory.CreateDirectory(output);
        try
        {
            ServerAddress.Text=Environment.GetEnvironmentVariable("SCHOOL_UI_SERVER")??"";
            if(!Uri.TryCreate(ServerAddress.Text,UriKind.Absolute,out var address)||!address.IsLoopback||address.Scheme!="http")throw new InvalidOperationException("상담 화면 검사는 로컬 가상 서버만 사용합니다.");
            File.WriteAllText(Path.Combine(LocalDirectory,"update-seen.txt"),SchoolMessenger.Shared.ReleaseNotice.Load("desktop").Sequence.ToString());
            UsernameBox.Text="admin";PasswordBox.Password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")??"";await SignIn();
            foreach(var owned in OwnedWindows.Cast<Window>().ToArray())owned.Close();
            var window=new CounselingWindow(this);window.Show();
            for(var i=0;i<100&&!window.Ready;i++)await Task.Delay(50);
            if(!window.Ready)throw new InvalidOperationException("상담 화면을 불러오지 못했습니다.");
            var day=AdminControls<DatePicker>(window).First();day.SelectedDate=DateTime.Parse(Environment.GetEnvironmentVariable("YY_COUNSELING_DATE")!);
            AdminControls<System.Windows.Controls.Button>(window).Single(b=>b.Content?.ToString()=="선택한 날짜 조회 · 새로고침").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for(var i=0;i<100&&!window.Ready;i++)await Task.Delay(50);
            if(!window.Ready||!AdminControls<TextBlock>(window).Any(t=>t.Text.Contains("가상 1-1")))throw new InvalidOperationException("담당 학급의 실제 상담 시간이 표시되지 않았습니다.");
            if(AdminControls<TextBlock>(window).Any(t=>t.Text.Contains("PortalClass {")))throw new InvalidOperationException("학급 선택에 내부 데이터 형식이 표시되었습니다.");
            window.UpdateLayout();SavePreview(window,Path.Combine(output,"counseling-preview.png"));
            await Disconnect();if(window.IsVisible)throw new InvalidOperationException("로그아웃 후 상담 예약 화면이 남았습니다.");
            File.WriteAllText(Path.Combine(output,"counseling-ui-checks.json"),JsonSerializer.Serialize(new{passed=new[]{"인증된 교내 연결로 상담 시간 표시","네이티브 상담 화면 렌더링","로그아웃 때 상담 화면 종료"}}));
            Environment.Exit(0);
        }
        catch(Exception e){File.WriteAllText(Path.Combine(output,"counseling-ui-error.txt"),e.ToString());Environment.Exit(1);}
    }
}

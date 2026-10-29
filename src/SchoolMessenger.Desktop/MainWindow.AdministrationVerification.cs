using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    static IEnumerable<T> AdminControls<T>(DependencyObject root)where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var item=VisualTreeHelper.GetChild(root,i);if(item is T value)yield return value;foreach(var child in AdminControls<T>(item))yield return child;}
    }
    async Task VerifyAdministratorUi()
    {
        var checks=new List<string>();var output=Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..","artifacts");
        try
        {
            ServerAddress.Text=Environment.GetEnvironmentVariable("SCHOOL_UI_SERVER")??"";
            if(!Uri.TryCreate(ServerAddress.Text,UriKind.Absolute,out var address)||!address.IsLoopback||address.Scheme!="http")throw new InvalidOperationException("관리 UI 검사는 로컬 HTTP 가상 서버만 사용합니다.");
            output=Environment.GetEnvironmentVariable("YY_ADMIN_UI_OUTPUT")??output;Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(LocalDirectory,"update-seen.txt"),ReleaseNotice.Load("desktop").Sequence.ToString());
            UsernameBox.Text="admin";PasswordBox.Password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")??"";RememberLogin.IsChecked=true;await SignIn();
            for(var i=0;i<100&&administratorWindow?.Ready!=true;i++)await Task.Delay(50);
            if(administratorWindow is not{IsVisible:true,Ready:true})throw new InvalidOperationException("관리자 로그인 후 관리 시스템이 열리지 않았습니다.");
            checks.Add("관리자 로그인 후 네이티브 서버 관리 화면 자동 표시 및 상태 로딩");
            var first=administratorWindow;OpenAdministrator();if(!ReferenceEquals(first,administratorWindow)||OwnedWindows.OfType<ServerManagementWindow>().Count()!=1)throw new InvalidOperationException("관리 창이 중복되었습니다.");
            checks.Add("관리 메뉴 재실행 시 기존 창 활성화");
            AdminControls<ComboBox>(first).Single().SelectedIndex=0;
            AdminControls<Button>(first).Single(b=>b.Content?.ToString()=="부하 설정 적용").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for(var i=0;i<100&&(!first.Ready||(await Api!.Get<JsonElement>("api/admin/server-resources")).GetProperty("profile").GetString()!="low-memory");i++)await Task.Delay(50);
            await first.Refresh(true);if((await Api!.Get<JsonElement>("api/admin/server-resources")).GetProperty("profile").GetString()!="low-memory")throw new InvalidOperationException("관리 창에서 부하 설정 저장 실패");
            await Task.Delay(10500);await first.Refresh(true);
            first.Title+=" · 로컬 가상 서버 검증";SavePreview(first,Path.Combine(output,"admin-system-preview.png"));checks.Add("실제 관리 화면의 부하 설정 버튼이 인증된 API에 저장");
            var saved=File.ReadAllBytes(savedLogin.FilePath);await Disconnect();if(first.IsVisible)throw new InvalidOperationException("연결 종료 후 관리 창이 남았습니다.");
            File.WriteAllBytes(savedLogin.FilePath,saved);await TryAutomaticLogin();if(administratorWindow is not{IsVisible:true})throw new InvalidOperationException("자동 로그인 후 관리 창이 열리지 않았습니다.");
            checks.Add("암호화된 자동 로그인 복원 후 관리 시스템 자동 표시");
            var restored=administratorWindow;await Api!.Send<JsonElement>(HttpMethod.Post,"api/logout");await Disconnect();if(restored.IsVisible||OwnedWindows.OfType<ServerManagementWindow>().Any())throw new InvalidOperationException("로그아웃 후 관리 창이 남았습니다.");checks.Add("로그아웃 시 관리 창과 저장된 로그인 제거");
            UsernameBox.Text=Environment.GetEnvironmentVariable("SCHOOL_UI_TEACHER")??"ordinary";PasswordBox.Password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")??"";RememberLogin.IsChecked=false;await SignIn();OpenAdministrator();
            if(administratorWindow is not null||AdminButton.Visibility==Visibility.Visible)throw new InvalidOperationException("일반 교직원에게 관리 창이 표시되었습니다.");
            try{await Api!.Get<JsonElement>("api/admin/server-resources");throw new InvalidOperationException("일반 계정의 관리 API 접근 허용");}catch(ApiRequestException e)when(e.StatusCode==HttpStatusCode.Forbidden){}
            checks.Add("일반 교직원 로그인은 관리 창을 열지 않으며 서버에서도 접근 거부");
            await Api!.Send<JsonElement>(HttpMethod.Post,"api/logout");await Disconnect();
            File.WriteAllText(Path.Combine(output,"administrator-ui-checks.json"),JsonSerializer.Serialize(new{passed=checks,realSchoolServer=false,verifiedAt=DateTimeOffset.UtcNow}));
            exiting=true;Application.Current.Shutdown(0);
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"administrator-ui-error.txt"),error.ToString());exiting=true;Application.Current.Shutdown(1);}
    }
}

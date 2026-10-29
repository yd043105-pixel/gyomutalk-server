using System.Windows;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    async void OpenElectivesClick(object sender,RoutedEventArgs e)
    {await Run(async()=>{if(await RefreshNavigationAccess())new ElectiveWindow(this).Show();});}
    async Task VerifyElectivesUi()
    {
        var output=Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")??Path.Combine(LocalDirectory,"elective-ui.json");ElectiveWindow? window=null;
        try
        {
            ServerAddress.Text=Environment.GetEnvironmentVariable("SCHOOL_UI_SERVER")??throw new InvalidOperationException("검사 서버 주소 필요");
            if(!Uri.TryCreate(ServerAddress.Text,UriKind.Absolute,out var address)||!address.IsLoopback||address.Scheme!="http")throw new InvalidOperationException("수강신청 UI 검사는 로컬 HTTP 가상 서버만 사용합니다.");
            File.WriteAllText(Path.Combine(LocalDirectory,"update-seen.txt"),SchoolMessenger.Shared.ReleaseNotice.Load("desktop").Sequence.ToString());UsernameBox.Text="admin";PasswordBox.Password=Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")??"";await SignIn();
            var directory=Path.GetDirectoryName(Path.GetFullPath(output))!;Directory.CreateDirectory(directory);window=new ElectiveWindow(this);window.Show();var checks=await window.Verify((w,p)=>SavePreview(w,p),directory);File.WriteAllText(output,JsonSerializer.Serialize(new{checks,realSchoolData=false}));
        }
        catch(Exception e){File.WriteAllText(output,JsonSerializer.Serialize(new{error=e.Message}));}
        finally{window?.Close();CloseApp();}
    }
}

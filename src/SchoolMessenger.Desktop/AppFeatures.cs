using System.Windows;

namespace SchoolMessenger.Desktop;

// Features that only work together with the phone apps stay built but closed until the apps ship.
// To open them, set Ready to true. Verification runs keep them open so their checks still cover the code.
static class AppFeatures
{
    internal static bool Ready=Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--verify-",StringComparison.Ordinal));
    // Download-site screenshots run in verification but should show what a school sees.
    internal static bool LooksClosed=>!Ready||MainWindow.CaptureScale>1;
    internal static bool Pending(Window owner,string feature)
    {
        if(Ready)return false;
        MessageBox.Show(owner,feature+" 기능은 개발 진행 중입니다.\n\n학생·학부모·교사용 앱과 연동되는 기능이라, 앱이 나온 뒤에 사용할 수 있습니다.",feature,MessageBoxButton.OK,MessageBoxImage.Information);
        return true;
    }
}

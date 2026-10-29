using System.Windows;

namespace SchoolMessenger.Desktop;

static class Branding
{
    public static readonly System.Drawing.Icon TrayIcon=LoadIcon();
    static System.Drawing.Icon LoadIcon()
    {
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/brand_mark.ico"))!.Stream;
        using var icon=new System.Drawing.Icon(stream);return (System.Drawing.Icon)icon.Clone();
    }
}

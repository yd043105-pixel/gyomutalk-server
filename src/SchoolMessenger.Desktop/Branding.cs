using System.Windows;

namespace SchoolMessenger.Desktop;

static class Branding
{
    public static readonly System.Drawing.Icon TrayIcon=LoadIcon("brand_mark");
    // The server manager has its own mark, so it cannot be mistaken for the teacher program in the tray.
    public static readonly System.Drawing.Icon AdminTrayIcon=LoadIcon("admin_mark");
    static System.Drawing.Icon LoadIcon(string name)
    {
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/"+name+".ico"))!.Stream;
        using var icon=new System.Drawing.Icon(stream);return (System.Drawing.Icon)icon.Clone();
    }
}

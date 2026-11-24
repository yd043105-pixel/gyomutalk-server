using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SchoolMessenger.Desktop;

static class Branding
{
    public static readonly System.Drawing.Icon TrayIcon=LoadIcon("brand_mark");
    // The server manager has its own mark, so it cannot be mistaken for the teacher program in the tray.
    public static readonly System.Drawing.Icon AdminTrayIcon=LoadIcon("admin_mark");
    static DrawingImage? original;
    // The teacher program's mark in the chosen screen color: its tile and dots take the accent, the roof a light shade of it,
    // the flag stays yellow. Windows show it in the title bar and the taskbar (AppIcon); the tray takes it through TrayFor.
    // The desktop shortcut keeps the blue mark of the program file.
    public static void Tint(ResourceDictionary resources, Color accent, Color roof)
    {
        original ??= resources["ProductMark"] as DrawingImage;
        if (original is null) return;
        var blue = Color.FromRgb(0x24, 0x63, 0xEB); var sky = Color.FromRgb(0xA9, 0xD9, 0xEC);
        var mark = original.Clone();
        Brush? Recolor(Brush? brush) => brush is SolidColorBrush { Color: var c } ? c == blue ? new SolidColorBrush(accent) : c == sky ? new SolidColorBrush(roof) : brush : brush;
        foreach (var part in ((DrawingGroup)mark.Drawing).Children.OfType<GeometryDrawing>())
        {
            part.Brush = Recolor(part.Brush);
            if (part.Pen is { } pen) pen.Brush = Recolor(pen.Brush);
        }
        mark.Freeze(); resources["ProductMark"] = mark;
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen()) context.DrawImage(mark, new Rect(0, 0, 64, 64));
        var icon = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); icon.Render(visual); icon.Freeze();
        resources["AppIcon"] = icon;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(nint handle);
    static System.Drawing.Icon? tray; static BitmapSource? trayFrom;
    public static System.Drawing.Icon TrayFor(System.Windows.Forms.NotifyIcon target)
    {
        if (System.Windows.Application.Current.Resources["AppIcon"] is not BitmapSource image) return TrayIcon;
        if (image == trayFrom && tray is not null) { target.Icon = tray; return tray; }
        trayFrom = image;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(stream);
        var handle = bitmap.GetHicon(); var next = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); DestroyIcon(handle);
        var previous = tray; target.Icon = tray = next; previous?.Dispose();
        return next;
    }
    static System.Drawing.Icon LoadIcon(string name)
    {
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/"+name+".ico"))!.Stream;
        using var icon=new System.Drawing.Icon(stream);return (System.Drawing.Icon)icon.Clone();
    }
}

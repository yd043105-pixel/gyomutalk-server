using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SchoolMessenger.Desktop;

// Ctrl+V with a picture (a capture, an image copied from a page) or files copied in Explorer attaches them, as in
// KakaoTalk. Copied text still pastes as text.
static class ClipboardFiles
{
    public static bool IsPaste(KeyEventArgs e) =>
        e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control || e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift;

    public static string[] Take()
    {
        try { return Clipboard.ContainsText() ? [] : From(Clipboard.GetDataObject()); }
        catch (System.Runtime.InteropServices.ExternalException) { return []; }
    }

    public static string[] From(IDataObject? data)
    {
        if (data is null) return [];
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) return paths;
        BitmapSource? image = null;
        // The PNG copy keeps transparency and exact colours; the plain bitmap is what older programs put there.
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is Stream png)
            try { image = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; }
            catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException) { }
        if (image is null && data.GetDataPresent(DataFormats.Bitmap)) image = data.GetData(DataFormats.Bitmap) as BitmapSource;
        if (image is null) return [];
        var folder = Path.Combine(Path.GetTempPath(), "gyomutalk-clipboard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"붙여넣은 사진 {DateTime.Now:yyyyMMdd-HHmmss}.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(path)) encoder.Save(file);
        return [path];
    }
}

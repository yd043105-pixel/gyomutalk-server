using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// Files received in 교무톡 land in one folder without asking: 문서\GyomutalkFiles. Right-click offers that folder
// (showing where it is) or 다른 이름으로 저장 to choose the place and name.
static class Downloads
{
    // Test runs (their own data folder) save beside that folder, never into the real documents folder.
    internal static string Folder => Environment.GetEnvironmentVariable("SCHOOL_UI_LOCALDIR") is { Length: > 0 } test ? Path.Combine(test, "GyomutalkFiles")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GyomutalkFiles");

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // The sender chose the name: only its last part is kept, characters Windows refuses become _, and it stays short.
    internal static string CleanName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat((name ?? "").Replace('\\', '/').Split('/')[^1].Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "받은 파일";
        var stem = Path.GetFileNameWithoutExtension(clean); var extension = Path.GetExtension(clean);
        if (extension.Length > 20) { stem = clean; extension = ""; }
        if (stem.Length > 120) stem = stem[..120];
        if (stem.Length == 0 || Reserved.Contains(stem)) stem = "_" + stem;
        return stem + extension;
    }

    // A free path in the folder: a file already there is kept, and the new one becomes "이름 (2).확장자".
    internal static string FreePath(string? name)
    {
        Directory.CreateDirectory(Folder);
        var clean = CleanName(name);
        var stem = Path.GetFileNameWithoutExtension(clean); var extension = Path.GetExtension(clean);
        var path = Path.Combine(Folder, clean);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++) path = Path.Combine(Folder, $"{stem} ({n}){extension}");
        return path;
    }

    // Left click: straight into the folder. Returns the saved path, or null when the download was paused or failed.
    internal static async Task<string?> ToFolder(Window owner, Api api, Attachment file)
    {
        var path = FreePath(file.Name);
        return await AttachmentTransferWindow.Save(owner, api, file.Id, path) ? path : null;
    }

    internal static async Task<string?> SaveAs(Window owner, Api api, Attachment file)
    {
        Directory.CreateDirectory(Folder);
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = CleanName(file.Name), InitialDirectory = Folder };
        if (dialog.ShowDialog(owner) != true) return null;
        return await AttachmentTransferWindow.Save(owner, api, file.Id, dialog.FileName) ? dialog.FileName : null;
    }

    internal static string Saved(string path) =>
        Path.GetDirectoryName(path) == Folder ? $"GyomutalkFiles 폴더에 저장했습니다 · {Path.GetFileName(path)}" : "파일을 저장했습니다 · " + path;

    internal static void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Folder }, UseShellExecute = false })?.Dispose();
    }

    static Task Act(Window owner, Api? api, Func<Attachment?> file, Func<Func<Task>, Task> run, Action<string> report, Func<Window, Api, Attachment, Task<string?>> how) =>
        run(async () => { if (api is not null && file() is { } chosen && await how(owner, api, chosen) is { } path) report(Saved(path)); });

    // A click on a received file.
    internal static Task Receive(Window owner, Api? api, Func<Attachment?> file, Func<Func<Task>, Task> run, Action<string> report) =>
        Act(owner, api, file, run, report, ToFolder);

    // Gives a received-file button its right-click menu once; the file is read when the menu is used.
    internal static void Wire(Button button, Window owner, Func<Api?> api, Func<Attachment?> file, Func<Func<Task>, Task> run, Action<string> report)
    {
        if (button.ContextMenu is not null) return;
        button.ContextMenu = Menu(() => Act(owner, api(), file, run, report, ToFolder), () => Act(owner, api(), file, run, report, SaveAs));
        button.ToolTip = "누르면 GyomutalkFiles 폴더에 받습니다 · 오른쪽 클릭: 다른 이름으로 저장";
    }

    // The right-click menu of a received file: the folder (with its path), another place, and the folder itself.
    internal static ContextMenu Menu(Func<Task> toFolder, Func<Task> saveAs)
    {
        var menu = new ContextMenu();
        var into = new MenuItem();
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = "지정된 폴더에 다운로드" });
        var where = new TextBlock { Text = Folder, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360, ToolTip = Folder };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        words.Children.Add(where); into.Header = words;
        System.Windows.Automation.AutomationProperties.SetName(into, "지정된 폴더에 다운로드 " + Folder);
        into.Click += async (_, _) => await toFolder();
        var other = new MenuItem { Header = "다른 이름으로 저장…" };
        other.Click += async (_, _) => await saveAs();
        var open = new MenuItem { Header = "받은 파일 폴더 열기" };
        open.Click += (_, _) => OpenFolder();
        menu.Items.Add(into); menu.Items.Add(other); menu.Items.Add(new Separator()); menu.Items.Add(open);
        return menu;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint = "GetWindow")] static extern nint OrderWindow(nint handle, uint command);
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")] static extern bool OrderEnabled(nint handle);
    static bool Above(Window front, Window back)
    {
        var target = new WindowInteropHelper(back).Handle;
        for (var next = OrderWindow(new WindowInteropHelper(front).Handle, 2); next != 0; next = OrderWindow(next, 2))
            if (next == target) return true;
        return false;
    }
    async Task VerifyWindowOrder()
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Bring(Window window)
        {
            window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        foreach (var window in SessionWindows) window.Close();
        var compose = new ComposeWindow(this, people, null, "창 전환 검사"); compose.BodyBox.Text = "합성 자료: 로그아웃해도 임시저장이 남아야 합니다.";
        var mailbox = new MailboxWindow(this);
        var chat = new ChatWindow(this, people);
        var survey = new SurveyWindow(this, people);
        var work = new WorkWindow(this);
        var timetable = new TimetableWindow(this);
        Window[] windows = [compose, mailbox, chat, survey, work, timetable];
        foreach (var window in windows) window.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(windows.All(w => w.Owner is null && !w.Topmost && OrderWindow(new WindowInteropHelper(w).Handle, 4) == 0), "work windows have no native owner and are not topmost");
        Check(windows.All(w => SessionWindows.Contains(w)), "all work windows are still tracked by their signed-in session");
        await Bring(compose); await Bring(this);
        Check(Above(this, compose), "activating the roster brings it in front of the composer");
        await Bring(compose);
        Check(Above(compose, this), "activating the composer brings it back in front of the roster");
        Hide(); Check(compose.IsVisible, "hiding the roster leaves the work window usable"); Show(); WindowState = WindowState.Normal;
        var modal = new Window { Owner = compose, Title = "합성 선택 창", Width = 240, Height = 120, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var blocked = false;
        modal.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => { blocked = !OrderEnabled(new WindowInteropHelper(compose).Handle) && modal.Owner == compose; modal.Close(); }));
        modal.ShowDialog(); Check(blocked, "a required modal still blocks and belongs to its actual parent window");
        var before = SessionWindows.OfType<ChatWindow>().Count(); OpenNotification("chat", null);
        Check(SessionWindows.OfType<ChatWindow>().Count() == before, "chat notification reuses an existing independent chat window");
        var result = await Api!.Send<JsonElement>(System.Net.Http.HttpMethod.Post, "api/messages", new { clientId = Guid.NewGuid().ToString("N"), title = "창 갱신 확인", body = "합성 메시지", recipientIds = new[] { Api.Session!.User!.Id }, attachmentIds = Array.Empty<string>(), all = false });
        var messageId = result.GetProperty("id").GetString();
        await Refresh();
        for (var i = 0; i < 40 && !mailbox.MessageList.Items.OfType<MessageItem>().Any(m => m.Id == messageId); i++) { await Task.Delay(50); await RefreshWindows(); }
        Check(mailbox.MessageList.Items.OfType<MessageItem>().Any(m => m.Id == messageId), "session refresh still delivers a new message to an independent mailbox");
        var preview = Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")!))!;
        SavePreview(this, Path.Combine(preview, "organization.png"));
        await mailbox.ShowMessage(messageId);
        SavePreview(mailbox, Path.Combine(preview, "mailbox.png"));
        SavePreview(compose, Path.Combine(preview, "compose.png"));
        await SignOut();
        Check(windows.All(w => !w.IsVisible) && SessionWindows.Length == 0 && Api is null, "logout closes every independent work window and clears the session");
        Check(Directory.EnumerateFiles(Path.Combine(LocalDirectory, "drafts"), "*.dpapi", SearchOption.AllDirectories).Any(p => JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser)).RootElement.GetProperty("title").GetString() == "창 전환 검사"), "logout preserves the unsent message in an encrypted draft");
        UsernameBox.Text = Environment.GetEnvironmentVariable("SCHOOL_UI_USER") ?? "admin"; PasswordBox.Password = Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD") ?? ""; await SignIn();
        var exitWindow = new MailboxWindow(this); exitWindow.Show();
        Check(HasOpenWork, "update preparation still detects open independent work");
        exiting = true; Close();
        Check(!exitWindow.IsVisible && SessionWindows.Length == 0, "exiting the program closes independent work windows");
        var output = Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")!))!;
        Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "window-order-checks.json"), JsonSerializer.Serialize(new { checks, realSchoolData = false }));
        System.Windows.Application.Current.Shutdown(0);
    }
}

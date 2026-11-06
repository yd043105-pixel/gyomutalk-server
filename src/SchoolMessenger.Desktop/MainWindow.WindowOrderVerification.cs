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
using System.Windows.Controls;
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
        await VerifyRosterSelection(Check);
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
        try
        {
            await Api!.Get<JsonElement>("api/timetable/status");
            async Task Toggle(bool enabled)
            {
                await Api.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/admin/timetable-feature",new { enabled });
                for(var i=0;i<80&&TimetableButton.IsVisible!=enabled;i++)await Task.Delay(50);
                Check(TimetableButton.IsVisible==enabled&&(enabled||!timetable.IsVisible),enabled
                    ?"enabling the timetable restores the sidebar without signing in again"
                    :"disabling the timetable hides the sidebar entry and closes its open window");
            }
            await Toggle(false);await Toggle(true);
        }
        catch(ApiRequestException error) when(error.StatusCode==System.Net.HttpStatusCode.NotFound) { }
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
        var folderCompose=new ComposeWindow(this,people,null,"폴더 시험");folderCompose.Show();
        await folderCompose.VerifyFolderRetry(preview,Check);
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
    async Task VerifyRosterSelection(Action<bool, string> check)
    {
        async Task<string> AddTeacher(string username, string name)
        {
            var account = await Api!.Send<JsonElement>(System.Net.Http.HttpMethod.Post, "api/admin/users", new
            {
                username, name, department = "검증부", password = "Test-" + Guid.NewGuid(), isAdmin = false, canBroadcast = false
            });
            return account.GetProperty("id").GetString()!;
        }
        var first = await AddTeacher("roster.first", "검증 교사 가");
        var second = await AddTeacher("roster.second", "검증 교사 나");
        var outside = await AddTeacher("roster.outside", "검증 교사 다");
        await Api!.Send<JsonElement>(System.Net.Http.HttpMethod.Put, $"api/admin/users/{first}/roster-classification",
            new { grades = new[] { 1, 2 }, duties = new[] { "담임", "방송" } });
        await RefreshPeople();

        TreeViewItem[] Groups() => ((TreeViewItem)OrganizationTree.Items[0]).Items.OfType<TreeViewItem>().ToArray();
        TreeViewItem[] Rows(string id) => Groups().SelectMany(group => group.Items.OfType<TreeViewItem>())
            .Where(row => row.Tag is Person person && person.Id == id).ToArray();
        static CheckBox Box(TreeViewItem row) => ((Grid)row.Header).Children.OfType<CheckBox>().Single();
        async Task<string[]> OpenFromMenu(string id)
        {
            var row = Rows(id).Single();
            row.IsSelected = true;
            var previous = SessionWindows.OfType<ComposeWindow>().ToHashSet();
            var command = row.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "메시지 보내기"));
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            for (var i = 0; i < 100; i++)
            {
                var opened = SessionWindows.OfType<ComposeWindow>().FirstOrDefault(window => !previous.Contains(window));
                if (opened is not null)
                {
                    var labels = opened.RecipientChips.Children.OfType<Border>()
                        .Select(chip => ((StackPanel)chip.Child).Children.OfType<TextBlock>().Single().Text).ToArray();
                    opened.Close();
                    return labels;
                }
                await Task.Delay(30);
            }
            throw new InvalidOperationException("우클릭 메뉴에서 메시지 창이 열리지 않았습니다.");
        }
        string Label(string id) => people.Single(person => person.Id == id).Label;

        RosterMode.SelectedIndex = 1;
        check(Rows(first).Length == 2, "a teacher assigned to two grades appears in both grade groups");
        Box(Rows(first)[0]).IsChecked = true;
        check(Rows(first).All(row => Box(row).IsChecked == true) && selectedPeople.SetEquals([first]), "duplicate roster rows synchronize and select one recipient");
        RosterMode.SelectedIndex = 2;
        check(Rows(first).Length == 2 && Rows(first).All(row => Box(row).IsChecked == true), "duty groups preserve selection from the grade view");
        RosterMode.SelectedIndex = 3;
        check(Rows(first).Length == 0 && Rows(second).Length == 1 && Rows(outside).Length == 1, "unclassified view shows only teachers without a grade or duty");
        Box(Rows(second)[0]).IsChecked = true;
        RosterMode.SelectedIndex = 0;
        check((await OpenFromMenu(outside)).ToHashSet().SetEquals([Label(first), Label(second)]),
            "right-click on an unchecked teacher composes to all checked recipients only");
        var groupCheck = Groups().Select(group => (CheckBox)group.Header).Single(box => ((string)box.Content).StartsWith("검증부"));
        check(groupCheck.IsChecked is null, "partially selected groups display a mixed checkbox");
        groupCheck.IsChecked = false;
        check(selectedPeople.SetEquals([first, second, outside]) && groupCheck.IsChecked == true, "toggling a mixed group selects all its members");
        groupCheck.IsChecked = false;
        check((await OpenFromMenu(outside)).SequenceEqual([Label(outside)]), "right-click with no checked teachers composes to one teacher");
    }
}

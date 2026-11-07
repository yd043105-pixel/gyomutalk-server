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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SchoolMessenger.Contracts;

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
        var preview = Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")!))!;
        async Task CaptureAfterResize(Window window,string name)
        {
            await Task.Delay(120);
            await Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            var visual=(FrameworkElement)VisualTreeHelper.GetChild(window,0);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(preview,name+".png"));encoder.Save(file);
        }
        await VerifyRosterSelection(Check);
        PeopleSearch.Text = "존재하지 않는 가상 교직원";
        var (originalWidth,originalHeight)=(Width,Height);
        Width=MinWidth;Height=MinHeight;
        await CaptureAfterResize(this,"organization-empty-compact");
        Check(RosterEmpty.IsVisible&&!OrganizationTree.IsVisible,"compact organization shows a dedicated empty search card");
        PeopleSearch.Clear();await CaptureAfterResize(this,"organization-compact");
        Width=originalWidth;Height=originalHeight;
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
        for(var i=0;i<80&&mailbox.MailboxEmptyTitle.Text!="받은 메시지가 없습니다";i++)await Task.Delay(50);
        mailbox.Width=mailbox.MinWidth;mailbox.Height=mailbox.MinHeight;
        await CaptureAfterResize(mailbox,"mailbox-empty-compact");
        Check(mailbox.MailboxEmptyTitle.IsVisible&&!mailbox.ReplyButton.IsVisible&&Grid.GetColumnSpan(mailbox.ReaderPane)==2,
            "compact empty mailbox retains search and uses the full reading area without reply controls");
        mailbox.Width=880;mailbox.Height=660;
        IEnumerable<DependencyObject> Controls(DependencyObject parent)
        {
            for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
            {
                var child=VisualTreeHelper.GetChild(parent,index);yield return child;
                foreach(var nested in Controls(child))yield return nested;
            }
        }
        async Task WaitUntil(Func<bool> condition)
        {
            for(var i=0;i<100&&!condition();i++)await Task.Delay(50);
            timetable.UpdateLayout();
        }
        bool TextOnSchedule(string text)=>Controls(timetable).OfType<TextBlock>().Any(label=>label.Text==text&&label.IsVisible);
        await WaitUntil(()=>TextOnSchedule("7교시"));
        SavePreview(timetable,Path.Combine(preview,"timetable.png"));
        timetable.Width=timetable.MinWidth;timetable.Height=timetable.MinHeight;
        await CaptureAfterResize(timetable,"timetable-compact");
        var swapButton=Controls(timetable).OfType<Button>().Single(button=>Equals(button.Content,"수업 교체 신청"));
        Check(TextOnSchedule("7교시")&&swapButton.IsVisible&&
            swapButton.TranslatePoint(new Point(),timetable).Y+swapButton.ActualHeight<=timetable.ActualHeight,
            "compact timetable keeps the schedule actions in the viewport");
        var next=Controls(timetable).OfType<Button>().Single(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="다음");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(()=>TextOnSchedule("아직 올라온 시간표가 없습니다"));
        SavePreview(timetable,Path.Combine(preview,"timetable-empty-compact.png"));
        Check(TextOnSchedule("아직 올라온 시간표가 없습니다")&&Controls(timetable).OfType<Button>().Count(button=>Equals(button.Content,"시간표 올리기")&&button.IsVisible)==1,
            "unpublished week highlights one upload action in the empty card");
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
        SavePreview(this, Path.Combine(preview, "organization.png"));
        await mailbox.ShowMessage(messageId!);
        SavePreview(mailbox, Path.Combine(preview, "mailbox.png"));
        mailbox.Width=mailbox.MinWidth;mailbox.Height=mailbox.MinHeight;
        await CaptureAfterResize(mailbox,"mailbox-compact");
        Check(mailbox.ReplyButton.IsVisible&&Grid.GetRow(mailbox.DetailPanel)==0&&
            mailbox.ReplyButton.TranslatePoint(new Point(),mailbox.ReaderPane).Y>=mailbox.DetailPanel.ActualHeight,
            "compact message reader keeps reply outside the scrolling message");
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
        var parent = Guid.NewGuid().ToString("N");
        var child = Guid.NewGuid().ToString("N");
        var snapshot = await Api!.Get<RosterFolders>("api/admin/roster-folders");
        await Api.Send<RosterFolders>(System.Net.Http.HttpMethod.Put, "api/admin/roster-folders", new RosterFolders(snapshot.Revision,
            [new(parent, null, "검증 프로젝트", [first]), new(child, parent, "방송 준비", [first, second])]));
        await RefreshPeople();

        IEnumerable<TreeViewItem> RowsBelow(ItemsControl control)
        {
            foreach (var row in control.Items.OfType<TreeViewItem>())
            {
                yield return row;
                foreach (var childRow in RowsBelow(row)) yield return childRow;
            }
        }
        TreeViewItem[] Rows(string id) => RowsBelow(OrganizationTree)
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
                        .Select(chip => (string)((StackPanel)chip.Child).Children.OfType<TextBlock>().Single().ToolTip).ToArray();
                    opened.Close();
                    return labels;
                }
                await Task.Delay(30);
            }
            throw new InvalidOperationException("우클릭 메뉴에서 메시지 창이 열리지 않았습니다.");
        }
        string Label(string id) => people.Single(person => person.Id == id).Label;

        var parentRow = RowsBelow(OrganizationTree).Single(row => Equals(row.Tag, "folder:" + parent));
        check(parentRow.Items.OfType<TreeViewItem>().Any(row => Equals(row.Tag, "folder:" + child)) &&
            Rows(first).Length == 2, "a folder contains a second level and a teacher can appear in both");
        check(RowsBelow(OrganizationTree).Single(row => Equals(row.Tag, "unclassified")).Items.OfType<TreeViewItem>()
            .Any(row => row.Tag is Person p && p.Id == outside), "unassigned teachers appear in the unclassified folder");
        Box(Rows(first)[0]).IsChecked = true;
        check(Rows(first).All(row => Box(row).IsChecked == true) && selectedPeople.SetEquals([first]), "duplicate roster rows synchronize and select one recipient");
        PeopleSearch.Text = "방송 준비";
        check(Rows(first).Length == 1 && Box(Rows(first)[0]).IsChecked == true && Rows(outside).Length == 0,
            "folder search opens matching paths and keeps prior selections");
        PeopleSearch.Clear();
        var groupCheck = (CheckBox)RowsBelow(OrganizationTree).Single(row => Equals(row.Tag, "folder:" + parent)).Header;
        check(groupCheck.IsChecked is null, "partially selected folders display a mixed checkbox");
        groupCheck.IsChecked = false;
        check(selectedPeople.SetEquals([first, second]) && groupCheck.IsChecked == true, "toggling a parent selects its direct and nested members once");
        check((await OpenFromMenu(outside)).ToHashSet().SetEquals([Label(first), Label(second)]),
            "right-click on an unchecked teacher composes to all checked recipients only");
        ((CheckBox)RowsBelow(OrganizationTree).Single(row => Equals(row.Tag, "folder:" + parent)).Header).IsChecked = false;
        check((await OpenFromMenu(outside)).SequenceEqual([Label(outside)]), "right-click with no checked teachers composes to one teacher");
    }
}

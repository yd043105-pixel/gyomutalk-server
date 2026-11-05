using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SchoolMessenger.Desktop;

public partial class ChatWindow : Window
{
    readonly MainWindow main;
    readonly Person[] people;
    readonly ObservableCollection<SourceFile> files = [];
    ChatRoom? menuRoom;
    int shown = 100;
    ChatPending? pending;
    bool busy, refreshing, suppressSelection;
    int selectionVersion;
    public string? RoomId => (Rooms.SelectedItem as ChatRoom)?.Id;
    string DraftPath(string room) => Path.Combine(main.LocalDirectory, $"chat-{main.Api!.Session!.User!.Id}-{room}.json");
    public ChatWindow(MainWindow main, Person[] people, string[]? selected = null, string? room = null)
    {
        this.main = main; this.people = people; main.RegisterWorkWindow(this); InitializeComponent(); FilesList.ItemsSource = files; StatusTone.Watch(Feedback); ChatEntry.Me = main.Api?.Session?.User?.Id;
        main.ChatChanged += Changed;
        Closed += (_, _) => { main.ChatChanged -= Changed; DiscardFiles(); };
        // Someone reading another window has not read the new message yet; coming back here does.
        Loaded += async (_, _) => { await Run(async () => { if (selected is { Length: > 0 }) await CreateRoom(selected, ""); else await ReloadRooms(room); }); Activated += async (_, _) => await Run(Refresh); };
    }
    async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { Feedback.Text = error.Message; } }
    async void Changed(string _) => await Run(Refresh);
    public async Task CreateRoom(string[] ids, string name)
    {
        var response = await main.Api!.Send<JsonElement>(HttpMethod.Post, "api/chats", new { memberIds = ids, name });
        await ReloadRooms(response.GetProperty("id").GetString());
    }
    public Task ShowRoom(string room) => ReloadRooms(room);
    async void NewRoom(object sender, RoutedEventArgs e)
    { var picker = new RecipientWindow(main, people.Where(p => p.Id != main.Api!.Session!.User!.Id).ToArray(), true); if (picker.ShowDialog() == true) await Run(() => CreateRoom(picker.SelectedIds, picker.RoomName)); }
    async Task ReloadRooms(string? select = null)
    {
        if (busy || pending is not null) return;
        var room = select ?? RoomId;
        var result = (await main.Archive.LocalRooms(await main.Api!.Get<ChatRoom[]>("api/chats"))).Select(r => r with { SelfId = main.Api!.Session!.User!.Id, Muted = main.MutedRooms.Contains(r.Id) }).ToArray();
        suppressSelection = true; Rooms.ItemsSource = result; Rooms.SelectedItem = result.FirstOrDefault(r => r.Id == room) ?? result.FirstOrDefault(); suppressSelection = false;
        UpdateHead();
        if (RoomId != room || select is not null) SelectRoom(this, null!);
    }
    void UpdateHead()
    {
        var hasRooms = Rooms.Items.Count > 0;
        ChatPane.Visibility = hasRooms ? Visibility.Visible : Visibility.Collapsed; EmptyHost.Visibility = hasRooms ? Visibility.Collapsed : Visibility.Visible;
        if (!hasRooms && EmptyHost.Content is null) { var start = new Button { Content = "새 대화방" }; start.SetResourceReference(StyleProperty, "PrimaryButton"); start.Click += NewRoom; EmptyHost.Content = AdminUi.Empty("아직 대화방이 없습니다", "동료를 골라 대화를 시작하세요.", start); }
        if (Rooms.SelectedItem is not ChatRoom room) return;
        RoomTitle.Text = room.Display; MembersLabel.Text = room.Participants;
        BellIcon.Data = (Geometry)FindResource(room.Muted ? "BellOff" : "BellOn"); BellButton.ToolTip = room.Muted ? "이 방 알림 켜기" : "이 방 알림 끄기";
        System.Windows.Automation.AutomationProperties.SetName(BellButton, (string)BellButton.ToolTip);
    }
    // Opening the menu on another room must not open that room and mark its messages as read.
    void KeepSelection(object sender, MouseButtonEventArgs e) => e.Handled = true;
    void RoomMenu(object sender, ContextMenuEventArgs e)
    {
        menuRoom = (ItemsControl.ContainerFromElement(Rooms, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as ChatRoom;
        if (menuRoom is null) { e.Handled = true; return; }
        MuteItem.Header = menuRoom.Muted ? "이 방 알림 켜기" : "이 방 알림 끄기"; LeaveItem.Header = menuRoom.Direct ? "목록에서 지우기" : "대화방 나가기";
    }
    async void RenameRoom(object sender, RoutedEventArgs e)
    {
        if (menuRoom is not { } room) return;
        var box = new TextBox { Text = room.Display, MaxLength = 100, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(AdminUi.Heading("대화방 이름 바꾸기")); panel.Children.Add(box);
        var note = AdminUi.Label("내 화면에서만 바뀝니다. 비워 두면 원래 이름으로 돌아갑니다.", "Muted", 12); note.Margin = new Thickness(0, 8, 0, 16); panel.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.SetResourceReference(StyleProperty, "SubtleButton"); buttons.Children.Add(cancel);
        var save = new Button { Content = "저장", IsDefault = true }; save.SetResourceReference(StyleProperty, "PrimaryButton"); buttons.Children.Add(save);
        var host = new Border { Child = panel }; host.SetResourceReference(Border.BackgroundProperty, "Surface");
        var dialog = new Window { Title = "교무톡 · 채팅", Owner = this, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = host, Style = (Style)FindResource("SchoolWindow") };
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        await Run(async () => { await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room.Id}/name", new { name = box.Text }); await ReloadRooms(); });
    }
    async void MuteRoom(object sender, RoutedEventArgs e) { if (menuRoom is { } room) await Run(() => SetMuted(room)); }
    async void ToggleMute(object sender, RoutedEventArgs e) { if (Rooms.SelectedItem is ChatRoom room) await Run(() => SetMuted(room)); }
    async Task SetMuted(ChatRoom room)
    {
        if (!main.MutedRooms.Add(room.Id)) main.MutedRooms.Remove(room.Id);
        main.SavePreferences(); await ReloadRooms();
    }
    async void LeaveRoom(object sender, RoutedEventArgs e)
    {
        if (menuRoom is not { } room) return;
        var warning = room.Direct ? $"{room.Display} 대화를 내 목록에서 지울까요?\n상대방의 대화는 그대로이고, 상대방이 새 메시지를 보내면 다시 나타납니다." : $"{room.Display} 대화방에서 나갈까요?\n나간 뒤에는 이 대화방을 다시 볼 수 없습니다.";
        if (MessageBox.Show(this, warning, room.Direct ? "목록에서 지우기" : "대화방 나가기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(async () => { await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room.Id}/leave"); if (!room.Direct) main.Archive.DropRoom(room.Id); main.MutedRooms.Remove(room.Id); main.SavePreferences(); await ReloadRooms(); });
    }
    async void SelectRoom(object sender, SelectionChangedEventArgs e)
    {
        if (RoomId is null || suppressSelection) return;
        var room = (ChatRoom)Rooms.SelectedItem; selectionVersion++; shown = 100; DiscardFiles(); pending = null;
        UpdateHead(); BodyBox.Clear(); Feedback.Text = "";
        try
        {
            var path = DraftPath(room.Id);
            if (File.Exists(path))
            {
                var draft = JsonSerializer.Deserialize<ChatPending>(File.ReadAllText(path));
                if (draft?.Server == main.Api!.Address.ToString() && draft.RoomId == room.Id) { pending = draft; BodyBox.Text = draft.Body; Freeze(true); Feedback.Text = "전송 결과 미확인. 보내기를 눌러 같은 대화를 재시도하세요."; }
            }
            await LoadMessages(false);
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    public async Task Refresh()
    { if (refreshing || busy || main.Api is null) return; refreshing = true; try { await LoadMessages(false); if (pending is null) await ReloadRooms(); } finally { refreshing = false; } }
    async Task LoadMessages(bool older)
    {
        var room = RoomId; var version = selectionVersion; if (room is null || main.Api is null) return;
        await main.Archive.KeepRoom(room);
        if (room != RoomId || version != selectionVersion) return;
        var all = main.Archive.ChatEntries(room); if (older) shown += 100;
        MessagesList.ItemsSource = all.TakeLast(shown).ToArray(); OlderButton.Visibility = all.Length > shown ? Visibility.Visible : Visibility.Collapsed;
        LocalNote.Visibility = all.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!older) { MessagesScroll.ScrollToBottom(); if (all.Length > 0 && IsActive) await main.Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room}/read", new { sequence = all[^1].Sequence }); }
    }
    async void Older(object sender, RoutedEventArgs e) => await Run(() => LoadMessages(true));
    void Attach(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "업무 자료|*.hwp;*.hwpx;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.csv;*.png;*.jpg;*.jpeg;*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        var added = dialog.FileNames.Where(p => files.All(f => f.Path != p)).Select(p => new SourceFile(p)).ToArray(); var all = files.Concat(added).Select(f => new FileInfo(f.Path).Length).ToArray();
        if (all.Length > 10 || all.Any(size => size > 104_857_600) || all.Sum() > 209_715_200) { Feedback.Text = "파일당 100MB, 합계 200MB, 최대 10개입니다."; return; }
        foreach (var file in added) files.Add(file);
    }
    async void RemoveFile(object sender, RoutedEventArgs e)
    {
        if (pending is not null || busy || ((Button)sender).Tag is not SourceFile file) return;
        await Run(() => TakeBack(file));
    }
    async Task TakeBack(SourceFile file)
    {
        files.Remove(file); if (file.Uploaded is { } sent) await main.Api!.Send<JsonElement>(HttpMethod.Delete, $"api/attachments/{sent.Id}");
    }
    internal async Task<string> VerifyTakeBack(string path)
    {
        var file = new SourceFile(path) { Uploaded = await main.Api!.Upload(path) }; files.Add(file);
        await TakeBack(file); return file.Uploaded.Id;
    }
    // Files already on the server for a message that will never be sent go away with the wait list.
    void DiscardFiles()
    {
        foreach (var sent in files.Select(f => f.Uploaded).OfType<Attachment>().ToArray()) _ = Run(() => main.Api!.Send<JsonElement>(HttpMethod.Delete, $"api/attachments/{sent.Id}"));
        files.Clear();
    }
    void Freeze(bool frozen) { BodyBox.IsEnabled = AttachButton.IsEnabled = FilesList.IsEnabled = Rooms.IsEnabled = !frozen; }
    async void SendClick(object sender, RoutedEventArgs e) => await Run(SendMessage);
    public async Task SendMessage()
    {
        var room = RoomId;
        if (room is null || busy || main.Api is null || (pending is null && string.IsNullOrWhiteSpace(BodyBox.Text) && files.Count == 0)) return;
        busy = true; Freeze(true); SendButton.IsEnabled = false;
        try
        {
            if (pending is null)
            {
                foreach (var file in files) file.Uploaded ??= await main.Api.Upload(file.Path);
                pending = new ChatPending(main.Api.Address.ToString(), room, Guid.NewGuid().ToString(), BodyBox.Text, files.Select(f => f.Uploaded!.Id).ToArray());
                File.WriteAllText(DraftPath(room), JsonSerializer.Serialize(pending));
            }
            await main.Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room}/messages", pending);
            File.Delete(DraftPath(room)); pending = null; BodyBox.Clear(); files.Clear(); Feedback.Text = "";
            await LoadMessages(false);
        }
        catch { Feedback.Text = pending is null ? "첨부 업로드 실패. 다시 시도하세요." : "전송 결과 미확인. 보내기를 눌러 재시도하세요."; throw; }
        finally { busy = false; Freeze(pending is not null); SendButton.IsEnabled = true; }
    }
    async void MessageKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await Run(SendMessage); } }
    async void Download(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Attachment file) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = file.Name };
        if (dialog.ShowDialog(this) == true) await Run(() => main.Api!.Download(file.Id, dialog.FileName));
    }
}

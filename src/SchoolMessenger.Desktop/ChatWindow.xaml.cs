using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SchoolMessenger.Desktop;

public partial class ChatWindow : Window
{
    readonly MainWindow main;
    readonly Person[] people;
    readonly List<string> files = [];
    readonly List<Attachment> uploaded = [];
    ChatEntry[] entries = [];
    ChatPending? pending;
    bool busy, refreshing, suppressSelection;
    int selectionVersion;
    public string? RoomId => (Rooms.SelectedItem as ChatRoom)?.Id;
    string DraftPath(string room) => Path.Combine(main.LocalDirectory, $"chat-{main.Api!.Session!.User!.Id}-{room}.json");
    public ChatWindow(MainWindow main, Person[] people, string[]? selected = null)
    {
        this.main = main; this.people = people; Owner = main; InitializeComponent();
        main.ChatChanged += Changed;
        Closed += (_, _) => main.ChatChanged -= Changed;
        Loaded += async (_, _) => await Run(async () => { if (selected is { Length: > 0 }) await CreateRoom(selected, ""); else await ReloadRooms(); });
    }
    async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { Feedback.Text = error.Message; } }
    async void Changed(string _) => await Run(Refresh);
    public async Task CreateRoom(string[] ids, string name)
    {
        var response = await main.Api!.Send<JsonElement>(HttpMethod.Post, "api/chats", new { memberIds = ids, name });
        await ReloadRooms(response.GetProperty("id").GetString());
    }
    async void NewRoom(object sender, RoutedEventArgs e)
    { var picker = new RecipientWindow(main, people.Where(p => p.Id != main.Api!.Session!.User!.Id).ToArray(), true); if (picker.ShowDialog() == true) await Run(() => CreateRoom(picker.SelectedIds, picker.RoomName)); }
    async Task ReloadRooms(string? select = null)
    {
        if (busy || pending is not null) return;
        var room = select ?? RoomId;
        var result = (await main.Api!.Get<ChatRoom[]>("api/chats")).Select(r => r with { SelfId = main.Api.Session!.User!.Id }).ToArray();
        suppressSelection = true; Rooms.ItemsSource = result; Rooms.SelectedItem = result.FirstOrDefault(r => r.Id == room) ?? result.FirstOrDefault(); suppressSelection = false;
        if (RoomId != room || select is not null) SelectRoom(this, null!);
    }
    async void SelectRoom(object sender, SelectionChangedEventArgs e)
    {
        if (RoomId is null || suppressSelection) return;
        var room = (ChatRoom)Rooms.SelectedItem; selectionVersion++; entries = []; files.Clear(); uploaded.Clear(); pending = null;
        RoomTitle.Text = room.Display; MembersLabel.Text = room.Participants; BodyBox.Clear(); FileHint.Text = ""; Feedback.Text = "";
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
        var previous = entries; var suffix = older && entries.Length > 0 ? "?before=" + entries[0].Sequence : "";
        var loaded = await main.Api.Get<ChatEntry[]>($"api/chats/{room}/messages{suffix}");
        if (room != RoomId || version != selectionVersion) return;
        entries = (older ? loaded.Concat(previous) : previous.Concat(loaded)).DistinctBy(m => m.Id).OrderBy(m => m.Sequence).ToArray();
        MessagesList.ItemsSource = entries; OlderButton.Visibility = loaded.Length == 100 ? Visibility.Visible : Visibility.Collapsed;
        if (!older) { MessagesScroll.ScrollToBottom(); if (loaded.Length > 0) await main.Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room}/read", new { sequence = loaded[^1].Sequence }); }
    }
    async void Older(object sender, RoutedEventArgs e) => await Run(() => LoadMessages(true));
    void Attach(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "업무 자료|*.hwp;*.hwpx;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.csv;*.png;*.jpg;*.jpeg;*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames) if (!files.Contains(path)) files.Add(path);
        if (files.Count > 10 || files.Any(p => new FileInfo(p).Length > 104_857_600) || files.Sum(p => new FileInfo(p).Length) > 209_715_200) { files.Clear(); Feedback.Text = "파일당 100MB, 합계 200MB, 최대 10개입니다."; }
        FileHint.Text = string.Join(", ", files.Select(Path.GetFileName));
    }
    void ClearFiles(object sender, RoutedEventArgs e) { files.Clear(); uploaded.Clear(); FileHint.Text = ""; }
    void Freeze(bool frozen) { BodyBox.IsEnabled = AttachButton.IsEnabled = ClearFilesButton.IsEnabled = Rooms.IsEnabled = !frozen; }
    async void SendClick(object sender, RoutedEventArgs e) => await Run(SendMessage);
    public async Task SendMessage()
    {
        var room = RoomId;
        if (room is null || busy || main.Api is null || (pending is null && string.IsNullOrWhiteSpace(BodyBox.Text) && files.Count == 0)) return;
        busy = true; Freeze(true); SendButton.IsEnabled = false;
        if (pending is null && uploaded.Count > 0 && files.Count == 0) uploaded.Clear();
        try
        {
            if (pending is null)
            {
                foreach (var path in files.Skip(uploaded.Count)) uploaded.Add(await main.Api.Upload(path));
                pending = new ChatPending(main.Api.Address.ToString(), room, Guid.NewGuid().ToString(), BodyBox.Text, uploaded.Select(a => a.Id).ToArray());
                File.WriteAllText(DraftPath(room), JsonSerializer.Serialize(pending));
            }
            await main.Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{room}/messages", pending);
            File.Delete(DraftPath(room)); pending = null; BodyBox.Clear(); files.Clear(); uploaded.Clear(); FileHint.Text = ""; Feedback.Text = "";
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

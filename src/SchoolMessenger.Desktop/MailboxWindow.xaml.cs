using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
namespace SchoolMessenger.Desktop;
public partial class MailboxWindow : Window
{
    readonly MainWindow main;
    Api? Api => main.Api;
    readonly string? personId;
    readonly ObservableCollection<MessageItem> messages = new();
    string box;
    Detail? detail;
    bool refreshing, rendering;
    int detailsOpening;
    public MailboxWindow(MainWindow main, string box = "received", Person? person = null)
    {
        this.main = main; this.box = person is null ? box : "conversation"; personId = person?.Id;
        main.RegisterWorkWindow(this); Resources = main.Resources; InitializeComponent(); StatusTone.Watch(StatusLabel);
        MessageList.ItemsSource = messages;
        Title = person is null ? "교무톡 · 메시지함" : person.Name + " · 주고받은 메시지";
        BoxTitle.Text = person is null ? "메시지함" : person.Name + " 관련 메시지"; UpdateTabs(); UpdateMailboxLayout(false);
        Loaded += async (_, _) => await Reload();
        Closed += (_, _) => ResetTaskPrompt();
    }
    public Task Reload() => Run(() => Refresh());
    public Task ShowMessage(string id) => Run(() => OpenDetail(id));
    async Task Refresh(bool more = false)
    {
        if (Api is null || refreshing || detailsOpening>0) return;
        refreshing = true;
        try
        {
            var selected = detail?.Message.Id;
            var selectionVersion=taskPromptVersion;
            var path = $"api/messages?box={box}&q={Uri.EscapeDataString(SearchBox.Text)}&offset={(more ? fetched : 0)}";
            if (FromDate.SelectedDate is { } from) path += $"&from={new DateTimeOffset(from).ToUnixTimeMilliseconds()}";
            if (ToDate.SelectedDate is { } to) path += $"&to={new DateTimeOffset(to.AddDays(1)).ToUnixTimeMilliseconds() - 1}";
            if (personId is not null) path += "&person=" + Uri.EscapeDataString(personId);
            var answer = await Api.Get<MessageItem[]>(path); fetched = (more ? fetched : 0) + answer.Length;
            var rows = answer.Select(Local).Where(r => r.Title.Length > 0).ToList();
            if (!more && SearchBox.Text.Trim() is { Length: > 0 } query) rows.AddRange(LocalMatches(query).Where(m => rows.All(r => r.Id != m.Id)));
            // A list refresh started earlier must not clear or replace a message the user just opened.
            if(detailsOpening>0||selectionVersion!=taskPromptVersion||detail?.Message.Id!=selected)return;
            rendering = true;
            if (!more) messages.Clear();
            foreach (var row in rows) if (!messages.Any(m => m.Id == row.Id)) messages.Add(row with { IsSent = row.SenderId == Api.Session!.User!.Id });
            MessageList.SelectedItem = messages.FirstOrDefault(m => m.Id == selected);
            rendering = false;
            ListHint.Text = $"{messages.Count}개 표시 · 한 번에 100개 조회";
            UpdateMailboxLayout(messages.Count == 0);
            MoreMessages.Visibility = answer.Length == 100 ? Visibility.Visible : Visibility.Collapsed;
            if (selected is not null && messages.Any(m=>m.Id==selected)) await OpenDetail(selected, markRead: false);
            else if(!more){detail=null;ResetTaskPrompt();DetailBody.Text="";DetailTitle.Text="";FileList.ItemsSource=null;ReceiptList.ItemsSource=null;DetailPanel.Visibility=Visibility.Collapsed;EmptyHint.Visibility=Visibility.Visible;}
            ConnectionLabel.Text = "이 PC에 저장된 메시지만 보입니다 · 첨부파일은 업로드 후 30일간 보관됩니다.";
            StatusLabel.Text = "";
        }
        finally { refreshing = false; rendering = false; }
    }
    // Once this PC saved a message the server gives only its receipts, so title, preview and search come from the copy here.
    int fetched;
    MessageItem Local(MessageItem row) => row.Title.Length == 0 && main.Archive.Load(row.Id) is { } copy ? row with { Title = copy.Message.Title, Preview = SchoolMessenger.Shared.MessageText.Excerpt(copy.Message.Body) } : row;
    IEnumerable<MessageItem> LocalMatches(string query)
    {
        var me = Api!.Session!.User!.Id;
        long from = FromDate.SelectedDate is { } a ? new DateTimeOffset(a).ToUnixTimeMilliseconds() : 0, to = ToDate.SelectedDate is { } b ? new DateTimeOffset(b.AddDays(1)).ToUnixTimeMilliseconds() - 1 : long.MaxValue;
        foreach (var copy in main.Archive.Search(query))
        {
            var m = copy.Message; var mine = copy.Recipients.FirstOrDefault(r => r.Id == me);
            var fits = box switch { "sent" => m.SenderId == me, "conversation" => m.SenderId == personId || copy.Recipients.Any(r => r.Id == personId), "unread" => mine is { ReadAt: null }, _ => mine is not null };
            if (fits && m.CreatedAt >= from && m.CreatedAt <= to)
                yield return new MessageItem(m.Id, m.Title, SchoolMessenger.Shared.MessageText.Excerpt(m.Body), m.CreatedAt, m.SenderName, mine?.ReadAt, copy.Recipients.Length, copy.Recipients.Count(r => r.ReadAt is not null), copy.Attachments.Length, m.SenderId);
        }
    }
    async Task OpenDetail(string id, bool markRead = true)
    {
        if (Api is null) return;
        detailsOpening++;
        try
        {
        ResetTaskPrompt();
        var version = taskPromptVersion;
        var opened = await Api.Get<Detail>($"api/messages/{id}");
        if (version != taskPromptVersion) return;
        if (markRead && opened.Recipients.Any(r => r.Id == Api.Session!.User!.Id && r.ReadAt is null))
        {
            await Api.Send<JsonElement>(HttpMethod.Post, $"api/messages/{id}/read");
            opened = await Api.Get<Detail>($"api/messages/{id}");
        }
        if (version != taskPromptVersion) return;
        try { detail = main.Archive.Merge(opened); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { detail = opened; StatusLabel.Text = "이 PC에 메시지를 저장하지 못했습니다. 디스크 공간과 권한을 확인하세요."; }
        if (detail.Message.Purged) detail = detail with { Message = detail.Message with { Title = "이 PC에 없는 메시지", Body = "메시지는 받은 PC에만 저장됩니다. 이 PC에는 사본이 없어 내용을 볼 수 없습니다." } };
        DetailTitle.Text = SchoolMessenger.Shared.MessageText.Excerpt(detail.Message.Title, 200);
        DetailTitle.Visibility = detail.Message.Title == SchoolMessenger.Shared.MessageText.Excerpt(detail.Message.Body, 80) ? Visibility.Collapsed : Visibility.Visible;
        DetailMeta.Text = $"{detail.Message.SenderName}   ·   {DateTimeOffset.FromUnixTimeMilliseconds(detail.Message.CreatedAt).ToLocalTime():yyyy.MM.dd HH:mm}   ·   수신 {detail.Recipients.Length}명";
        DetailAvatar.Content = Avatar.Create(detail.Message.SenderName, 32);
        ReceiptPillText.Text = $"{detail.Recipients.Count(r => r.ReadAt is not null)}/{detail.Recipients.Length}명 확인";
        DetailBody.SetFormattedText(detail.Message.Body, detail.Message.Format);
        RecallButton.Visibility=detail.Message.SenderId==Api.Session!.User!.Id?Visibility.Visible:Visibility.Collapsed;
        RecallButton.IsEnabled=detail.Recipients.Any(r=>r.ReadAt is null);
        RecallButton.ToolTip="안 읽은 수신자가 한 명 이상 남아 있으면 회수할 수 있습니다.";
        SubmissionButton.Visibility = detail.SubmissionRequest ? Visibility.Visible : Visibility.Collapsed;
        var index = messages.ToList().FindIndex(m => m.Id == id);
        if (index >= 0)
        {
            rendering = true;
            messages[index] = messages[index] with { ReadAt = detail.Recipients.FirstOrDefault(r => r.Id == Api.Session!.User!.Id)?.ReadAt, ReadCount = detail.Recipients.Count(r => r.ReadAt is not null) };
            MessageList.SelectedItem = messages[index];
            rendering = false;
        }
        await main.UpdateUnread();
        if (version != taskPromptVersion) return;
        FileList.ItemsSource = detail.Attachments; ReceiptList.ItemsSource = detail.Recipients;
        AttachmentHeader.Visibility = detail.Attachments.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        LoadTaskPrompt(id);
        }
        finally{detailsOpening--;}
    }
    async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { StatusLabel.Text = error.Message; }
    }
    async void SelectMessage(object sender, SelectionChangedEventArgs e)
    { if (!rendering && MessageList.SelectedItem is MessageItem item) await Run(() => OpenDetail(item.Id)); }
    async void ChangeBox(object sender, RoutedEventArgs e)
    {
        box = ((System.Windows.Controls.Button)sender).Uid; UpdateTabs();
        BoxTitle.Text = "메시지함";
        detail = null; ResetTaskPrompt(); DetailPanel.Visibility = Visibility.Collapsed; EmptyHint.Visibility = Visibility.Visible;
        await Run(() => Refresh());
    }
    void UpdateTabs()
    {
        foreach (var tab in new[] { ReceivedTab, UnreadTab, SentTab })
        {
            tab.Tag = tab.Uid == box ? "selected" : null;
        }
    }
    void UpdateMailboxLayout(bool empty)
    {
        ListRow.Height = empty ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        EmptyRow.Height = empty ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(ListPane, empty ? 2 : 1);
        ListPane.BorderThickness = empty ? new Thickness(0,0,0,1) : new Thickness(0,0,1,0);
        SearchTools.MaxWidth = empty ? 440 : double.PositiveInfinity;
        SearchTools.HorizontalAlignment = empty ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        if (empty) SearchTools.Width = 390; else SearchTools.ClearValue(WidthProperty);
        MessageList.Visibility = ListFooter.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ReaderPane, empty ? 1 : 0);
        Grid.SetColumn(ReaderPane, empty ? 0 : 1);
        Grid.SetColumnSpan(ReaderPane, empty ? 2 : 1);
        var filtered = !string.IsNullOrWhiteSpace(SearchBox.Text) || FromDate.SelectedDate is not null || ToDate.SelectedDate is not null;
        EmptyArt.Content = AdminUi.EmptyArt("MailIcon");
        MailboxEmptyTitle.Text = !empty ? "메시지를 선택하세요" : filtered ? "검색 결과가 없습니다" : box switch
        {
            "sent" => "보낸 메시지가 없습니다",
            "unread" => "미확인 메시지가 없습니다",
            "conversation" => "주고받은 메시지가 없습니다",
            _ => "받은 메시지가 없습니다"
        };
        MailboxEmptyDescription.Text = !empty ? "본문과 첨부파일을 확인할 수 있습니다." : filtered
            ? "검색어나 날짜 범위를 바꿔 보세요." : "메시지가 생기면 여기에서 확인할 수 있습니다.";
    }
    async void NewMessage(object sender, RoutedEventArgs e) => await main.WriteMessage();
    async void RefreshClick(object sender, RoutedEventArgs e) => await Run(() => Refresh());
    async void LoadMore(object sender, RoutedEventArgs e) => await Run(() => Refresh(true));
    async void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) await Run(() => Refresh()); }

    async void Reply(object sender, RoutedEventArgs e)
    {
        if (detail is not null) await main.WriteMessage(detail.Message.SenderId, "답장: " + detail.Message.Title);
    }
    // Forwarding starts a new message with the original text; attachments stay with the original and are not re-sent.
    async void Forward(object sender, RoutedEventArgs e)
    {
        if (detail is null) return; var m = detail.Message;
        await main.WriteMessage(title: "전달: " + m.Title, forwardBody: string.Join(Environment.NewLine, "", "", "---------- 전달한 메시지 ----------", "보낸 사람: " + m.SenderName, "제목: " + m.Title, "", m.Body));
    }
    async void Recall(object sender,RoutedEventArgs e)
    {
        if(Api is null||detail is null)return;
        var id=detail.Message.Id;
        var prompt="메시지를 회수할까요? 읽은 사람을 포함한 전체 수신자의 메시지함과 첨부 접근에서 제거됩니다. 이미 저장한 사본은 회수할 수 없습니다.";
        if(detail.SubmissionRequest)prompt+="\n연결된 파일 제출 요청도 종료됩니다.";
        if(MessageBox.Show(this,prompt,"메시지 회수",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        await Run(async()=>{await Api.Send<JsonElement>(HttpMethod.Post,$"api/messages/{id}/recall");main.Archive.Remove(id);detail=null;DetailBody.Text="";DetailPanel.Visibility=Visibility.Collapsed;EmptyHint.Visibility=Visibility.Visible;await Refresh();StatusLabel.Text="메시지를 회수했습니다.";});
    }
    internal async Task VerifyRecallState()
    {
        var me=Api!.Session!.User!.Id;
        var other=(await Api.Get<UserInfo[]>("api/admin/users")).First(u=>u.Active&&u.Id!=me);
        var saved=await Api.Send<JsonElement>(HttpMethod.Post,"api/messages",new{clientId=Guid.NewGuid().ToString(),title="회수 버튼 상태 검증",body="가상 읽음 상태 검사",recipientIds=new[]{me,other.Id},attachmentIds=Array.Empty<string>(),all=false});
        var id=saved.GetProperty("id").GetString()!;
        await OpenDetail(id);
        if(!RecallButton.IsEnabled||detail!.Recipients.Count(r=>r.ReadAt is not null)!=1)throw new InvalidOperationException("일부 읽음 상태에서 회수 버튼이 활성화되지 않았습니다.");
        using var reader=new Api(Api.Address.AbsoluteUri);await reader.Login(other.Username,Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!);
        await reader.Get<Detail>("api/messages/"+id);await OpenDetail(id);
        if(RecallButton.IsEnabled)throw new InvalidOperationException("모두 읽은 상태에서 회수 버튼이 활성화되었습니다.");
    }
    void OpenSubmission(object sender, RoutedEventArgs e) { if (detail is not null) new WorkWindow(main, detail.SubmissionRequestId ?? detail.Message.Id).Show(); }
    async void Download(object sender, RoutedEventArgs e)
    {
        if (Api is null || ((System.Windows.Controls.Button)sender).Tag is not Attachment file) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = file.Name };
        if (dialog.ShowDialog(this) == true) await Run(async () => { if(await AttachmentTransferWindow.Save(this,Api,file.Id,dialog.FileName))StatusLabel.Text = "파일을 저장했습니다."; });
    }

}

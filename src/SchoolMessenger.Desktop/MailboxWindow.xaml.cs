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
        Owner = main; Resources = main.Resources; InitializeComponent();
        MessageList.ItemsSource = messages;
        Title = person is null ? "온라인 교무실 · 메시지함" : person.Name + " · 메시지 관리함";
        BoxTitle.Text = person is null ? box switch { "sent" => "보낸 메시지", "unread" => "미확인 메시지", _ => "받은 메시지" } : person.Name + " 관련 메시지"; UpdateTabs();
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
            var path = $"api/messages?box={box}&q={Uri.EscapeDataString(SearchBox.Text)}&offset={(more ? messages.Count : 0)}";
            if (FromDate.SelectedDate is { } from) path += $"&from={new DateTimeOffset(from).ToUnixTimeMilliseconds()}";
            if (ToDate.SelectedDate is { } to) path += $"&to={new DateTimeOffset(to.AddDays(1)).ToUnixTimeMilliseconds() - 1}";
            if (personId is not null) path += "&person=" + Uri.EscapeDataString(personId);
            var rows = await Api.Get<MessageItem[]>(path);
            // A list refresh started earlier must not clear or replace a message the user just opened.
            if(detailsOpening>0||selectionVersion!=taskPromptVersion||detail?.Message.Id!=selected)return;
            rendering = true;
            if (!more) messages.Clear();
            foreach (var row in rows) if (!messages.Any(m => m.Id == row.Id)) messages.Add(row with { IsSent = row.SenderId == Api.Session!.User!.Id });
            MessageList.SelectedItem = messages.FirstOrDefault(m => m.Id == selected);
            rendering = false;
            ListHint.Text = messages.Count == 0 ? "메시지가 없습니다. 검색 조건도 확인하세요." : $"{messages.Count}개 표시 · 한 번에 100개 조회";
            if (selected is not null && messages.Any(m=>m.Id==selected)) await OpenDetail(selected, markRead: false);
            else if(!more){detail=null;ResetTaskPrompt();DetailBody.Text="";DetailTitle.Text="";FileList.ItemsSource=null;ReceiptList.ItemsSource=null;DetailPanel.Visibility=Visibility.Collapsed;EmptyHint.Visibility=Visibility.Visible;}
            ConnectionLabel.Text = "첨부파일은 업로드 후 30일간 보관됩니다.";
            StatusLabel.Text = "";
        }
        finally { refreshing = false; rendering = false; }
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
        detail = opened;
        DetailTitle.Text = SchoolMessenger.Shared.MessageText.Excerpt(detail.Message.Title, 200);
        DetailTitle.Visibility = detail.Message.Title == SchoolMessenger.Shared.MessageText.Excerpt(detail.Message.Body, 80) ? Visibility.Collapsed : Visibility.Visible;
        DetailMeta.Text = $"{detail.Message.SenderName}   ·   {DateTimeOffset.FromUnixTimeMilliseconds(detail.Message.CreatedAt).ToLocalTime():yyyy.MM.dd HH:mm}   ·   수신 {detail.Recipients.Length}명";
        DetailBody.SetFormattedText(detail.Message.Body, detail.Message.Format);
        try{main.Archive.Save(detail);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){StatusLabel.Text="메시지는 서버에 보관됐지만 PC 암호화 사본을 저장하지 못했습니다.";}
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
        EmptyHint.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        await LoadTaskPrompt(id);
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
        box = (string)((System.Windows.Controls.Button)sender).Tag; UpdateTabs();
        BoxTitle.Text = box switch { "unread" => "미확인 메시지", "sent" => "보낸 메시지", _ => "받은 메시지" };
        detail = null; ResetTaskPrompt(); DetailPanel.Visibility = Visibility.Collapsed; EmptyHint.Visibility = Visibility.Visible;
        await Run(() => Refresh());
    }
    void UpdateTabs()
    {
        foreach (var tab in new[] { ReceivedTab, UnreadTab, SentTab })
        {
            tab.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, (string)tab.Tag == box ? "Blue" : "Muted");
            tab.ClearValue(System.Windows.Controls.Button.BorderBrushProperty);
            if ((string)tab.Tag == box) tab.SetResourceReference(System.Windows.Controls.Button.BorderBrushProperty, "Blue");
        }
    }
    async void NewMessage(object sender, RoutedEventArgs e) => await main.WriteMessage();
    async void RefreshClick(object sender, RoutedEventArgs e) => await Run(() => Refresh());
    async void LoadMore(object sender, RoutedEventArgs e) => await Run(() => Refresh(true));
    async void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) await Run(() => Refresh()); }

    async void Reply(object sender, RoutedEventArgs e)
    {
        if (detail is not null) await main.WriteMessage(detail.Message.SenderId, "답장: " + detail.Message.Title);
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
        if (dialog.ShowDialog(this) == true) await Run(async () => { await Api.Download(file.Id, dialog.FileName); StatusLabel.Text = "파일을 저장했습니다."; });
    }

}

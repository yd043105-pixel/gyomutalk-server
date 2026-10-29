using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class MailboxWindow
{
    int taskPromptVersion;
    bool savingTaskDecision;
    JsonElement[] taskCandidates = [];
    readonly Dictionary<string, CheckBox> taskChoices = new();

    void ResetTaskPrompt()
    {
        taskPromptVersion++;
        TaskPrompt.Visibility = Visibility.Collapsed;
        taskCandidates = []; taskChoices.Clear(); TaskCandidateList.Children.Clear();
    }

    async Task LoadTaskPrompt(string messageId)
    {
        if (Api is null) return;
        var version = taskPromptVersion;
        try
        {
            var rows = await Api.Get<JsonElement[]>($"api/todos?sourceId={Uri.EscapeDataString(messageId)}");
            if (version != taskPromptVersion || detail?.Message.Id != messageId) return;
            taskCandidates = rows.Where(r => r.GetProperty("sourceId").GetString() == messageId && r.GetProperty("status").GetString() == "suggested").ToArray();
            if (taskCandidates.Length == 0) return;
            TaskPromptTitle.Text = $"할 일 {taskCandidates.Length}개를 찾았어요. 추가할까요?";
            TaskPromptFeedback.Text = "선택하지 않은 항목은 추가하지 않습니다. 기한은 할 일 목록에서 수정할 수 있어요.";
            foreach (var row in taskCandidates)
            {
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = row.GetProperty("title").GetString(), TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeights.SemiBold });
                var due = row.GetProperty("dueDate").GetString();
                var date = new TextBlock { Text = due is null ? "기한 없음 · 추가 후 설정" : "기한 " + due, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
                date.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); label.Children.Add(date);
                var choice = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 5, 0, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
                choice.Checked += (_, _) => UpdateTaskAction(); choice.Unchecked += (_, _) => UpdateTaskAction();
                taskChoices.Add(row.GetProperty("id").GetString()!, choice); TaskCandidateList.Children.Add(choice);
            }
            TaskPromptActions.IsEnabled = TaskCandidateList.IsEnabled = true;
            UpdateTaskAction(); TaskPrompt.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            if (version == taskPromptVersion && detail?.Message.Id == messageId)
                StatusLabel.Text = "할 일 후보를 불러오지 못했습니다. 메시지를 다시 열거나 할 일 목록에서 확인하세요.";
        }
    }

    void UpdateTaskAction() => AddSuggestedTasks.IsEnabled = taskChoices.Values.Any(c => c.IsChecked == true);
    async void AddTasks(object sender, RoutedEventArgs e) => await SaveTaskDecision(true);
    async void DismissTasks(object sender, RoutedEventArgs e) => await SaveTaskDecision(false);

    async Task SaveTaskDecision(bool add)
    {
        if (Api is null || savingTaskDecision || detail is null) return;
        var messageId = detail.Message.Id; var version = taskPromptVersion;
        var choices = taskCandidates.Select(row => (Row: row, Add: add && taskChoices[row.GetProperty("id").GetString()!].IsChecked == true)).ToArray();
        savingTaskDecision = true; TaskPromptActions.IsEnabled = TaskCandidateList.IsEnabled = false;
        try
        {
            foreach (var choice in choices)
                await Api.Send<JsonElement>(HttpMethod.Patch, "api/todos/" + choice.Row.GetProperty("id").GetString(), new
                {
                    title = choice.Row.GetProperty("title").GetString(), dueDate = choice.Row.GetProperty("dueDate").GetString(),
                    status = choice.Add ? "open" : "dismissed", revision = choice.Row.GetProperty("revision").GetInt32()
                });
            if (version != taskPromptVersion || detail?.Message.Id != messageId) return;
            ResetTaskPrompt();
            StatusLabel.Text = add ? $"할 일 {choices.Count(c => c.Add)}개를 추가했습니다. 할 일 목록에서 D-Day와 기한을 확인하세요." : "할 일에 추가하지 않았습니다.";
        }
        catch (Exception error)
        {
            if (version != taskPromptVersion || detail?.Message.Id != messageId) return;
            ResetTaskPrompt(); await LoadTaskPrompt(messageId);
            StatusLabel.Text = "일부 항목을 처리하지 못했습니다. 남은 후보를 확인해 주세요. " + error.Message;
        }
        finally
        {
            savingTaskDecision = false;
            if (version == taskPromptVersion) TaskPromptActions.IsEnabled = TaskCandidateList.IsEnabled = true;
        }
    }
}

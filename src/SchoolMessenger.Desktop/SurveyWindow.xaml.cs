using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class SurveyWindow : Window
{
    readonly MainWindow main;
    readonly Person[] people;
    readonly List<(TextBox Text, ComboBox Kind, CheckBox Required, TextBox Options, Border Card)> questions = [];
    readonly List<Control> answers = [];
    string[] targetIds = [];
    SurveyDetail? current;
    bool busy, suppressSelection;
    int detailVersion, listVersion;
    string? desiredSurveyId;
    public SurveyWindow(MainWindow main, Person[] people, string[]? targets = null)
    {
        this.main = main; this.people = people; Owner = main; InitializeComponent();
        DeadlineDate.SelectedDate = DateTime.Today.AddDays(7);
        SendAll.Visibility = main.Api!.Session!.User is { IsAdmin: true } or { CanBroadcast: true } ? Visibility.Visible : Visibility.Collapsed;
        AddQuestion(); if (targets is not null) { targetIds = targets; TargetLabel.Text = string.Join(", ", people.Where(p => targets.Contains(p.Id)).Select(p => p.Label)); Tabs.SelectedIndex = 2; }
        main.SurveyChanged += Changed; Closed += (_, _) => main.SurveyChanged -= Changed;
        Loaded += async (_, _) => await Run(() => Reload());
    }
    async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { Feedback.Text = error.Message; } }
    async void Changed(string id) => await Run(async () => { await Reload(); if (current?.Id == id && ResultsTab.IsEnabled) await RenderResults(); });
    public async Task Reload(string? select = null)
    {
        if (main.Api is null) return;
        if (select is not null) desiredSurveyId = select;
        var version = ++listVersion;
        var surveys = await main.Api.Get<SurveyItem[]>("api/surveys");
        if (version != listVersion) return;
        var id = desiredSurveyId ?? (SurveyList.SelectedItem as SurveyItem)?.Id;
        suppressSelection = true; SurveyList.ItemsSource = surveys; SurveyList.SelectedItem = surveys.FirstOrDefault(s => s.Id == id) ?? surveys.FirstOrDefault(); suppressSelection = false;
        if (current is not null && !current.Closed && surveys.Any(s => s.Id == current.Id && s.Closed))
        { current = current with { Closed = true }; AnswerButton.IsEnabled = false; foreach (var field in answers) field.IsEnabled = false; DeadlineLabel.Text += " · 종료"; }
        if (select is not null || (current is null && SurveyList.SelectedItem is SurveyItem)) await ShowSurvey(((SurveyItem)SurveyList.SelectedItem!).Id);
    }
    async void SelectSurvey(object sender, SelectionChangedEventArgs e)
    { if (!suppressSelection && SurveyList.SelectedItem is SurveyItem survey) { Tabs.SelectedIndex = 0; await Run(() => ShowSurvey(survey.Id)); } }
    public async Task ShowSurvey(string id)
    {
        desiredSurveyId = id;
        var version = ++detailVersion;
        var detail = await main.Api!.Get<SurveyDetail>($"api/surveys/{id}"); if (version != detailVersion) return;
        current = detail; Heading.Text = current.Title; Description.Text = current.Description;
        DeadlineLabel.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds(current.Deadline).ToLocalTime():yyyy.MM.dd HH:mm} 마감 · {(current.Closed ? "종료" : "진행 중")}";
        ResultsTab.IsEnabled = current.OwnerId == main.Api.Session!.User!.Id; AnswerButton.IsEnabled = current.CanAnswer && !current.Closed;
        AnswerPanel.Children.Clear(); answers.Clear();
        for (var i = 0; i < current.Questions.Length; i++)
        {
            var question = current.Questions[i]; var value = current.Answers?.ElementAtOrDefault(i) ?? "";
            AnswerPanel.Children.Add(new TextBlock { Text = $"{i + 1}. {question.Text}" + (question.Required ? " *" : ""), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8) });
            Control field;
            if (question.Kind == "choice") { var combo = new ComboBox { ItemsSource = new[] { "" }.Concat(question.Options).ToArray(), SelectedItem = value }; field = combo; }
            else field = new TextBox { Text = value, MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
            field.IsEnabled = current.CanAnswer && !current.Closed; answers.Add(field); AnswerPanel.Children.Add(field);
        }
        if (ResultsTab.IsEnabled) await RenderResults(); else ResultsPanel.Children.Clear();
    }
    void NewSurvey(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;
    void ChooseTargets(object sender, RoutedEventArgs e)
    { var picker = new RecipientWindow(main, people, false, targetIds); if (picker.ShowDialog() == true) { targetIds = picker.SelectedIds; TargetLabel.Text = string.Join(", ", people.Where(p => targetIds.Contains(p.Id)).Select(p => p.Label)); } }
    void AddQuestionClick(object sender, RoutedEventArgs e) => AddQuestion();
    internal void ResetQuestionsForVerification() { questions.Clear(); QuestionsPanel.Children.Clear(); }
    public void AddQuestion(string text = "", string kind = "choice", string[]? options = null)
    {
        if (questions.Count >= 20) { Feedback.Text = "문항은 최대 20개입니다."; return; }
        var panel = new StackPanel(); var card = new Border { BorderBrush = (System.Windows.Media.Brush)FindResource("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0), Child = panel };
        var header = new DockPanel(); var remove = new Button { Content = "삭제", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(7, 3, 7, 3) }; DockPanel.SetDock(remove, Dock.Right); header.Children.Add(remove); header.Children.Add(new TextBlock { Text = "문항", FontWeight = FontWeights.SemiBold }); panel.Children.Add(header);
        var title = new TextBox { Text = text, MaxLength = 500, Margin = new Thickness(0, 8, 0, 8), ToolTip = "질문 내용" }; panel.Children.Add(title);
        var row = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(row);
        var type = new ComboBox { ItemsSource = new[] { "객관식", "주관식" }, SelectedIndex = kind == "text" ? 1 : 0, Width = 130 }; row.Children.Add(type);
        var required = new CheckBox { Content = "필수 응답", IsChecked = true, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(required);
        var hint = new TextBlock { Text = "선택지 · 한 줄에 하나씩", FontSize = 11, Margin = new Thickness(0, 10, 0, 6) }; panel.Children.Add(hint);
        var choices = new TextBox { Text = string.Join("\n", options ?? new[] { "찬성", "반대" }), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 65, MaxLength = 4200 }; panel.Children.Add(choices);
        void SetKind() => hint.Visibility = choices.Visibility = type.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        type.SelectionChanged += (_, _) => SetKind(); SetKind();
        questions.Add((title, type, required, choices, card)); QuestionsPanel.Children.Add(card);
        remove.Click += (_, _) => { questions.RemoveAll(q => q.Card == card); QuestionsPanel.Children.Remove(card); };
    }
    async void CreateClick(object sender, RoutedEventArgs e) => await Run(CreateSurvey);
    public async Task CreateSurvey()
    {
        if (busy) return;
        if (!TimeSpan.TryParseExact(DeadlineTime.Text.Trim(), @"hh\:mm", null, out var time) || DeadlineDate.SelectedDate is not DateTime date) { Feedback.Text = "마감일과 시간을 확인하세요. 예: 17:00"; return; }
        if (SendAll.IsChecked == true && MessageBox.Show(this, "전체 교직원에게 설문을 보내시겠습니까?", "전체 발송", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var qs = questions.Select(q => new SurveyQuestion(q.Text.Text.Trim(), q.Kind.SelectedIndex == 0 ? "choice" : "text", q.Required.IsChecked == true,
            q.Kind.SelectedIndex == 0 ? q.Options.Text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).ToArray() : [])).ToArray();
        busy = true; CreateButton.IsEnabled = false;
        try
        {
            var result = await main.Api!.Send<JsonElement>(HttpMethod.Post, "api/surveys", new { title = TitleBox.Text, description = DescriptionBox.Text, deadline = new DateTimeOffset(date.Date + time).ToUnixTimeMilliseconds(), questions = qs, targetIds, all = SendAll.IsChecked == true });
            var id = result.GetProperty("id").GetString()!;
            await ShowSurvey(id); await Reload(id); Tabs.SelectedIndex = 0; Feedback.Text = "설문을 보냈습니다.";
            TitleBox.Clear(); DescriptionBox.Clear(); questions.Clear(); QuestionsPanel.Children.Clear(); AddQuestion();
        }
        finally { busy = false; CreateButton.IsEnabled = true; }
    }
    async void SaveAnswer(object sender, RoutedEventArgs e) => await Run(() => SubmitAnswers());
    public async Task SubmitAnswers(string[]? supplied = null)
    {
        if (current is null || busy) return; busy = true; AnswerButton.IsEnabled = false;
        try
        {
            var values = supplied ?? answers.Select(a => a is TextBox text ? text.Text : ((ComboBox)a).SelectedItem?.ToString() ?? "").ToArray();
            await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/surveys/{current.Id}/answers", new { answers = values });
            await ShowSurvey(current.Id); await Reload(); Feedback.Text = "응답을 저장했습니다. 마감 전에는 수정할 수 있습니다.";
        }
        finally { busy = false; AnswerButton.IsEnabled = current?.CanAnswer == true && !current.Closed; }
    }
    async void LoadResults(object sender, RoutedEventArgs e) => await Run(RenderResults);
    async Task RenderResults()
    {
        if (current is null) return;
        var id = current.Id; var result = await main.Api!.Get<SurveyResults>($"api/surveys/{id}/results"); if (current?.Id != id) return;
        ResultsPanel.Children.Clear();
        void Line(string text, bool bold = false) => ResultsPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 6, 0, 6) });
        Line($"응답 {result.Responses.Length}명", true);
        for (var i = 0; i < result.Questions.Length; i++)
        {
            var q = result.Questions[i]; Line($"{i + 1}. {q.Text}", true);
            if (q.Kind == "choice") foreach (var option in q.Options) Line($"{option}: {result.Responses.Count(r => r.Answers[i] == option)}명");
            foreach (var response in result.Responses) Line($"{response.Name} · {response.Department}: {response.Answers[i]}");
        }
    }
    async void CloseSurvey(object sender, RoutedEventArgs e)
    {
        if (current is null || MessageBox.Show(this, "설문을 종료하면 응답을 받거나 수정할 수 없습니다. 종료하시겠습니까?", "설문 종료", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(async () => { await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/surveys/{current.Id}/close"); await ShowSurvey(current.Id); await Reload(); });
    }
    public static string Csv(SurveyResults results)
    {
        static string Cell(string value) { if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        var rows = new List<string> { string.Join(",", new[] { "이름", "부서", "응답 시각" }.Concat(results.Questions.Select(q => q.Text)).Select(Cell)) };
        rows.AddRange(results.Responses.Select(r => string.Join(",", new[] { r.Name, r.Department, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm") }.Concat(r.Answers).Select(Cell))));
        return string.Join("\r\n", rows);
    }
    async void ExportResults(object sender, RoutedEventArgs e)
    {
        if (current is null) return; var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "설문결과.csv", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog(this) == true) await Run(async () => { var result = await main.Api!.Get<SurveyResults>($"api/surveys/{current.Id}/results"); File.WriteAllText(dialog.FileName, Csv(result), new UTF8Encoding(true)); Feedback.Text = "CSV 파일을 저장했습니다."; });
    }
}

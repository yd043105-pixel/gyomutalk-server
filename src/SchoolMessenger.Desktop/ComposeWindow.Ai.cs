using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SchoolMessenger.Desktop;

// ‘AI로 다듬기’: the selected words (or the whole body) go to the AI the teacher connected, and the suggestion is shown
// next to the original; nothing in the message changes until the teacher presses ‘바꾸기’.
public partial class ComposeWindow
{
    Button? aiButton;

    void InitializeAi()
    {
        if (!main.Feature("ai") || DraftListButton.Parent is not Panel bar) return;
        aiButton = new Button { Content = "AI로 다듬기 ▾", Style = (Style)FindResource("SubtleButton"), Margin = new Thickness(12, 0, 0, 0), ToolTip = "고른 글(없으면 본문 전체)을 AI가 다듬습니다. 바꾸기 전에 먼저 보여 줍니다." };
        AutomationProperties.SetName(aiButton, "AI로 다듬기");
        var menu = new ContextMenu();
        foreach (var rewrite in Ai.Rewrites)
        {
            var item = new MenuItem { Header = rewrite.Label }; item.Click += async (_, _) => await Rewrite(rewrite);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "AI 연결 설정…" }; settings.Click += (_, _) => new AiWindow(this).ShowDialog(); menu.Items.Add(settings);
        aiButton.Click += (_, _) => { menu.PlacementTarget = aiButton; menu.IsOpen = true; };
        bar.Children.Add(aiButton);
    }

    // 보내기 전 확인: only when the check finds something, and the teacher may still send it as it is.
    bool StyleChecked()
    {
        var found = StyleCheck.Check(TitleBox.Text + "\n" + BodyBox.Text);
        if (found.Length == 0) return true;
        var lines = string.Join("\n", found.Take(6).Select(f => $"· [{f.Kind}] ‘{f.Found}’ → {f.Instead}")) + (found.Length > 6 ? $"\n· 그 밖에 {found.Length - 6}건" : "");
        if (Notice.Show(this, $"보내기 전에 확인할 표현이 {found.Length}건 있습니다.\n\n{lines}\n\n그대로 보낼까요? ‘아니요’를 누르면 고칠 수 있습니다.", "보내기 전 확인", MessageBoxButton.YesNo) == MessageBoxResult.Yes) return true;
        Feedback.Text = "표현을 고친 뒤 다시 보내세요." + (aiButton is null ? "" : " ‘AI로 다듬기’로 고칠 수도 있습니다."); return false;
    }

    async Task Rewrite(Ai.Rewrite rewrite)
    {
        // Nothing connected yet: the connection window opens right away, and the rewrite goes on once a model is picked.
        if (!Ai.Ready)
        {
            new AiWindow(this, "‘AI로 다듬기’를 쓰려면 먼저 AI를 연결하세요. 아래에서 하나를 연결하고 ‘완료’를 누르면 바로 다듬습니다.").ShowDialog();
            if (!Ai.Ready) { Feedback.Text = "AI를 연결하지 않아 다듬지 않았습니다."; return; }
        }
        var selection = BodyBox.Selection;
        var whole = selection.IsEmpty;
        var original = (whole ? BodyBox.Text : selection.Text).Trim();
        if (original.Length == 0) { Feedback.Text = "다듬을 글을 먼저 쓰세요."; return; }
        var provider = Ai.InUse!;
        aiButton!.IsEnabled = false; Feedback.Text = Ai.Name(provider) + "가 " + (whole ? "본문을" : "고른 글을") + " 다듬는 중…";
        try
        {
            using var cancel = new CancellationTokenSource();
            var answer = await Ai.Ask(rewrite.Instructions, original, cancel.Token);
            Feedback.Text = "";
            var result = new AiResultWindow(this, rewrite.Label, provider, original, answer);
            if (result.ShowDialog() != true) return;
            if (whole) BodyBox.Text = result.Text; else selection.Text = result.Text;
            Feedback.Text = whole ? "본문을 다듬은 글로 바꿨습니다. 글자 서식은 다시 지정하세요." : "고른 글을 다듬은 글로 바꿨습니다.";
        }
        catch (AiException e) { Feedback.Text = e.Message; }
        finally { aiButton.IsEnabled = true; }
    }
}

// The original and the suggestion side by side; the suggestion can be edited before it goes into the message.
public sealed class AiResultWindow : Window
{
    readonly TextBox suggestion;
    public string Text => suggestion.Text;

    public AiResultWindow(Window owner, string label, string provider, string original, string answer)
    {
        Owner = owner; Title = "AI로 다듬기 · " + label; Width = 820; Height = 560; MinWidth = 560; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource("SchoolWindow");
        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new DockPanel { Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var replace = new Button { Content = "바꾸기", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        var keep = new Button { Content = "그대로 두기", IsCancel = true };
        replace.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(keep); buttons.Children.Add(replace);
        DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons);
        var source = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        source.Inlines.Add(new Run(Ai.Name(provider) + " · " + Ai.Current.Of(provider).Model));
        if (provider == Ai.ChatGpt)
        {
            source.Inlines.Add(new Run(" · Using ChatGPT plan · "));
            var manage = new Hyperlink(new Run("Manage usage")); manage.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ai.ManageUsage) { UseShellExecute = true });
            source.Inlines.Add(manage);
        }
        actions.Children.Add(source); root.Children.Add(actions);
        var hint = AdminUi.Label("AI가 고친 글은 틀릴 수 있습니다. 날짜·숫자·이름을 확인하고, 필요하면 오른쪽 글을 직접 고친 뒤 ‘바꾸기’를 누르세요.", "Muted", 12);
        hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(0, 0, 0, 10); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        TextBox Pane(string text, bool editable, int column, string title)
        {
            var heading = AdminUi.Label(title, "Ink", 13, true); heading.Margin = new Thickness(0, 0, 0, 6); Grid.SetColumn(heading, column); grid.Children.Add(heading);
            var box = new TextBox { Text = text, IsReadOnly = !editable, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10) };
            AutomationProperties.SetName(box, title); Grid.SetColumn(box, column); Grid.SetRow(box, 1); grid.Children.Add(box); return box;
        }
        Pane(original, false, 0, "원래 글");
        suggestion = Pane(answer, true, 2, "다듬은 글");
        root.Children.Add(grid);
        Content = root;
    }
}

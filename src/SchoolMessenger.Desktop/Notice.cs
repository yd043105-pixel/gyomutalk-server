using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// The program's own message box: same calls and results as MessageBox.Show, drawn with the school window style.
static class Notice
{
    static readonly Regex Destructive = new("삭제|나가기|나갈|회수|종료|지울|제거|초기화|비활성", RegexOptions.CultureInvariant);

    public static MessageBoxResult Show(string text, string caption = "교무톡") => Show(null, text, caption);
    public static MessageBoxResult Show(Window? owner, string text, string caption = "교무톡", MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var result = button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.No : button == MessageBoxButton.OKCancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "교무톡" : caption, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null, WindowStartupLocation = owner is { IsLoaded: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        dialog.Style = (Style)Application.Current.FindResource("SchoolWindow");
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        message.SetResourceReference(TextBlock.ForegroundProperty, image is MessageBoxImage.Error ? "Danger" : "Ink");
        body.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        body.Children.Add(actions);
        Button Add(string label, MessageBoxResult value, bool primary)
        {
            var action = new Button { Content = label, MinWidth = 76, Margin = new Thickness(8, 0, 0, 0) };
            if (primary) action.SetResourceReference(FrameworkElement.StyleProperty, Destructive.IsMatch(text + caption) && button != MessageBoxButton.OK ? "DangerButton" : "PrimaryButton");
            action.Click += (_, _) => { result = value; dialog.Close(); };
            actions.Children.Add(action); return action;
        }
        var buttons = button switch
        {
            MessageBoxButton.YesNo => new[] { Add("아니요", MessageBoxResult.No, false), Add("예", MessageBoxResult.Yes, true) },
            MessageBoxButton.YesNoCancel => new[] { Add("취소", MessageBoxResult.Cancel, false), Add("아니요", MessageBoxResult.No, false), Add("예", MessageBoxResult.Yes, true) },
            MessageBoxButton.OKCancel => new[] { Add("취소", MessageBoxResult.Cancel, false), Add("확인", MessageBoxResult.OK, true) },
            _ => new[] { Add("확인", MessageBoxResult.OK, true) }
        };
        buttons[0].IsCancel = true;
        var preferred = defaultResult == MessageBoxResult.None ? buttons[^1] : buttons.FirstOrDefault(b => b.Content as string == defaultResult switch
        { MessageBoxResult.Yes => "예", MessageBoxResult.No => "아니요", MessageBoxResult.Cancel => "취소", _ => "확인" }) ?? buttons[^1];
        preferred.IsDefault = true;
        dialog.Content = body;
        dialog.Loaded += (_, _) => preferred.Focus();
        if (image is MessageBoxImage.Warning or MessageBoxImage.Error) System.Media.SystemSounds.Exclamation.Play();
        dialog.ShowDialog();
        return result;
    }
}

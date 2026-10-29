using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    bool updateNoticeOpen;
    void OpenUpdates(object sender, RoutedEventArgs e) => ShowUpdates(true);

    void ShowUpdates(bool manual = false)
    {
        if (updateNoticeOpen) return;
        try
        {
            var notice = ReleaseNotice.Load("desktop");
            var path = Path.Combine(LocalDirectory, "update-seen.txt");
            var confirmed = 0;
            try { if (File.Exists(path)) int.TryParse(File.ReadAllText(path), out confirmed); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!manual && !notice.ShouldShow(confirmed)) return;
            updateNoticeOpen = true;
            var window = new Window { Owner = this, Title = "업데이트 내역 · " + notice.Version, Width = 430, Height = 440,
                MaxHeight = SystemParameters.WorkArea.Height - 30, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Style = (Style)FindResource("SchoolWindow") };
            var layout = new DockPanel { Margin = new Thickness(20) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            var later = new Button { Content = "나중에", IsCancel = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
            later.Click += (_, _) => window.DialogResult = false;
            var accept = new Button { Content = "확인", IsDefault = true, Padding = new Thickness(14, 8, 14, 8), Style = (Style)FindResource("PrimaryButton") };
            accept.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(later); buttons.Children.Add(accept); layout.Children.Add(buttons);
            layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = notice.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 23 } });
            window.Content = layout;
            if (window.ShowDialog() == true)
            {
                try
                {
                    if (File.Exists(path) && int.TryParse(File.ReadAllText(path), out var latest)) confirmed = Math.Max(confirmed, latest);
                    File.WriteAllText(path, Math.Max(confirmed, notice.Sequence).ToString());
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { StatusLabel.Text = "업데이트 확인 기록을 저장하지 못했습니다. 다음 로그인에서 다시 안내할 수 있습니다."; }
            }
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException)
        { StatusLabel.Text = "업데이트 내역을 읽지 못했습니다. 프로그램을 다시 설치해 주세요."; }
        finally { updateNoticeOpen = false; }
    }

    void VerifyUpdateNotice()
    {
        var notice = ReleaseNotice.Load("desktop");
        var path = Path.Combine(LocalDirectory, "update-seen.txt");
        if (File.Exists(path)) throw new InvalidOperationException("업데이트 검사는 새 테스트 폴더에서 실행하세요.");
        void Exercise(bool confirm, bool manual, bool expected)
        {
            var appeared = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var popup = OwnedWindows.Cast<Window>().FirstOrDefault(w => w.Title.StartsWith("업데이트 내역 · ", StringComparison.Ordinal));
                if (popup is null) return;
                appeared = true;
                if (confirm)
                {
                    Directory.CreateDirectory("artifacts");
                    SavePreview(popup, "artifacts/update-notice-preview.png");
                    var buttons = ((DockPanel)popup.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>();
                    buttons.Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else popup.Close();
            }));
            ShowUpdates(manual);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (appeared != expected) throw new InvalidOperationException("업데이트 팝업 표시 조건 오류");
        }
        Exercise(false, false, true);
        if (File.Exists(path)) throw new InvalidOperationException("닫기만 한 버전을 확인 처리했습니다.");
        Exercise(true, false, true);
        if (File.ReadAllText(path) != notice.Sequence.ToString()) throw new InvalidOperationException("업데이트 확인 기록 실패");
        Exercise(false, false, false);
        Exercise(false, true, true);
        File.WriteAllText(path, (notice.Sequence - 1).ToString());
        Exercise(true, false, true);
        File.WriteAllText(path, (notice.Sequence + 1).ToString());
        Exercise(false, false, false);
        File.WriteAllText(path, notice.Sequence.ToString());
        File.WriteAllText("artifacts/update-notice-check.json", System.Text.Json.JsonSerializer.Serialize(new { confirmed = notice.Sequence, checks = 6 }));
    }
}

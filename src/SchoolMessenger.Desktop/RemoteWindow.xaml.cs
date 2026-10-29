using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SchoolMessenger.Desktop;

public partial class RemoteWindow : Window
{
    readonly RemoteClient client;
    bool updating, closing;
    long lastMove;
    public RemoteWindow(MainWindow main, RemoteClient client) { this.client = client; Owner = main; InitializeComponent(); }
    public void Update(RemoteOffer offer)
    {
        var host = client.IsHost; var active = offer.State == "active";
        Heading.Text = host ? $"{offer.ViewerName} 선생님에게 내 화면 공유" : $"{offer.HostName} 선생님의 화면 보기";
        Status.Text = active ? (client.DirectConnected ? (offer.Control ? "PC 직접 연결 · 암호화 · 원격 제어 허용" : "PC 직접 연결 · 암호화 · 화면 보기만 허용") : "PC 직접 연결 중 · 화면은 서버를 거치지 않습니다.") : $"{offer.InitiatorName} 선생님의 요청 · 수락 후 연결됩니다.";
        updating = true; AllowControl.IsChecked = offer.Control; updating = false;
        AllowControl.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Visibility = !active && client.IsTarget ? Visibility.Visible : Visibility.Collapsed;
        ScreenBorder.Visibility = active && !host ? Visibility.Visible : Visibility.Collapsed;
        if (host || !active) { Width = 510; Height = 310; Topmost = active && host; } else { Width = 900; Height = 660; Topmost = false; }
        if (active && host) Owner = null;
        StopButton.Content = active ? "지원 종료" : client.IsTarget ? "거절" : "요청 취소";
        if (active && !host) ScreenBorder.Focus();
    }
    public void Frame(byte[] jpeg)
    {
        if (!RemoteDesktop.ValidFrame(jpeg)) throw new InvalidOperationException("잘못된 화면 데이터입니다.");
        using var stream = new MemoryStream(jpeg); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        if (image.PixelWidth > 1280 || image.PixelHeight > 1280) throw new InvalidOperationException("화면 크기 제한을 넘었습니다."); ScreenImage.Source = image;
    }
    async void Accept(object sender, RoutedEventArgs e)
    { AcceptButton.IsEnabled = false; try { await client.Accept(AllowControl.IsChecked == true); } catch (Exception error) { Feedback.Text = error.Message; AcceptButton.IsEnabled = true; } }
    async void Stop(object sender, RoutedEventArgs e) => await client.Stop();
    async void PermissionChanged(object sender, RoutedEventArgs e)
    { if (updating || client.Offer is null || (client.Offer.State == "pending" && client.IsTarget)) return; try { await client.SetControl(AllowControl.IsChecked == true); } catch (Exception error) { Feedback.Text = error.Message; await client.Stop(); } }
    bool Point(MouseEventArgs e, out double x, out double y)
    {
        x = y = 0; if (!client.CanControl || ScreenImage.Source is not BitmapSource image) return false;
        var scale = Math.Min(ScreenImage.ActualWidth / image.PixelWidth, ScreenImage.ActualHeight / image.PixelHeight);
        if (scale <= 0) return false;
        var position = e.GetPosition(ScreenImage); var width = image.PixelWidth * scale; var height = image.PixelHeight * scale;
        x = (position.X - (ScreenImage.ActualWidth - width) / 2) / width; y = (position.Y - (ScreenImage.ActualHeight - height) / 2) / height;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }
    async void MoveRemote(object sender, MouseEventArgs e)
    { if (Environment.TickCount64 - lastMove < 40 || !Point(e, out var x, out var y)) return; lastMove = Environment.TickCount64; await client.Input(new RemoteInput("move", x, y)); }
    async void MouseDownRemote(object sender, MouseButtonEventArgs e)
    { if (!Point(e, out var x, out var y) || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return; e.Handled = true; ScreenBorder.Focus(); ScreenImage.CaptureMouse(); await client.Input(new RemoteInput(e.ChangedButton == MouseButton.Left ? "left" : "right", x, y, Down: true)); }
    async void MouseUpRemote(object sender, MouseButtonEventArgs e)
    { ScreenImage.ReleaseMouseCapture(); if (!client.CanControl || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return; var inside = Point(e, out var x, out var y); await client.Input(new RemoteInput(e.ChangedButton == MouseButton.Left ? "left" : "right", inside ? x : .5, inside ? y : .5, Down: false)); e.Handled = true; }
    async void WheelRemote(object sender, MouseWheelEventArgs e)
    { if (Point(e, out var x, out var y)) { e.Handled = true; await client.Input(new RemoteInput("wheel", x, y, Math.Clamp(e.Delta, -1200, 1200))); } }
    async void KeyDownRemote(object sender, KeyEventArgs e)
    { if (!client.CanControl || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return; e.Handled = true; if (!e.IsRepeat) await client.Input(new RemoteInput("key", Value: KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), Down: true)); }
    async void KeyUpRemote(object sender, KeyEventArgs e)
    { if (!client.CanControl || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return; e.Handled = true; await client.Input(new RemoteInput("key", Value: KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), Down: false)); }
    async void TextRemote(object sender, TextCompositionEventArgs e)
    { if (!client.CanControl || string.IsNullOrEmpty(e.Text)) return; e.Handled = true; await client.Input(new RemoteInput("text", Text: e.Text)); }
    async void LostFocusRemote(object sender, KeyboardFocusChangedEventArgs e) { if (client.CanControl) await client.Input(new RemoteInput("release")); }
    void WindowClosing(object? sender, CancelEventArgs e) { if (closing) return; e.Cancel = true; _ = client.Stop(); }
    public void Finish(string reason) { closing = true; Feedback.Text = reason; Close(); }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    // Session membership is separate from native ownership: a work window may go behind the roster.
    readonly HashSet<Window> workWindows = [];
    internal Window[] SessionWindows => workWindows.Concat(OwnedWindows.Cast<Window>()).Distinct().ToArray();
    internal bool ClosingWorkWindows => closingWindows;
    internal bool HasOpenWork => SessionWindows.Any(w => w.IsVisible);

    internal void RegisterWorkWindow(Window window)
    {
        if (!workWindows.Add(window)) return;
        window.Loaded += Position;
        window.Closed += (_, _) => { window.Loaded -= Position; workWindows.Remove(window); };
        void Position(object sender, RoutedEventArgs args)
        {
            window.Loaded -= Position;
            var pixels = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var area = new Rect(transform.Transform(new Point(pixels.Left, pixels.Top)), transform.Transform(new Point(pixels.Right, pixels.Bottom)));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Math.Clamp(Left + (ActualWidth - window.ActualWidth) / 2, area.Left, Math.Max(area.Left, area.Right - window.ActualWidth));
            window.Top = Math.Clamp(Top + (ActualHeight - window.ActualHeight) / 2, area.Top, Math.Max(area.Top, area.Bottom - window.ActualHeight));
        }
    }

    void CloseSessionWindows()
    {
        closingWindows = true;
        try
        {
            foreach (var window in SessionWindows)
            {
                window.Close();
                // A subordinate modal or an operation may cancel closing; it must not expose a logged-out session.
                if (window.IsVisible) window.Hide();
            }
        }
        finally { closingWindows = false; }
    }
}

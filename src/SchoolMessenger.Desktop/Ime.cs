using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace SchoolMessenger.Desktop;

// A Korean syllable still being put together (the underlined 동 of 홍길동) belongs to the input method until it is
// committed. Some Windows input methods drop it instead when the focus leaves the box — for a password box, which turns
// Korean off, or a window that pops up — so the last letter of a name went missing at sign-in. Every text box here
// commits it before the focus leaves.
static class Ime
{
    [DllImport("imm32.dll")] static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll")] static extern bool ImmNotifyIME(nint context, int action, int index, int value);
    const int CompositionString = 0x15, Complete = 0x1;

    public static void Watch()
    {
        // Also raised when the whole window loses the keyboard to another window.
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.PreviewLostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((sender, _) => Commit((DependencyObject)sender)));
    }

    public static void Commit(DependencyObject element)
    {
        try
        {
            if (PresentationSource.FromDependencyObject(element) is not HwndSource { Handle: var window } || window == 0) return;
            var context = ImmGetContext(window);
            if (context == 0) return;
            ImmNotifyIME(context, CompositionString, Complete, 0);
            ImmReleaseContext(window, context);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SchoolMessenger.Desktop;

// Windows 11 rounds the real window, so nothing is left unpainted outside a drawn corner. Windows 10 ignores this and stays square.
static class WindowCorners
{
    const int CornerPreference=33,Round=2;
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    internal static void Register()=>EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
    {
        var handle=new WindowInteropHelper((Window)sender).Handle;var round=Round;
        if(handle!=IntPtr.Zero)DwmSetWindowAttribute(handle,CornerPreference,ref round,sizeof(int));
    }));
}

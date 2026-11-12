using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SchoolMessenger.Desktop;

// Windows 11 rounds the real window, so nothing is left unpainted outside a drawn corner. Windows 10 ignores this and stays square.
static class WindowCorners
{
    const int CornerPreference=33,Round=2,GetMinMaxInfo=0x24,NearestMonitor=2;
    [StructLayout(LayoutKind.Sequential)]struct Spot{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct Area{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct Limits{public Spot Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [StructLayout(LayoutKind.Sequential)]struct Monitor{public int Size;public Area Screen,Work;public int Flags;}
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromWindow(IntPtr window,int flags);
    [DllImport("user32.dll")]static extern bool GetMonitorInfo(IntPtr monitor,ref Monitor info);
    internal static void Register()=>EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>
    {
        var handle=new WindowInteropHelper((Window)sender).Handle;var round=Round;
        if(handle==IntPtr.Zero)return;
        DwmSetWindowAttribute(handle,CornerPreference,ref round,sizeof(int));
        // Loaded fires again when a window is shown again; RemoveHook keeps it to one hook.
        var source=HwndSource.FromHwnd(handle);source?.RemoveHook(KeepInsideWorkArea);source?.AddHook(KeepInsideWorkArea);
    }));
    // A window without the system frame maximizes over the whole monitor: its top is cut and its bottom sits behind the taskbar.
    // Telling Windows the work area keeps a maximized window fully visible. Not marked handled, so WPF still applies MinWidth and MinHeight.
    static IntPtr KeepInsideWorkArea(IntPtr window,int message,IntPtr w,IntPtr l,ref bool handled)
    {
        if(message!=GetMinMaxInfo)return IntPtr.Zero;
        var monitor=new Monitor{Size=Marshal.SizeOf<Monitor>()};
        if(!GetMonitorInfo(MonitorFromWindow(window,NearestMonitor),ref monitor))return IntPtr.Zero;
        var limits=Marshal.PtrToStructure<Limits>(l);
        limits.MaxPosition=new Spot{X=monitor.Work.Left-monitor.Screen.Left,Y=monitor.Work.Top-monitor.Screen.Top};
        limits.MaxSize=new Spot{X=monitor.Work.Right-monitor.Work.Left,Y=monitor.Work.Bottom-monitor.Work.Top};
        Marshal.StructureToPtr(limits,l,false);
        return IntPtr.Zero;
    }
}

using System;
using System.Runtime.InteropServices;

namespace SchoolMessenger.Desktop;

// Reads elapsed input time for this Windows session; never records keys or pointer coordinates.
internal static class IdleActivity
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetLastInputInfo(ref LastInput input);
    public static TimeSpan Elapsed(uint now,uint last)=>TimeSpan.FromMilliseconds(unchecked(now-last));
    public static TimeSpan? Read()
    {var input=new LastInput{Size=(uint)Marshal.SizeOf<LastInput>()};return GetLastInputInfo(ref input)?Elapsed(unchecked((uint)Environment.TickCount64),input.Tick):null;}
    public static bool IsAway(TimeSpan? idle,int minutes,bool locked,bool previous=false)=>locked||(idle is null?previous:idle>=TimeSpan.FromMinutes(minutes));
}

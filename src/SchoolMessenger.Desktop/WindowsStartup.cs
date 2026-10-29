using System.Reflection;
using Microsoft.Win32;

namespace SchoolMessenger.Desktop;

public sealed class WindowsStartup(string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run",string entryName="YeoyangSchoolMessenger",string? command=null)
{
    public bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(registryPath); return key?.GetValue(entryName) is string value&&value==(command??Command); }
    }
    public static string Command
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("자동 실행 경로를 확인할 수 없습니다.");
            var assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? " \"" + Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll") + "\"" : "";
            return "\"" + executable + "\"" + assembly + " --startup";
        }
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        if (enabled) key.SetValue(entryName, command??Command, RegistryValueKind.String);
        else if(key.GetValue(entryName) is string value&&value== (command??Command))key.DeleteValue(entryName, throwOnMissingValue: false);
    }
}

namespace SchoolMessenger.Contracts;

public static class AttachmentLimits
{
    public const long LegacyFile = 100L * 1024 * 1024;
    public const long GiB = 1024L * 1024 * 1024;
    public const int DefaultGiB = 2, MaximumGiB = 10;
    public const long Message = DefaultGiB * GiB, Maximum = MaximumGiB * GiB;
    public const long DiskReserve = 500L * 1024 * 1024;
    public static string Label(long bytes) => $"{bytes / GiB}GiB";
    public const string NameHeader = "X-Gyomutalk-File-Name";
}

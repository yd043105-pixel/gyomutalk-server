namespace SchoolMessenger.Contracts;

public static class AttachmentLimits
{
    public const long LegacyFile = 100L * 1024 * 1024;
    public const long Message = 2L * 1024 * 1024 * 1024;
    public const long Staged = Message + 500L * 1024 * 1024;
    public const string MessageLabel = "2GiB";
    public const string NameHeader = "X-Gyomutalk-File-Name";
}

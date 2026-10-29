using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace SchoolMessenger.AnnouncementServer;

public static class PortalSecurity
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string Id(HttpContext c) => c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    public static IResult Error(string message, int code = 400) => Results.Json(new { error = message }, statusCode: code);
    public static bool Password(string? value) => value?.Length is >= 12 and <= 128;
    public static bool Username(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9._-]{3,32}$");
    public static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 50 && !value.Any(char.IsControl);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static bool VerifiedTeacher(PortalStore store, Account teacher, bool schoolWide = false)
    {
        if (teacher is not { Active: true, Role: "teacher", InternalUserId: not null }) return false;
        return store.Query("SELECT CanBroadcast FROM TeacherProofs WHERE PersonId=$id AND Active=1 AND VerifiedAt>$cutoff",r=>r.GetBoolean(0),
            ("$id",teacher.Id),("$cutoff",Now-120_000)).Any(allowed=>!schoolWide || (allowed && teacher.CanBroadcast));
    }
    public static PasswordHasher<Account> Hasher(IServiceProvider services) => services.GetRequiredService<PasswordHasher<Account>>();
}

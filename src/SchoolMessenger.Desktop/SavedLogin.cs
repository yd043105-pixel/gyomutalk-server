using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolMessenger.Desktop;

public sealed class SavedLogin(string directory)
{
    public string FilePath => Path.Combine(directory, "login.dat");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YeoyangSchoolMessenger.Login.v1");
    public bool Exists => File.Exists(FilePath);
    public void Clear() => File.Delete(FilePath);
    public void Save(Api api)
    {
        var cookies = api.Cookies.GetCookies(api.Address).Cast<Cookie>().Where(c => c.Name.StartsWith("SchoolMessenger.Session", StringComparison.Ordinal) && c.Expires > DateTime.Now)
            .Select(c => new SavedCookie(c.Name, c.Value, c.Path, c.Expires.ToUniversalTime())).ToArray();
        if (cookies.Length == 0) throw new InvalidOperationException("자동 로그인 정보를 저장하지 못했습니다. 다시 로그인하세요.");
        var value = JsonSerializer.SerializeToUtf8Bytes(new SavedSession(api.Address.AbsoluteUri, api.Session!.User!.Username, cookies));
        var encrypted = ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser);
        var temporary = FilePath + ".tmp";
        File.WriteAllBytes(temporary, encrypted); File.Move(temporary, FilePath, true);
    }
    public bool Restore(Api api, string username)
    {
        if (!Exists) return false;
        var data = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        var saved = JsonSerializer.Deserialize<SavedSession>(data);
        if (saved is null || saved.Address != api.Address.AbsoluteUri || !saved.Username.Equals(username, StringComparison.OrdinalIgnoreCase) || saved.Cookies.Length == 0 || saved.Cookies.Any(c => c.Expires <= DateTime.UtcNow))
        { Clear(); return false; }
        foreach (var cookie in saved.Cookies) api.Cookies.Add(api.Address, new Cookie(cookie.Name, cookie.Value, cookie.Path) { HttpOnly = true, Secure = api.Address.Scheme == "https", Expires = cookie.Expires });
        return true;
    }
    sealed record SavedSession(string Address, string Username, SavedCookie[] Cookies);
    sealed record SavedCookie(string Name, string Value, string Path, DateTime Expires);
}

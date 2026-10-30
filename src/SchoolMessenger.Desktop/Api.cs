using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;

namespace SchoolMessenger.Desktop;

public sealed class Api : IDisposable
{
    public CookieContainer Cookies { get; } = new();
    public Uri Address { get; }
    public HttpClient Http { get; }
    public Session? Session { get; private set; }
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Api(string address)
    {
        if(!Uri.TryCreate(address.TrimEnd('/')+'/',UriKind.Absolute,out var uri)||uri.AbsolutePath!="/"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new InvalidOperationException("학교에서 안내한 서버 기본 주소를 입력하세요.");
        Address = uri;
        if (Address.Scheme != "https" && !(Address.Scheme == "http" && Address.IsLoopback))
            throw new InvalidOperationException("교내 서버 주소는 HTTPS여야 합니다. HTTP는 이 PC의 개발 서버에만 허용됩니다.");
        Http = new HttpClient(CertificatePins.Handler(Address, new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect=false })) { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(90) };
        Http.DefaultRequestHeaders.Add("X-YY-Product","windows");
        Http.DefaultRequestHeaders.Add("X-YY-Version",typeof(Api).Assembly.GetName().Version?.ToString(3)??"1.0.0");
    }
    public async Task RefreshSession(CancellationToken cancellationToken = default) => Session = await Get<Session>("api/session", cancellationToken);
    public async Task<T> Get<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync(path, cancellationToken);
        await Check(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
    public async Task<T> Send<T>(HttpMethod method, string path, object? value = null, HttpContent? content = null)
    {
        if (Session is null) await RefreshSession();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", Session!.CsrfToken);
        request.Content = content ?? JsonContent.Create(value ?? new { });
        using var response = await Http.SendAsync(request);
        await Check(response);
        if (response.Content.Headers.ContentLength == 0) return default!;
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default! : JsonSerializer.Deserialize<T>(text, Json)!;
    }
    public async Task Login(string username, string password, bool rememberLogin = false)
    {
        await Send<JsonElement>(HttpMethod.Post, "api/login", new { username, password, rememberLogin });
        await RefreshSession();
    }
    public async Task<Attachment> Upload(string path)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(File.OpenRead(path)), "file", Path.GetFileName(path));
        return await Send<Attachment>(HttpMethod.Post, "api/attachments", content: content);
    }
    public async Task Download(string id, string destination)
        => await DownloadPath($"api/attachments/{id}/download", destination);
    public async Task DownloadPath(string path, string destination)
    {
        using var response = await Http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
        await Check(response);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = File.Create(temporary)) await response.Content.CopyToAsync(output);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static async Task Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "로그인이 만료되었습니다. 다시 로그인하세요.",
            HttpStatusCode.Forbidden => "권한이 없습니다.",
            HttpStatusCode.TooManyRequests => "요청이 많습니다. 잠시 후 다시 시도하세요.",
            _ => "서버 요청 실패. 연결 상태를 확인하세요."
        };
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (error.TryGetProperty("error", out var property)) message = property.GetString() ?? message;
        }
        catch (JsonException) { }
        throw new ApiRequestException(response.StatusCode,message);
    }
    public void Dispose() => Http.Dispose();
}
internal sealed class ApiRequestException(HttpStatusCode status,string message):InvalidOperationException(message)
{public HttpStatusCode StatusCode{get;}=status;}
public record UserInfo(string Id, string Username, string Name, string Department, bool IsAdmin, bool CanBroadcast, bool Active, bool ActivationRequired=false, string? SeatIp=null,string Position="",bool SharedPassword=false,bool PasswordChangeRequired=false);
public record Session(string SchoolName, string CsrfToken, UserInfo? User);
public record Person(string Id, string Name, string Department, bool Online,string Position="",bool Away=false,bool Mobile=false)
{
    public string Label => $"{Name} · {Department}";
    public bool Selected { get; set; }
    public bool Favorite { get; set; }
    public bool Available=>Online?!Away:Mobile;
    public string Presence => Online ? (Away ? "자리비움" : "온라인") : Mobile ? "휴대폰 접속" : "오프라인";
}
public record MessageItem(string Id, string Title, string Preview, long CreatedAt, string SenderName,
    long? ReadAt, int RecipientCount, int ReadCount, int AttachmentCount, string SenderId = "")
{
    public string Date => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime().ToString("MM.dd HH:mm");
    public string Summary => $"{SenderName}   ·   {Date}";
    public bool IsSent { get; init; }
    public string ReadLabel => IsSent ? $"{ReadCount}/{RecipientCount}명 확인" : ReadAt is null ? "미확인" : "확인";
    public string DisplayTitle => SchoolMessenger.Shared.MessageText.Excerpt(string.IsNullOrWhiteSpace(Title) ? Preview : Title, 200);
    public string FilesLabel => AttachmentCount > 0 ? $"첨부 {AttachmentCount}" : "";
}
public record MessageContent(string Id, string SenderId, string SenderName, string Title, string Body, long CreatedAt, SchoolMessenger.Contracts.MessageFormat? Format = null, bool Purged = false);
public record Attachment(string Id, string Name, long Size, long ExpiresAt = 0, bool Expired = false)
{
    public bool Available => !Expired;
    public string Description => Expired ? $"{Name} · 보관 기간 만료" : $"{Name} · {Size / 1024d:N0}KB";
    public string Expiry => ExpiresAt > 0 ? $"{DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt).ToLocalTime():yyyy.MM.dd HH:mm}까지 다운로드" : "";
}
public record Receipt(string Id, string Name, string Department, long? ReadAt)
{
    public string Label => $"{Name} · {Department}   {(ReadAt is null ? "미확인" : DateTimeOffset.FromUnixTimeMilliseconds(ReadAt.Value).ToLocalTime().ToString("MM.dd HH:mm 확인"))}";
}
public record Detail(MessageContent Message, Attachment[] Attachments, Receipt[] Recipients, bool SubmissionRequest = false, string? SubmissionRequestId = null);

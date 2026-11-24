using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SchoolMessenger.Desktop;

// AI for a teacher's own writing. Each teacher connects their own account on their own PC: ChatGPT by signing in
// (their Plus or Pro plan, OpenAI's sign-in for open-source apps that run locally), OpenRouter or Google AI Studio by
// pasting a key. Everything is kept on this PC, encrypted for this Windows user; the school server never sees it.
public static class Ai
{
    public const string ChatGpt = "chatgpt", OpenRouter = "openrouter", Google = "google";
    public static readonly string[] Providers = [ChatGpt, OpenRouter, Google];
    public static string Name(string provider) => provider switch { ChatGpt => "ChatGPT", OpenRouter => "OpenRouter", _ => "Google AI Studio" };
    public const string ManageUsage = "https://chatgpt.com/settings/usage";
    public const string OpenRouterKeys = "https://openrouter.ai/keys", GoogleKeys = "https://aistudio.google.com/apikey";

    public sealed record Account(string? Key = null, string? Model = null, string? ClientId = null, string? Refresh = null, string? Access = null,
        long Expires = 0, string? IdToken = null, string? Email = null);
    public sealed record Settings(string? Use = null, string? HostId = null, Dictionary<string, Account>? Accounts = null, bool Welcomed = false)
    {
        public Account Of(string provider) => Accounts?.GetValueOrDefault(provider) ?? new();
        public bool Connected(string provider) => provider == ChatGpt ? Of(provider).Refresh is not null : Of(provider).Key is not null;
        public Settings With(string provider, Account? account)
        {
            var all = new Dictionary<string, Account>(Accounts ?? []);
            if (account is null) all.Remove(provider); else all[provider] = account;
            return this with { Accounts = all, Use = account is null && Use == provider ? Providers.FirstOrDefault(p => p != provider && all.ContainsKey(p)) : Use ?? (account is null ? null : provider) };
        }
    }

    // Test runs point these at a local stand-in; real runs use the companies' addresses.
    static string Base(string name, string real) => Environment.GetEnvironmentVariable("YY_AI_" + name) ?? real;
    static string AuthBase => Base("OPENAI_AUTH", "https://auth.openai.com");
    static string OpenAiBase => Base("OPENAI_API", "https://api.openai.com/v1");
    static string OpenRouterBase => Base("OPENROUTER", "https://openrouter.ai/api/v1");
    static string GoogleBase => Base("GOOGLE", "https://generativelanguage.googleapis.com/v1beta/openai");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static string? file;
    static readonly SemaphoreSlim Gate = new(1, 1);
    public static Settings Current { get; private set; } = new();

    public static void Load(string localDirectory)
    {
        file = Path.Combine(localDirectory, "ai.dpapi");
        try { Current = File.Exists(file) ? JsonSerializer.Deserialize<Settings>(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException or UnauthorizedAccessException) { Current = new(); }
    }
    public static void Save(Settings settings)
    {
        Current = settings;
        if (file is null) return;
        var temporary = file + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(settings, Json), null, DataProtectionScope.CurrentUser));
        File.Move(temporary, file, true);
    }

    public static string? InUse => Current.Use is { } use && Current.Connected(use) && Current.Of(use).Model is { Length: > 0 } ? use : null;
    public static bool Ready => InUse is not null;

    // ── Asking ──
    public static async Task<string> Ask(string instructions, string text, CancellationToken cancel)
    {
        var provider = InUse ?? throw new AiException("설정 → AI 연결에서 먼저 AI를 연결하고 모델을 고르세요.");
        var account = Current.Of(provider);
        try
        {
            return provider == ChatGpt ? await AskChatGpt(account, instructions, text, cancel) : await AskCompatible(provider, account, instructions, text, cancel);
        }
        catch (HttpRequestException) { throw new AiException(Name(provider) + "에 연결하지 못했습니다. 인터넷 연결이나 학교 방화벽을 확인하세요."); }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { throw new AiException(Name(provider) + "의 응답이 너무 늦습니다. 잠시 뒤 다시 하세요."); }
    }

    static async Task<string> AskCompatible(string provider, Account account, string instructions, string text, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, (provider == OpenRouter ? OpenRouterBase : GoogleBase) + "/chat/completions")
        {
            Content = JsonContent(new { model = account.Model, messages = new object[] { new { role = "system", content = instructions }, new { role = "user", content = text } } })
        };
        Authorize(request, provider, account.Key!);
        using var response = await Http.SendAsync(request, cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) throw Failure(provider, response.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        var answer = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(answer) ? throw new AiException(Name(provider) + "가 빈 답을 보냈습니다. 다른 모델을 골라 보세요.") : answer.Trim();
    }

    static void Authorize(HttpRequestMessage request, string provider, string key)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        // OpenRouter asks apps to name themselves; header values must be plain ASCII.
        if (provider == OpenRouter) { request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/yd043105-pixel/gyomutalk-server"); request.Headers.TryAddWithoutValidation("X-Title", "GyomuTalk"); }
    }

    static AiException Failure(string provider, HttpStatusCode status, string body)
    {
        var detail = "";
        try { using var json = JsonDocument.Parse(body); var root = json.RootElement; var error = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0].GetProperty("error") : root.GetProperty("error"); detail = error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""; }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        var what = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "열쇠가 맞지 않거나 만료되었습니다. 설정 → AI 연결에서 다시 연결하세요.",
            HttpStatusCode.PaymentRequired => "이 계정의 잔액·크레딧이 부족합니다. 무료 모델을 고르거나 충전하세요.",
            HttpStatusCode.TooManyRequests => "사용 한도에 도달했습니다. 잠시 뒤 다시 하거나 다른 모델을 고르세요.",
            HttpStatusCode.NotFound => "고른 모델을 찾지 못했습니다. 설정 → AI 연결에서 모델을 다시 고르세요.",
            _ => "요청을 처리하지 못했습니다."
        };
        return new AiException(Name(provider) + ": " + what + (detail.Length > 0 ? " (" + (detail.Length > 160 ? detail[..160] + "…" : detail) + ")" : ""));
    }

    // ── Models ──
    public static async Task<string[]> Models(string provider, CancellationToken cancel)
    {
        var account = Current.Of(provider);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, (provider switch { ChatGpt => OpenAiBase, OpenRouter => OpenRouterBase, _ => GoogleBase }) + "/models");
            if (provider == ChatGpt) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancel));
            else Authorize(request, provider, account.Key!);
            using var response = await Http.SendAsync(request, cancel);
            var body = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode) throw Failure(provider, response.StatusCode, body);
            using var json = JsonDocument.Parse(body);
            var ids = json.RootElement.GetProperty("data").EnumerateArray()
                .Where(m => provider != ChatGpt || !m.TryGetProperty("visibility", out var v) || v.GetString() == "list")
                .Select(m => m.GetProperty("id").GetString() ?? "").Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id)
                // Google lists embedding, image and speech models too; only text models can rewrite a message.
                .Where(id => id.Length > 0 && (provider != Google || id.Contains("gemini", StringComparison.OrdinalIgnoreCase) && !id.Contains("embedding", StringComparison.OrdinalIgnoreCase) && !id.Contains("tts", StringComparison.OrdinalIgnoreCase) && !id.Contains("image", StringComparison.OrdinalIgnoreCase)))
                .Distinct().ToArray();
            // Free OpenRouter models first, so a school can start without paying.
            return provider == OpenRouter ? ids.OrderBy(id => id.EndsWith(":free", StringComparison.Ordinal) ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal).ToArray() : ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }
        catch (HttpRequestException) { throw new AiException(Name(provider) + "에 연결하지 못했습니다. 인터넷 연결이나 학교 방화벽을 확인하세요."); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException and not AiException) { throw new AiException(Name(provider) + "의 모델 목록을 읽지 못했습니다."); }
    }

    // A pasted key is checked by listing the models with it before it is kept.
    public static async Task<string[]> Connect(string provider, string key, CancellationToken cancel)
    {
        key = key.Trim();
        if (key.Length < 10 || key.Any(char.IsWhiteSpace)) throw new AiException("열쇠를 끝까지 정확히 붙여넣으세요.");
        var before = Current;
        Current = Current.With(provider, Current.Of(provider) with { Key = key });
        try { var models = await Models(provider, cancel); Save(Current); return models; }
        catch { Current = before; throw; }
    }

    // ── ChatGPT sign-in (OAuth with PKCE on a 127.0.0.1 loopback) ──
    const string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    static string Url64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Random64() => Url64(RandomNumberGenerator.GetBytes(32));

    public static async Task SignIn(Action<string> openBrowser, CancellationToken cancel)
    {
        var settings = Current.HostId is null ? Current with { HostId = "urn:uuid:" + Guid.NewGuid() } : Current;
        var saved = settings.Of(ChatGpt);
        var listener = Listen();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback";
            string verifier = Random64(), state = Random64(), nonce = Random64();
            var challenge = Url64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var query = new List<(string, string)>
            {
                ("client_id", saved.ClientId ?? "dynamic_agent_client"), ("response_type", "code"), ("redirect_uri", redirect), ("scope", Scopes),
                ("resource", "https://api.openai.com/v1"), ("state", state), ("nonce", nonce), ("code_challenge", challenge),
                ("code_challenge_method", "S256"), ("ext_agent_host_id", settings.HostId!)
            };
            if (saved.ClientId is null) query.Add(("agent_name_hint", "교무톡"));
            else if (saved.IdToken is not null) query.Add(("id_token_hint", saved.IdToken));
            openBrowser(AuthBase + "/api/accounts/authorize?" + string.Join("&", query.Select(q => q.Item1 + "=" + Uri.EscapeDataString(q.Item2))));
            var callback = await Callback(listener, cancel);
            if (callback.GetValueOrDefault("state") != state) throw new AiException("로그인 응답이 이 요청과 맞지 않습니다. 다시 로그인하세요.");
            if (callback.ContainsKey("error")) throw new AiException(callback["error"] == "access_denied" ? "ChatGPT 로그인을 취소했거나 플랜 사용을 허용하지 않았습니다." : "ChatGPT 로그인에 실패했습니다: " + callback["error"]);
            var clientId = callback.GetValueOrDefault("client_id");
            if (saved.ClientId is not null && clientId is not null && clientId != saved.ClientId) throw new AiException("로그인 응답의 앱 정보가 맞지 않습니다. 다시 로그인하세요.");
            clientId ??= saved.ClientId ?? throw new AiException("ChatGPT가 이 프로그램을 등록하지 않았습니다. 다시 로그인하세요.");
            var code = callback.GetValueOrDefault("code") ?? throw new AiException("ChatGPT 로그인 응답에 승인 코드가 없습니다.");
            var tokens = await Token(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["resource"] = "https://api.openai.com/v1" }, cancel);
            if (tokens.Scope is { } scope && !scope.Split(' ').Contains("chatgpt.tokens.use.direct")) throw new AiException("ChatGPT 플랜 사용을 허용하지 않았습니다. 다시 로그인해 ‘플랜 사용’을 허용하세요.");
            Save(settings.With(ChatGpt, saved with { ClientId = clientId, Refresh = tokens.Refresh, Access = tokens.Access, Expires = tokens.Expires, IdToken = tokens.IdToken ?? saved.IdToken, Email = Email(tokens.IdToken) ?? saved.Email }));
        }
        finally { listener.Stop(); }
    }

    // The documented callback is http://127.0.0.1:1455/auth/callback; only the port may change when 1455 is taken.
    static TcpListener Listen()
    {
        foreach (var port in new[] { 1455, 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return listener; } catch (SocketException) when (port != 0) { }
        }
        throw new AiException("로그인을 받을 자리를 열지 못했습니다.");
    }

    static async Task<Dictionary<string, string>> Callback(TcpListener listener, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var line = await reader.ReadLineAsync(timeout.Token) ?? "";
            var parts = line.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "";
            var found = target.StartsWith("/auth/callback?", StringComparison.Ordinal);
            var page = found ? "<h2>교무톡 로그인이 끝났습니다.</h2><p>이 창을 닫고 교무톡으로 돌아가세요.</p>" : "<p>교무톡</p>";
            var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>교무톡</title><body style=\"font-family:'Malgun Gothic',sans-serif;padding:40px\">" + page);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n").Concat(html).ToArray(), timeout.Token);
            if (found) return target[(target.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
        }
    }

    sealed record Tokens(string Access, string? Refresh, long Expires, string? IdToken, string? Scope);
    static async Task<Tokens> Token(Dictionary<string, string> form, CancellationToken cancel)
    {
        using var response = await Http.PostAsync(AuthBase + "/api/accounts/oauth/token", new FormUrlEncodedContent(form), cancel);
        var body = await response.Content.ReadAsStringAsync(cancel);
        using var json = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            throw new AiException(form["grant_type"] == "refresh_token" ? "ChatGPT 로그인이 만료되었습니다. 설정 → AI 연결에서 다시 로그인하세요." : "ChatGPT 로그인을 마치지 못했습니다" + (error is null ? "." : $" ({error})."), error);
        }
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var seconds = root.TryGetProperty("expires_in", out var e2) && e2.TryGetInt64(out var s) ? s : 3600;
        return new(access.GetString()!, Text("refresh_token"), DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds(), Text("id_token"), Text("scope"));
    }

    // The e-mail is only shown next to "연결됨"; nothing is decided from the ID token, so its signature is not checked here.
    static string? Email(string? idToken)
    {
        try
        {
            if (idToken?.Split('.') is not [_, var payload, ..]) return null;
            var padded = payload.Replace('-', '+').Replace('_', '/'); padded += new string('=', (4 - padded.Length % 4) % 4);
            using var json = JsonDocument.Parse(Convert.FromBase64String(padded));
            return json.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    static async Task<string> AccessToken(CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel);
        try
        {
            var account = Current.Of(ChatGpt);
            if (account.Access is { } access && account.Expires > DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()) return access;
            if (account.Refresh is null || account.ClientId is null) throw new AiException("설정 → AI 연결에서 ChatGPT로 다시 로그인하세요.");
            try
            {
                var tokens = await Token(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = account.Refresh, ["client_id"] = account.ClientId }, cancel);
                Save(Current.With(ChatGpt, account with { Access = tokens.Access, Refresh = tokens.Refresh ?? account.Refresh, Expires = tokens.Expires, IdToken = tokens.IdToken ?? account.IdToken }));
                return tokens.Access;
            }
            catch (AiException e) when (e.Code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
            {
                // The sign-in is gone; keep the issued app registration so signing in again skips registering.
                Save(Current.With(ChatGpt, account with { Refresh = null, Access = null, Expires = 0 }));
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    static async Task<string> AskChatGpt(Account account, string instructions, string text, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiBase + "/responses")
        {
            Content = JsonContent(new { model = account.Model, instructions, input = text, store = false, stream = true })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(cancel));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (!response.IsSuccessStatusCode) throw ChatGptFailure(await response.Content.ReadAsStringAsync(cancel), response.StatusCode);
        var answer = new StringBuilder();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel));
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim(); if (data.Length == 0 || data == "[DONE]") continue;
            using var json = JsonDocument.Parse(data);
            var type = json.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "response.output_text.delta") answer.Append(json.RootElement.GetProperty("delta").GetString());
            else if (type == "response.completed") return answer.Length > 0 ? answer.ToString().Trim() : throw new AiException("ChatGPT가 빈 답을 보냈습니다.");
            else if (type is "response.failed" or "response.incomplete" or "error") throw ChatGptFailure(data, null);
        }
        throw new AiException("ChatGPT 응답이 중간에 끊겼습니다. 다시 하세요.");
    }

    static AiException ChatGptFailure(string body, HttpStatusCode? status)
    {
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var error = root.TryGetProperty("response", out var r) && r.TryGetProperty("error", out var e1) ? e1 : root.TryGetProperty("error", out var e2) ? e2 : default;
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c)) code = c.GetString();
        }
        catch (JsonException) { }
        return new AiException(code switch
        {
            "subscription_sharing_usage_limit_exceeded" => "ChatGPT 플랜의 사용 한도에 도달했습니다. ChatGPT 설정 → 사용량에서 확인하세요.",
            "subscription_sharing_usage_unavailable" => "ChatGPT 플랜을 지금 쓸 수 없습니다. 잠시 뒤 다시 하세요.",
            "subscription_sharing_user_not_eligible" => "이 ChatGPT 계정은 다른 앱에서 플랜을 쓸 수 없습니다(Plus·Pro만 됩니다). OpenRouter나 Google AI Studio를 연결하세요.",
            "subscription_sharing_invalid_user" => "ChatGPT 로그인이 해제되었습니다. 설정 → AI 연결에서 다시 로그인하세요.",
            _ when status is HttpStatusCode.Unauthorized => "ChatGPT 로그인이 해제되었습니다. 설정 → AI 연결에서 다시 로그인하세요.",
            _ => "ChatGPT가 요청을 처리하지 못했습니다" + (code is null ? "." : $" ({code}).")
        }, code);
    }

    public static void SignOut(string provider) => Save(provider == ChatGpt ? Current.With(ChatGpt, Current.Of(ChatGpt) is { ClientId: { } id } ? new Account(ClientId: id) : null) : Current.With(provider, null));

    static StringContent JsonContent(object value) => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    // ── What the teacher asks for ──
    public sealed record Rewrite(string Label, string Instructions);
    const string Keep = " 사실·날짜·시간·장소·숫자·사람 이름·요청 사항은 바꾸지 말고, 없는 내용을 지어내지 마세요. 설명이나 인사말 없이 고친 글만 출력하세요." + StyleCheck.Guide;
    public static readonly Rewrite[] Rewrites =
    [
        new("맞춤법·문장 다듬기", "다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 맞춤법과 띄어쓰기를 고치고 어색한 문장을 자연스럽게 다듬으세요." + Keep),
        new("더 짧게", "다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 핵심만 남겨 더 짧고 읽기 쉽게 줄이세요." + Keep),
        new("정중한 안내 말투로", "다음은 한국 고등학교 교사가 동료 교직원에게 보내는 업무 메시지 초안입니다. 교직원 전체에게 보내는 정중한 업무 안내 말투(‘~하시기 바랍니다’)로 바꾸세요." + Keep),
    ];
}

public sealed class AiException(string message, string? code = null) : InvalidOperationException(message)
{
    public string? Code { get; } = code;
}

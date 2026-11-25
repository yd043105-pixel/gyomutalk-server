// AI connection against a local stand-in for OpenAI, OpenRouter and Google AI Studio: no real account or key is used.
// Covers ChatGPT sign-in (PKCE, issued client ID, second sign-in, refresh, plan usage), pasted keys, model lists and rewriting.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchoolMessenger.Desktop;

var failed = false;
void Check(bool ok, string text) { Console.WriteLine((ok ? "PASS " : "FAIL ") + text); failed |= !ok; }

var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); port.Start(); var free = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
var root = $"http://localhost:{free}";
Environment.SetEnvironmentVariable("YY_AI_OPENAI_AUTH", root + "/auth");
Environment.SetEnvironmentVariable("YY_AI_OPENAI_API", root + "/openai");
Environment.SetEnvironmentVariable("YY_AI_OPENROUTER", root + "/openrouter");
Environment.SetEnvironmentVariable("YY_AI_GOOGLE", root + "/google");

var challenges = new Dictionary<string, string>(); var refreshed = 0; var lastResponses = "";
static string Url64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
string IdToken(string email) => "x." + Url64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { email }))) + ".x";
var server = new HttpListener(); server.Prefixes.Add(root + "/"); server.Start();
_ = Task.Run(async () =>
{
    while (server.IsListening)
    {
        HttpListenerContext c; try { c = await server.GetContextAsync(); } catch { return; }
        var path = c.Request.Url!.AbsolutePath; var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
        var auth = c.Request.Headers["Authorization"] ?? "";
        async Task Send(int status, string text, string type = "application/json") { c.Response.StatusCode = status; c.Response.ContentType = type; var bytes = Encoding.UTF8.GetBytes(text); await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close(); }
        string Reply(string text) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "다듬음: " + text } } } });
        string UserText() { using var j = JsonDocument.Parse(body); return j.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!; }
        if (path == "/auth/api/accounts/oauth/token")
        {
            var form = body.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            if (form["grant_type"] == "authorization_code")
            {
                var ok = challenges.TryGetValue(form["code"], out var challenge) && challenge == Url64(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))) && form["client_id"] == "oaiapp_test" && form["resource"] == "https://api.openai.com/v1";
                await Send(ok ? 200 : 400, ok ? JsonSerializer.Serialize(new { access_token = "access-1", refresh_token = "refresh-1", id_token = IdToken("teacher@example.com"), expires_in = 3600, scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct" }) : "{\"error\":\"invalid_grant\"}");
            }
            else { refreshed++; var ok = form["refresh_token"].StartsWith("refresh-") && form["client_id"] == "oaiapp_test"; await Send(ok ? 200 : 400, ok ? JsonSerializer.Serialize(new { access_token = "access-2", refresh_token = "refresh-2", expires_in = 3600 }) : "{\"error\":\"invalid_grant\"}"); }
        }
        else if (path == "/openai/models") await Send(auth.StartsWith("Bearer access-") ? 200 : 401, "{\"data\":[{\"id\":\"gpt-test-mini\",\"visibility\":\"list\"},{\"id\":\"gpt-hidden\",\"visibility\":\"hidden\"}]}");
        else if (path == "/openai/responses")
        {
            lastResponses = body; using var j = JsonDocument.Parse(body);
            if (j.RootElement.GetProperty("model").GetString() == "limit") { await Send(200, "data: {\"type\":\"response.failed\",\"response\":{\"error\":{\"code\":\"subscription_sharing_usage_limit_exceeded\"}}}\n\n", "text/event-stream"); continue; }
            var input = j.RootElement.GetProperty("input").GetString()!;
            await Send(200, $"data: {{\"type\":\"response.output_text.delta\",\"delta\":\"다듬음: \"}}\n\ndata: {JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = input })}\n\ndata: {{\"type\":\"response.completed\"}}\n\n", "text/event-stream");
        }
        else if (path.StartsWith("/openrouter/") || path.StartsWith("/google/"))
        {
            var good = path.StartsWith("/openrouter/") ? "Bearer sk-or-good-key-123" : "Bearer AIza-good-key-123";
            if (auth != good) { await Send(401, "{\"error\":{\"message\":\"Invalid API key\"}}"); continue; }
            if (path.EndsWith("/models")) await Send(200, path.StartsWith("/openrouter/") ? "{\"data\":[{\"id\":\"openai/gpt-paid\"},{\"id\":\"meta/llama:free\"},{\"id\":\"anthropic/claude-test\"}]}" : "{\"data\":[{\"id\":\"models/gemini-2.5-flash\"},{\"id\":\"models/text-embedding-004\"},{\"id\":\"models/gemini-2.5-flash-image\"},{\"id\":\"models/gemini-2.5-pro\"}]}");
            else await Send(200, Reply(UserText()));
        }
        else await Send(404, "{}");
    }
});

// 보내기 전 확인 on this PC: flags what it should, stays quiet on ordinary teacher writing.
var flagged = StyleCheck.Check("학부형 상담 자료를 익일까지 취합하여 송부 바랍니다 — 결론적으로 2026-11-20 회의에서 정합니다.");
Check(flagged.Select(f => f.Found).ToHashSet().SetEquals(["학부형", "익일", "취합", "송부", "—", "결론적으로", "2026-11-20"]) && flagged.First(f => f.Found == "2026-11-20").Instead == "2026. 11. 20.(금)",
    "보내기 전 확인 finds discriminatory and old administrative words, AI phrasing and 2026-11-20, and offers 2026. 11. 20.(금)");
Check(StyleCheck.Check("내일 오후 2시에 3층 회의실에서 학부모 상담 일정을 정합니다. 지난 회의 내용을 상기시켜 드립니다. 자료는 11월 20일까지 모아 주세요.").Length == 0,
    "ordinary writing, ‘상기시켜’ and ‘11월 20일’ are not flagged");
Check(Ai.Rewrites.All(r => r.Instructions.Contains("줄표") && r.Instructions.Contains("2026. 11. 20.(금)")), "every AI rewrite carries the same advice, so the AI does not bring in what the check flags");

// 교체 도우미: teachers go to the AI as stand-ins, and its order can only rearrange the checked candidates.
var stand = new SwapAdvice.Names("가상교사");
Check(stand.Of("가상교사") == "요청한 선생님" && stand.Of("가상담임") == "교사A" && stand.Of("둘째담임") == "교사B" && stand.Of("가상담임") == "교사A",
    "the asking teacher and every other teacher get the same stand-in name each time");
var swapPrompt = SwapAdvice.Prompt("오후 수업과는 바꾸지 말아 주세요", "수 1교시", [(true, [$"수 1-1 1교시 국어({stand.Of("가상교사")}) ↔ 2교시 수학({stand.Of("가상담임")})"]), (false, ["목 1-1 5교시 영어(교사B)"])]);
Check(!swapPrompt.Contains("가상담임") && swapPrompt.Contains("1) [같은 날]") && swapPrompt.Contains("2) [다른 날 포함]") && swapPrompt.Contains("오후 수업과는"),
    "the request sent to the AI carries the wish and numbered candidates, not teachers' names");
var (swapOrder, swapReason) = SwapAdvice.Read("2, 1\n오후를 피하라는 요청에 맞춰 2번을 먼저 두었습니다.", 3);
Check(swapOrder.SequenceEqual([1, 0, 2]) && swapReason.StartsWith("오후를"), "the AI's numbers set the order, a candidate it skips stays at the end, and its reason is kept");
Check(SwapAdvice.Read("3,3,9,1", 3).Order.SequenceEqual([2, 0, 1]) && SwapAdvice.Read("잘 모르겠습니다.", 2) is { Order: [0, 1], Reason: "잘 모르겠습니다." },
    "repeated or unknown numbers are ignored, and an answer without numbers keeps the program's order");

var folder = Path.Combine(Path.GetTempPath(), "ai-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
try
{
    Ai.Load(folder);
    Check(!Ai.Ready && Ai.Current.Accounts is null, "nothing is connected at first, so ‘AI로 다듬기’ asks to connect");
    try { await Ai.Ask("x", "y", CancellationToken.None); Check(false, "asking without a connection is refused"); } catch (AiException e) { Check(e.Message.Contains("AI 연결"), "asking without a connection is refused with where to connect"); }

    // ChatGPT: the first sign-in registers the app; the browser is played by this test.
    string? opened = null;
    async Task Approve(string url, bool giveClient)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        opened = url; challenges["code-" + challenges.Count] = query["code_challenge"];
        using var http = new HttpClient();
        await http.GetStringAsync(query["redirect_uri"] + $"?code=code-{challenges.Count - 1}&state={Uri.EscapeDataString(query["state"])}" + (giveClient ? "&client_id=oaiapp_test" : ""));
    }
    await Ai.SignIn(url => _ = Approve(url, true), CancellationToken.None);
    var first = new Uri(opened!).Query;
    Check(first.Contains("client_id=dynamic_agent_client") && first.Contains("agent_name_hint=" + Uri.EscapeDataString("교무톡")) && first.Contains("code_challenge_method=S256") && first.Contains(Uri.EscapeDataString("http://127.0.0.1:")) && first.Contains(Uri.EscapeDataString("/auth/callback")) && first.Contains("chatgpt.tokens.use.direct") && first.Contains("ext_agent_host_id=urn%3Auuid%3A"),
        "ChatGPT sign-in registers the app with PKCE, a 127.0.0.1 callback, plan-usage scope and a per-PC host ID");
    Check(Ai.Current.Connected(Ai.ChatGpt) && Ai.Current.Of(Ai.ChatGpt).ClientId == "oaiapp_test" && Ai.Current.Of(Ai.ChatGpt).Email == "teacher@example.com" && Ai.Current.Use == Ai.ChatGpt,
        "the issued client ID, tokens and account e-mail are kept, and ChatGPT becomes the AI in use");
    var models = await Ai.Models(Ai.ChatGpt, CancellationToken.None);
    Check(models.SequenceEqual(["gpt-test-mini"]), "only models the account may use are listed");
    Ai.Save(Ai.Current.With(Ai.ChatGpt, Ai.Current.Of(Ai.ChatGpt) with { Model = "gpt-test-mini" }));
    var answer = await Ai.Ask(Ai.Rewrites[0].Instructions, "회의는 3시 입니다", CancellationToken.None);
    using (var sent = JsonDocument.Parse(lastResponses))
        Check(answer == "다듬음: 회의는 3시 입니다" && !sent.RootElement.GetProperty("store").GetBoolean() && sent.RootElement.GetProperty("stream").GetBoolean() && sent.RootElement.GetProperty("instructions").GetString()!.Contains("바꾸지 말고"),
            "a rewrite goes to the Responses API with store off and streaming on, and the streamed text comes back whole");
    Ai.Save(Ai.Current.With(Ai.ChatGpt, Ai.Current.Of(Ai.ChatGpt) with { Expires = 0 }));
    await Ai.Ask("x", "또", CancellationToken.None);
    Check(refreshed == 1 && Ai.Current.Of(Ai.ChatGpt).Access == "access-2" && Ai.Current.Of(Ai.ChatGpt).Refresh == "refresh-2", "an expired access token is renewed with the refresh token and the new tokens are kept");
    Ai.Save(Ai.Current.With(Ai.ChatGpt, Ai.Current.Of(Ai.ChatGpt) with { Model = "limit" }));
    try { await Ai.Ask("x", "y", CancellationToken.None); Check(false, "usage limit"); } catch (AiException e) { Check(e.Message.Contains("사용 한도"), "reaching the ChatGPT plan's limit says so instead of failing silently"); }
    Ai.Save(Ai.Current.With(Ai.ChatGpt, Ai.Current.Of(Ai.ChatGpt) with { Model = "gpt-test-mini" }));
    Ai.SignOut(Ai.ChatGpt);
    Check(!Ai.Current.Connected(Ai.ChatGpt) && Ai.Current.Of(Ai.ChatGpt).ClientId == "oaiapp_test", "signing out forgets the tokens but keeps the app registration");
    await Ai.SignIn(url => _ = Approve(url, false), CancellationToken.None);
    var second = new Uri(opened!).Query;
    Check(second.Contains("client_id=oaiapp_test") && !second.Contains("agent_name_hint") && !second.Contains("id_token_hint") && Ai.Current.Connected(Ai.ChatGpt), "signing in again uses the issued client ID and shows the account choice after a sign-out; a callback without client_id is accepted");

    // Keys: a wrong key is refused and not kept; a right one is kept and its models listed.
    try { await Ai.Connect(Ai.OpenRouter, "sk-or-wrong-key-999", CancellationToken.None); Check(false, "wrong key"); }
    catch (AiException e) { Check(e.Message.Contains("열쇠") && !Ai.Current.Connected(Ai.OpenRouter), "a wrong OpenRouter key is refused with a reason and not kept"); }
    var routes = await Ai.Connect(Ai.OpenRouter, " sk-or-good-key-123 ", CancellationToken.None);
    Check(routes[0] == "meta/llama:free" && Ai.Current.Of(Ai.OpenRouter).Key == "sk-or-good-key-123", "an OpenRouter key is checked by listing models, free models first");
    var gemini = await Ai.Connect(Ai.Google, "AIza-good-key-123", CancellationToken.None);
    Check(gemini.SequenceEqual(["gemini-2.5-flash", "gemini-2.5-pro"]), "Google AI Studio lists only text Gemini models, without the models/ prefix");
    Ai.Save(Ai.Current.With(Ai.Google, Ai.Current.Of(Ai.Google) with { Model = "gemini-2.5-flash" }) with { Use = Ai.Google });
    Check(await Ai.Ask("x", "안녕하세요", CancellationToken.None) == "다듬음: 안녕하세요", "the AI in use can be switched to Google AI Studio and answers through its OpenAI-style endpoint");

    var saved = File.ReadAllBytes(Path.Combine(folder, "ai.dpapi"));
    Check(!Encoding.UTF8.GetString(saved).Contains("good-key") && !Encoding.UTF8.GetString(saved).Contains("refresh-"), "keys and tokens are stored encrypted for this Windows user, never as plain text");
    Ai.Load(folder);
    Check(Ai.Current.Use == Ai.Google && Ai.Current.Connected(Ai.OpenRouter) && Ai.Current.Connected(Ai.ChatGpt), "the connections come back after the program restarts");
}
catch (Exception e) { Check(false, e.ToString()); }
finally { server.Stop(); try { Directory.Delete(folder, true); } catch (IOException) { } }
return failed ? 1 : 0;

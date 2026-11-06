using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

public record AttachmentProgress(long Received, long Total, double BytesPerSecond);

public static class AttachmentDownload
{
    public static async Task Save(HttpClient client, string route, string account, string destination,
        Func<HttpResponseMessage, Task> check, Action<AttachmentProgress>? progress, CancellationToken cancellation)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{client.BaseAddress}\n{account}\n{route}\n{Path.GetFullPath(destination)}")));
        var partial = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".gyomutalk-{key[..32]}.part");
        var validator = partial + ".etag";
        var resumable = false;
        var ownsPartial = false;
        try
        {
            await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete, 128 * 1024, true))
            {
                ownsPartial = true;
                var metadata=new FileInfo(validator);
                var previous = metadata.Exists && metadata.Length==66 ? await File.ReadAllTextAsync(validator, cancellation) : "";
                var offset = IsHash(previous) ? output.Length : 0;
                resumable = offset > 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, route);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                    request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(previous));
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                var tag = response.Headers.ETag is { IsWeak:false } entityTag ? entityTag.Tag : "";
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                {
                    if (tag != previous || response.Content.Headers.ContentRange?.Length != offset)
                    {
                        resumable = false;
                        throw new IOException("이어받기 위치가 맞지 않습니다. 다시 다운로드하세요.");
                    }
                }
                else
                {
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                        resumable = false;
                    await check(response);
                    var range = response.Content.Headers.ContentRange;
                    var continued = response.StatusCode == HttpStatusCode.PartialContent;
                    if (continued && (offset == 0 || tag != previous || range?.From != offset || range.Length is not long length || range.To != length - 1))
                        throw new IOException("서버에서 받은 이어받기 범위가 올바르지 않습니다.");
                    if (!continued) { output.SetLength(0); offset = 0; }
                    output.Position = offset;
                    resumable = IsHash(tag);
                    if (resumable) await File.WriteAllTextAsync(validator, tag, cancellation);
                    else File.Delete(validator);
                    var total = continued ? range!.Length!.Value : response.Content.Headers.ContentLength ?? 0;
                    if(total>AttachmentLimits.Maximum){resumable=false;throw new IOException("파일당 10GiB까지 받을 수 있습니다.");}
                    var clock = Stopwatch.StartNew();
                    long received = offset, last = -200;
                    progress?.Invoke(new(received, total, 0));
                    await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                    var buffer = new byte[128 * 1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        received += count;
                        if (received>AttachmentLimits.Maximum||total > 0 && received > total) { resumable = false; throw new IOException("받은 파일이 예상 크기를 초과했습니다."); }
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                        if (progress is not null && clock.ElapsedMilliseconds - last >= 200)
                        {
                            last = clock.ElapsedMilliseconds;
                            progress(new(received, total, (received - offset) / clock.Elapsed.TotalSeconds));
                        }
                    }
                    if (total > 0 && received != total) throw new IOException("파일 전송이 중단됐습니다. 다시 다운로드하면 이어받을 수 있습니다.");
                    progress?.Invoke(new(received, total, (received - offset) / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
                }
                if (IsHash(tag))
                {
                    output.Position = 0;
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(output, cancellation)).Equals(tag[1..^1], StringComparison.OrdinalIgnoreCase))
                    { resumable = false; throw new IOException("파일 무결성 검사에 실패했습니다. 다시 다운로드하세요."); }
                }
                await output.FlushAsync(cancellation);
                File.Move(partial, destination, true);
                File.Delete(validator);
            }
        }
        catch
        {
            if (ownsPartial && !resumable) { File.Delete(partial); File.Delete(validator); }
            throw;
        }
    }

    static bool IsHash(string value) => value.Length == 66 && value[0] == '"' && value[^1] == '"' &&
        value.AsSpan(1, 64).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}

using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SchoolMessenger.Shared;

public sealed class FilePolicyException(string message, int status = 400) : Exception(message)
{ public int Status { get; } = status; }

public static class FilePolicy
{
    public const long MaxFile = 104_857_600, MaxTotal = 209_715_200, MaxStaged = 524_288_000;
    public const long MaxNoticeImage = 200_000;
    public static string NoticeImageName(string raw, long size)
    {
        if (size is <= 0 or > MaxNoticeImage) throw new FilePolicyException("학교 공지 이미지는 한 장당 200KB(0.2MB)까지입니다.", 413);
        var name = Name(raw);
        if (Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new FilePolicyException("학교 공지는 JPG·PNG 이미지로 올려주세요.");
        return name;
    }
    public static string NoticeImageType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => throw new FilePolicyException("학교 공지는 JPG·PNG 이미지로 올려주세요.") };
    public static void NoticeImage(string path, string name)
    {
        NoticeImageName(name, new FileInfo(path).Length);
        if (!Signature(path, Path.GetExtension(name).ToLowerInvariant())) throw new FilePolicyException("이미지 내용과 확장자가 일치하지 않습니다.");
        var data = File.ReadAllBytes(path); int width = 0, height = 0;
        if (NoticeImageType(name) == "image/png")
        {
            if (data.Length >= 45 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8,4)) == 13 && data.AsSpan(12,4).SequenceEqual("IHDR"u8) && data.AsSpan(data.Length-12,8).SequenceEqual(new byte[]{0,0,0,0,73,69,78,68}))
            { width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16,4)); height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20,4)); }
        }
        else if (data.Length >= 4 && data[^2] == 255 && data[^1] == 217)
        {
            for (var offset = 2; offset + 4 <= data.Length;)
            {
                if (data[offset++] != 255) break;
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset + 3 > data.Length) break;
                var marker = data[offset++]; if (marker is 218 or 217) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset,2));
                if (length < 2 || offset + length > data.Length) break;
                if (marker is 192 or 193 or 194 && length >= 8)
                { height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset+3,2)); width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset+5,2)); break; }
                offset += length;
            }
        }
        // Limit decoded memory even when a highly compressed upload is smaller than 500 KB.
        if (width <= 0 || height <= 0 || width > 6000 || height > 6000 || (long)width * height > 16_000_000)
            throw new FilePolicyException("이미지 구조 또는 해상도를 확인하세요. 최대 6000px, 총 1600만 화소입니다.");
    }
    public static string Name(string raw)
    {
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl) ||
            !new[] { ".hwp", ".hwpx", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".png", ".jpg", ".jpeg", ".zip" }.Contains(Path.GetExtension(name).ToLowerInvariant()))
            throw new FilePolicyException("지원하지 않는 파일 형식입니다.");
        return name;
    }
    public static bool Signature(string path, string extension)
    {
        Span<byte> bytes = stackalloc byte[8]; using var input = File.OpenRead(path);
        var length = input.Read(bytes); bytes = bytes[..length];
        if (extension is ".docx" or ".xlsx" or ".pptx" or ".hwpx" or ".zip") return length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 });
        if (extension is ".doc" or ".xls" or ".ppt" or ".hwp") return length == 8 && bytes.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 });
        if (extension == ".pdf") return length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8);
        if (extension == ".png") return length == 8 && bytes.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (extension is ".jpg" or ".jpeg") return length >= 3 && bytes[..3].SequenceEqual(new byte[] { 255, 216, 255 });
        return extension is ".txt" or ".csv";
    }
    enum ScanOutcome { Clean, Refused, Unavailable }
    // MpCmdRun says this when it cannot scan at all, typically because Defender is switched off beside another antivirus.
    // Anything else that is not a clean result is refused, so an answer we cannot read never counts as safe.
    static async Task<ScanOutcome> RunScanner(string path, string? scannerPath, CancellationToken cancellation)
    {
        var scanner = scannerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(scanner)) return ScanOutcome.Unavailable;
        var info = new ProcessStartInfo(scanner) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" }) info.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var bytes=new FileInfo(path).Length;
        timeout.CancelAfter(TimeSpan.FromMinutes(bytes<=MaxFile?1:Math.Clamp(Math.Ceiling(bytes/(1024d*1024*1024))*2,5,20)));
        using var scan = Process.Start(info) ?? throw new FilePolicyException("파일 검사기를 실행하지 못했습니다.", 503);
        try
        {
            var output = scan.StandardOutput.ReadToEndAsync(timeout.Token); var errors = scan.StandardError.ReadToEndAsync(timeout.Token);
            await scan.WaitForExitAsync(timeout.Token);
            if (scan.ExitCode == 0) return File.Exists(path) ? ScanOutcome.Clean : ScanOutcome.Refused;
            return File.Exists(path) && (await output + await errors).Contains("CmdTool: Failed with hr", StringComparison.Ordinal) ? ScanOutcome.Unavailable : ScanOutcome.Refused;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { if (!scan.HasExited) scan.Kill(true); throw new FilePolicyException("파일 검사가 오래 걸려 중단했습니다. 다시 시도하세요.", 503); }
        catch { if (!scan.HasExited) scan.Kill(true); throw; }
    }
    public static async Task Scan(string path, string name, bool development, string? scannerPath, CancellationToken cancellation)
    {
        if (!Signature(path, Path.GetExtension(name).ToLowerInvariant())) throw new FilePolicyException("파일 내용과 확장자가 일치하지 않습니다.");
        if (development) return;
        var outcome = await RunScanner(path, scannerPath, cancellation);
        if (outcome == ScanOutcome.Unavailable)
            // Reading the saved file gives a real-time antivirus its chance to block or remove it; this does not prove it looked.
            try { await using var input = File.OpenRead(path); await input.CopyToAsync(Stream.Null, cancellation); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { outcome = ScanOutcome.Refused; }
        if (outcome == ScanOutcome.Refused) throw new FilePolicyException("파일 검사에서 문제가 발견되어 올릴 수 없습니다.", 422);
    }
    // For the administrator's checklist: whether uploads on this PC really go through a scanner.
    public static async Task<bool> ScannerWorks(string? scannerPath = null)
    {
        var probe = Path.Combine(Path.GetTempPath(), "gyomutalk-scan-" + Guid.NewGuid().ToString("N") + ".txt");
        try { await File.WriteAllTextAsync(probe, "scanner check"); return await RunScanner(probe, scannerPath, CancellationToken.None) == ScanOutcome.Clean; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FilePolicyException or System.ComponentModel.Win32Exception) { return false; }
        finally { try { File.Delete(probe); } catch (IOException) { } }
    }
}

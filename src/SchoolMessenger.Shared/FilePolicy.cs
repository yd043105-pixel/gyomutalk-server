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
    public static async Task Scan(string path, string name, bool development, string? scannerPath, CancellationToken cancellation)
    {
        if (!Signature(path, Path.GetExtension(name).ToLowerInvariant())) throw new FilePolicyException("파일 내용과 확장자가 일치하지 않습니다.");
        if (development) return;
        var scanner = scannerPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(scanner)) throw new FilePolicyException("첨부파일 검사기를 사용할 수 없습니다. 관리자에게 문의하세요.", 503);
        var info = new ProcessStartInfo(scanner) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-Scan", "-ScanType", "3", "-File", path, "-DisableRemediation" }) info.ArgumentList.Add(argument);
        using var scan = Process.Start(info) ?? throw new FilePolicyException("파일 검사기를 실행하지 못했습니다.", 503);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await scan.WaitForExitAsync(timeout.Token); }
        catch { if (!scan.HasExited) scan.Kill(true); throw; }
        if (scan.ExitCode != 0 || !File.Exists(path)) throw new FilePolicyException("파일 검사 실패 또는 위험 파일입니다.", 422);
    }
}

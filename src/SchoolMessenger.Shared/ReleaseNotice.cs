using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SchoolMessenger.Shared;

public sealed record ReleaseNotice(int Sequence, string Version, string Text)
{
    public bool ShouldShow(int lastConfirmed) => Sequence > lastConfirmed;

    public static ReleaseNotice Load(string audience)
    {
        using var stream = typeof(ReleaseNotice).Assembly.GetManifestResourceStream("SchoolMessenger.UpdateNotes.json")
            ?? throw new InvalidDataException("업데이트 내역이 배포 파일에 없습니다.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var sections = root.GetProperty("audiences").GetProperty(audience).EnumerateObject()
            .Select(section => section.Name + "\n" + string.Join("\n", section.Value.EnumerateArray().Select(item => "• " + item.GetString())));
        return new(root.GetProperty("sequence").GetInt32(), root.GetProperty("version").GetString()!,
            root.GetProperty("releasedOn").GetString() + "\n\n" + string.Join("\n\n", sections));
    }
}

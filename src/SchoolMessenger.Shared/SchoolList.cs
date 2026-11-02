using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SchoolMessenger.Shared;

public sealed record SchoolEntry(string Name,string Address)
{
    public override string ToString()=>Name+"\n"+Address;
}

// Korean schools (초·중·고·특수·각종·그 외) from the 학교기본정보 lists, 2026-11. Names repeat across regions, so a school is its
// name plus road address. The list only helps people pick and confirm a school; the server address still comes from the
// school's own administrator program.
public static class SchoolList
{
    static SchoolEntry[]? all;
    public static SchoolEntry[] All=>all??=Load();
    static SchoolEntry[] Load()
    {
        using var stream=typeof(SchoolList).Assembly.GetManifestResourceStream("SchoolMessenger.Schools.tsv.gz")!;
        using var reader=new StreamReader(new GZipStream(stream,CompressionMode.Decompress));
        return reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>line.Split('\t')).Select(p=>new SchoolEntry(p[0],p.Length>1?p[1]:"")).ToArray();
    }
    static string Squash(string text)=>string.Concat(text.Where(c=>!char.IsWhiteSpace(c)));
    // Every word must appear in the name or address, so "중앙고 서울" narrows same-named schools by region.
    public static SchoolEntry[] Search(string query,int limit=30)
    {
        var words=query.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(Squash).Where(w=>w.Length>0).ToArray();
        if(words.Sum(w=>w.Length)<2)return [];
        var first=words[0];
        return All.Where(s=>{var text=Squash(s.Name+s.Address);return words.All(w=>text.Contains(w,StringComparison.OrdinalIgnoreCase));})
            .OrderBy(s=>Squash(s.Name).StartsWith(first,StringComparison.OrdinalIgnoreCase)?0:Squash(s.Name).Contains(first,StringComparison.OrdinalIgnoreCase)?1:2)
            .ThenBy(s=>s.Name.Length).ThenBy(s=>s.Name,StringComparer.Ordinal).ThenBy(s=>s.Address,StringComparer.Ordinal).Take(limit).ToArray();
    }
    public static SchoolEntry[] Named(string name)=>All.Where(s=>Squash(s.Name)==Squash(name)).ToArray();
    public static bool Same(SchoolEntry school,string? name,string? address)=>Squash(school.Name)==Squash(name??"")&&Squash(school.Address)==Squash(address??"");
    // A server that states its school address must be the chosen school; older servers without one are not judged.
    public static string? Mismatch(SchoolEntry? chosen,string? serverName,string? serverAddress)
        =>chosen is null||string.IsNullOrWhiteSpace(serverAddress)||Same(chosen,serverName,serverAddress)?null
            :"이 주소는 "+serverName+"("+serverAddress+") 서버입니다. 고른 학교 "+chosen.Name+"("+chosen.Address+")와 다릅니다. 학교 관리자에게 받은 주소인지 확인하세요.";
}

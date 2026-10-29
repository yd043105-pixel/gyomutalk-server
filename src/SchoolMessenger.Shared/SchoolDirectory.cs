using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SchoolMessenger.Shared;

// The publisher controls this directory. A school name or a LAN discovery reply is not registration approval.
public static class SchoolDirectory
{
    public const string Product="SchoolMessenger.School.1";
    const int MaximumBytes=1024*1024;
    public sealed record Entry(string Id,string Name,string Region,string OfficeAddress,string AnnouncementsAddress)
    {
        public string Label=>Name+" · "+Region;
        public string Address(string service)=>service switch{"office"=>OfficeAddress,"announcements"=>AnnouncementsAddress,_=>throw new InvalidOperationException("학교 연결 종류를 확인하세요.")};
    }
    public sealed record Catalog(string SourceUrl,Entry[] Schools);
    public static Catalog Bundled()
    {
        using var stream=typeof(SchoolDirectory).Assembly.GetManifestResourceStream("SchoolMessenger.SchoolDirectory.json")??throw new InvalidOperationException("학교 목록 파일을 확인하세요.");
        using var memory=new MemoryStream();stream.CopyTo(memory);return Parse(memory.ToArray());
    }
    static bool Label(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&!value.Any(char.IsControl);
    public static bool ServerAddress(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath=="/"&&value.Length<=240;
    static bool SourceAddress(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&value.Length<=500;
    static string Field(JsonElement element,string name)=>element.GetProperty(name).GetString()??"";
    static void Fields(JsonElement element,string[] allowed)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var property in element.EnumerateObject())if(!allowed.Contains(property.Name)||!seen.Add(property.Name))throw new InvalidOperationException("학교 목록의 항목을 확인하세요.");
    }
    public static Catalog Parse(byte[] bytes)
    {
        if(bytes.Length>MaximumBytes)throw new InvalidOperationException("학교 목록이 너무 큽니다.");
        try
        {
            using var json=JsonDocument.Parse(bytes,new(){MaxDepth=6});var root=json.RootElement;
            Fields(root,["schema","sourceUrl","schools"]);
            if(root.GetProperty("schema").GetInt32()!=1)throw new InvalidOperationException("학교 목록 형식을 확인하세요.");
            var source=root.TryGetProperty("sourceUrl",out var url)?url.GetString()??"":"";
            if(source.Length>0&&!SourceAddress(source))throw new InvalidOperationException("학교 목록 배포 주소는 HTTPS여야 합니다.");
            var rows=root.GetProperty("schools");if(rows.GetArrayLength()>4096)throw new InvalidOperationException("학교 목록 한도를 초과했습니다.");
            var ids=new HashSet<string>(StringComparer.Ordinal);var endpoints=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var schools=new List<Entry>();
            foreach(var row in rows.EnumerateArray())
            {
                Fields(row,["id","name","region","officeAddress","announcementsAddress"]);
                var entry=new Entry(Field(row,"id"),Field(row,"name"),Field(row,"region"),Field(row,"officeAddress"),Field(row,"announcementsAddress"));
                if(!Regex.IsMatch(entry.Id,"^[A-Za-z0-9_-]{5,64}$")||!ids.Add(entry.Id)||!Label(entry.Name,80)||!Label(entry.Region,80))throw new InvalidOperationException("학교 식별자·이름·지역을 확인하세요.");
                if(entry.OfficeAddress.Length==0&&entry.AnnouncementsAddress.Length==0)throw new InvalidOperationException("학교 접속 정보가 없습니다.");
                foreach(var address in new[]{entry.OfficeAddress,entry.AnnouncementsAddress}.Where(v=>v.Length>0))
                    if(!ServerAddress(address)||!endpoints.Add(new Uri(address).AbsoluteUri))throw new InvalidOperationException("학교 주소는 서로 겹치지 않는 HTTPS 기본 주소여야 합니다.");
                schools.Add(entry);
            }
            return new(source,schools.ToArray());
        }
        catch(Exception error)when(error is JsonException or KeyNotFoundException or FormatException or ArgumentException){throw new InvalidOperationException("학교 목록 파일을 읽지 못했습니다.");}
    }
    public static Entry[] Search(IEnumerable<Entry> schools,string query,string service)
    {
        query=query.Trim();if(query.Length>80)throw new InvalidOperationException("학교 검색어는 80자 이내로 입력하세요.");
        return schools.Where(e=>e.Address(service).Length>0&&(query.Length==0||e.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||e.Region.Contains(query,StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e=>e.Name,StringComparer.Ordinal).ThenBy(e=>e.Region,StringComparer.Ordinal).Take(100).ToArray();
    }
    static HttpClient Client(HttpMessageHandler? handler)=>new(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(6)};
    static async Task<byte[]> Read(HttpClient client,string url,int maximum,CancellationToken token)
    {
        using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>maximum)throw new InvalidOperationException("학교 연결 응답이 너무 큽니다.");
        using var stream=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buffer=new byte[4096];int count;
        while((count=await stream.ReadAsync(buffer,token))>0){if(memory.Length+count>maximum)throw new InvalidOperationException("학교 연결 응답이 너무 큽니다.");memory.Write(buffer,0,count);}
        return memory.ToArray();
    }
    public static async Task<Catalog> Refresh(Catalog bundled,CancellationToken token=default,HttpMessageHandler? handler=null)
    {
        if(bundled.SourceUrl.Length==0)return bundled;
        if(!SourceAddress(bundled.SourceUrl))throw new InvalidOperationException("학교 목록 배포 주소를 확인하세요.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var client=Client(handler);var next=Parse(await Read(client,bundled.SourceUrl,MaximumBytes,deadline.Token));
        // A downloaded file cannot replace the trusted update origin embedded in the application.
        return new(bundled.SourceUrl,next.Schools);
    }
    public static async Task Verify(Entry school,string service,CancellationToken token=default,HttpMessageHandler? handler=null)
    {
        var address=school.Address(service);if(!ServerAddress(address))throw new InvalidOperationException("선택한 학교의 접속 정보가 없습니다.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var client=Client(handler);using var json=JsonDocument.Parse(await Read(client,new Uri(new Uri(address),"server-info").AbsoluteUri,2048,deadline.Token),new(){MaxDepth=4});var identity=json.RootElement;
        Fields(identity,["product","schoolName","directoryProduct","schoolId","service"]);
        if(Field(identity,"directoryProduct")!=Product||Field(identity,"schoolId")!=school.Id||Field(identity,"service")!=service)throw new InvalidOperationException("선택한 학교와 서버 정보가 다릅니다. 학교 관리자에게 확인하세요.");
    }
}

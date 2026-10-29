using System.Net.Mail;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace SchoolMessenger.Shared;

// This checks a school-maintained review record, not a regulator's approval or a CSAP certificate.
public sealed class DeploymentReview
{
    public sealed record Evidence(string Path,string Sha256);
    public sealed record Connection(string Purpose,string Host,int Port,string Recipient,string Provider,string Kind,string[] ProcessingCountries,string EvidenceId);
    public sealed record Review(int Schema,string Status,string School,string SchoolId,string Service,string Basis,string InstitutionType,string ReviewAuthority,DateTimeOffset ReviewedAt,DateTimeOffset ExpiresAt,string[] ListenUrls,string[] StorageCountries,string[] ManagementCountries,Dictionary<string,Evidence> Documents,Connection[] ExternalServices);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=16,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    readonly Review? record;
    public bool Trial=>record is null;
    public string? SchoolId=>record?.SchoolId;
    public string? SchoolName=>record?.School;
    DeploymentReview(Review? value)=>record=value;
    static InvalidOperationException Invalid(string code)=>new("학교 보안 도입 검토를 확인하세요. ["+code+"]");
    static bool Label(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=160&&!value.Any(char.IsControl);
    static string Url(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||uri.UserInfo.Length!=0||uri.AbsolutePath!="/"||uri.Query.Length!=0||uri.Fragment.Length!=0)throw Invalid("review-listener-invalid");
        return uri.GetComponents(UriComponents.SchemeAndServer,UriFormat.UriEscaped);
    }
    static void NoLinks(string path)
    {
        if(OperatingSystem.IsWindows()&&(path.StartsWith(@"\\")||new DriveInfo(System.IO.Path.GetPathRoot(path)!).DriveType==DriveType.Network))throw Invalid("review-local-file-required");
        for(var current=path;!string.IsNullOrEmpty(current);current=System.IO.Path.GetDirectoryName(current))
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw Invalid("review-linked-file");
    }
    static void NoDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw Invalid("review-duplicate-field");NoDuplicates(property.Value);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())NoDuplicates(item);
    }
    static bool Countries(string[]? values)
    {
        if(values is not {Length:>0 and <=16})return false;
        try{return values.All(v=>v is not null&&Regex.IsMatch(v,"^[A-Z]{2}$")&&new RegionInfo(v).TwoLetterISORegionName==v);}catch(ArgumentException){return false;}
    }
    public static DeploymentReview Load(IConfiguration config,bool development,string service)
    {
        if(development)return new(null);
        try
        {
            var path=config["Operations:SecurityReviewFile"];
            if(string.IsNullOrWhiteSpace(path)||!System.IO.Path.IsPathFullyQualified(path))throw Invalid("review-required");
            path=System.IO.Path.GetFullPath(path);NoLinks(path);
            if(new FileInfo(path).Length>262144)throw Invalid("review-too-large");
            using var source=JsonDocument.Parse(File.ReadAllText(path),new(){MaxDepth=16});NoDuplicates(source.RootElement);
            var value=source.RootElement.Deserialize<Review>(Json)??throw Invalid("review-invalid");
            var now=DateTimeOffset.UtcNow;
            if(value.Schema!=1||value.Status!="approved"||value.Service!=service||!Label(value.School)||!Label(value.ReviewAuthority))throw Invalid("review-not-approved-for-service");
            if(value.SchoolId is null||!Regex.IsMatch(value.SchoolId,"^[A-Za-z0-9_-]{5,64}$")||value.SchoolId!=config["Operations:SchoolId"])throw Invalid("review-school-mismatch");
            if(value.InstitutionType is not ("education-site" or "school-administration" or "other"))throw Invalid("review-institution-invalid");
            if(value.ReviewedAt>now||value.ExpiresAt<=now||value.ExpiresAt<=value.ReviewedAt)throw Invalid("review-date-invalid");
            if(value.Basis is not ("on-premises" or "school-education-41-7" or "private-cloud-41-2"))throw Invalid("review-basis-invalid");
            if(!Countries(value.StorageCountries)||!Countries(value.ManagementCountries))throw Invalid("review-location-unconfirmed");
            if(value.Documents is null||value.Documents.Count>32||value.ExternalServices is null||value.ExternalServices.Length>24)throw Invalid("review-evidence-invalid");
            // School education exceptions keep the local decision, applicability and safeguards.
            // General agency submission documents are required only on the general review paths.
            var required=value.Basis=="school-education-41-7"
                ?new List<string>{"decision","security"}
                :new List<string>{"decision","overview","plan","requirements","network","security"};
            if(value.Basis=="school-education-41-7")
            {
                if(value.InstitutionType!="education-site")throw Invalid("education-applicability-required");
                required.Add("education-applicability");
            }
            if(value.Basis=="private-cloud-41-2")
            {
                if(value.StorageCountries.Any(c=>c!="KR")||value.ManagementCountries.Any(c=>c!="KR"))throw Invalid("domestic-location-required");
                required.AddRange(["domestic-location","public-separation","provider-eligibility","cloud-contract","csap-consideration","national-guidelines"]);
            }
            foreach(var name in required)if(!value.Documents.ContainsKey(name))throw Invalid("review-document-missing");
            foreach(var evidence in value.Documents.Values)
            {
                if(evidence is null||string.IsNullOrWhiteSpace(evidence.Path)||evidence.Path.Length>1024||evidence.Path.Any(char.IsControl)||evidence.Sha256 is null||!Regex.IsMatch(evidence.Sha256,"^[A-Fa-f0-9]{64}$"))throw Invalid("review-evidence-invalid");
                var file=System.IO.Path.GetFullPath(evidence.Path,System.IO.Path.GetDirectoryName(path)!);NoLinks(file);
                if(new FileInfo(file).Length>8*1024*1024)throw Invalid("review-evidence-too-large");
                using var stream=File.OpenRead(file);
                if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream),Convert.FromHexString(evidence.Sha256)))throw Invalid("review-evidence-changed");
            }
            var endpoints=config.GetSection("Kestrel:Endpoints").GetChildren().Select(e=>e["Url"]??"").ToArray();
            if(endpoints.Length==0)endpoints=(config["urls"]??config["ASPNETCORE_URLS"]??"").Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            if(endpoints.Length==0||value.ListenUrls is not {Length:>0 and <=8}||!endpoints.Select(Url).Order().SequenceEqual(value.ListenUrls.Select(Url).Distinct().Order()))throw Invalid("review-listeners-changed");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var connection in value.ExternalServices)
            {
                if(connection is null||connection.Purpose is not ("tunnel" or "school-bridge" or "error-mail" or "operations-mail" or "neis")||!Label(connection.Host)||Uri.CheckHostName(connection.Host)==UriHostNameType.Unknown||connection.Port is <1 or >65535||!Label(connection.Provider)||!Countries(connection.ProcessingCountries)||!value.Documents.ContainsKey(connection.EvidenceId??""))throw Invalid("review-connection-invalid");
                if(connection.Kind is not ("school-owned" or "third-party-cloud" or "public-data"))throw Invalid("review-provider-kind-invalid");
                if(connection.Purpose=="tunnel"&&connection.Kind!="third-party-cloud")throw Invalid("review-tunnel-provider-invalid");
                if(connection.Purpose is "error-mail" or "operations-mail")
                {
                    if(!MailAddress.TryCreate(connection.Recipient,out _)||connection.Port!=587)throw Invalid("review-mail-invalid");
                    if(connection.Host.Equals("smtp.gmail.com",StringComparison.OrdinalIgnoreCase)&&connection.Kind!="third-party-cloud")throw Invalid("review-mail-provider-invalid");
                }
                if(value.Basis=="on-premises"&&connection.Kind=="third-party-cloud")throw Invalid("cloud-review-basis-required");
                if(value.Basis=="private-cloud-41-2"&&connection.ProcessingCountries.Any(c=>c!="KR"))throw Invalid("domestic-processing-required");
                if(!seen.Add(connection.Purpose+"|"+connection.Host+"|"+connection.Port+"|"+connection.Recipient))throw Invalid("review-duplicate-connection");
            }
            var result=new DeploymentReview(value);
            if(!string.IsNullOrWhiteSpace(config["Announcements:Address"]))
            {
                var uri=new Uri(Url(config["Announcements:Address"]!));result.Demand("school-bridge",uri.Host,uri.Port);
            }
            if(config.GetValue("Portal:TunnelOnly",false))
            {
                var host=config["Portal:PublicHost"];
                if(string.IsNullOrWhiteSpace(host)||!config["Portal:AllowedHosts"]!.Split(',',StringSplitOptions.TrimEntries).Contains(host,StringComparer.OrdinalIgnoreCase))throw Invalid("review-tunnel-host-required");
                result.Demand("tunnel",host,443);
            }
            if(config.GetValue("FeedbackMail:Enabled",false))result.Demand("error-mail",config["FeedbackMail:Host"]??"",config.GetValue("FeedbackMail:Port",587),config["FeedbackMail:Recipient"]??"");
            if(config.GetValue("Operations:AlertMailEnabled",false))result.Demand("operations-mail",config["Operations:AlertMailHost"]??"",587,config["Operations:AlertMailRecipient"]??"");
            return result;
        }
        catch(InvalidOperationException){throw;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException or NotSupportedException){throw Invalid("review-unreadable-or-invalid");}
    }
    public void Demand(string purpose,string host,int port,string recipient="")
    {
        if(record is null)return;
        if(record.ExpiresAt<=DateTimeOffset.UtcNow)throw Invalid("review-expired");
        if(!record.ExternalServices.Any(c=>c.Purpose==purpose&&c.Host.Equals(host,StringComparison.OrdinalIgnoreCase)&&c.Port==port&&string.Equals(c.Recipient??"",recipient,StringComparison.Ordinal)))throw Invalid("review-destination-not-approved");
    }
    public object Summary()=>new{state=record is null?"trial":record.ExpiresAt>DateTimeOffset.UtcNow?"school-record-checked":"expired",school=record?.School,service=record?.Service,basis=record?.Basis,reviewedAt=record?.ReviewedAt,expiresAt=record?.ExpiresAt,externalServiceCount=record?.ExternalServices.Length??0,certificationVerified=false};
    public static void Map(WebApplication app,DeploymentReview review)=>app.MapGet("/api/admin/deployment-review",()=>Results.Ok(review.Summary())).RequireAuthorization("Admin");
}

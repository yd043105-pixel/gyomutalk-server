using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

// Each server archives documents and agreements in its own database.
public sealed class RegistrationPolicy
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public RegistrationPolicySet Current{get;}
    public RegistrationPolicy(string? file,bool development,string contentRoot)
    {
        string text;
        if(string.IsNullOrWhiteSpace(file))
        {using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SchoolMessenger.RegistrationDraft.json")!;using var reader=new StreamReader(stream);text=reader.ReadToEnd();}
        else
        {var path=Path.GetFullPath(file,contentRoot);if(new FileInfo(path).Length>131_072)throw new InvalidOperationException("회원가입 약관 파일은 128KB 이하여야 합니다.");text=File.ReadAllText(path);}
        var source=JsonSerializer.Deserialize<PolicySource>(text,Json)??throw new InvalidOperationException("회원가입 약관 파일을 확인하세요.");
        if(string.IsNullOrWhiteSpace(source.Operator)||string.IsNullOrWhiteSpace(source.Contact)||source.Documents is not{Length:>=2 and <=10}
           ||source.Documents.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()!=source.Documents.Length
           ||!source.Documents.Any(d=>d.Id=="terms"&&d.Required)||!source.Documents.Any(d=>d.Id=="privacy"&&d.Required)
           ||source.Documents.Any(d=>string.IsNullOrWhiteSpace(d.Id)||d.Id.Length>64||string.IsNullOrWhiteSpace(d.Title)||d.Title.Length>150||string.IsNullOrWhiteSpace(d.Version)||d.Version.Length>64||string.IsNullOrWhiteSpace(d.Body)||d.Body.Length>40_000||d.Action is not("consent"or"acknowledgment")))
            throw new InvalidOperationException("약관의 제목·버전·본문·필수 구분·확인/동의 방식을 확인하세요.");
        var revision=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source,Json))));
        var enabled=development||source.ApprovedForProduction;
        Current=new(revision,enabled,!source.ApprovedForProduction,enabled?(source.ApprovedForProduction?"학교가 확정한 약관을 확인하세요.":"검토용 초안입니다. 개발 시운행에서만 가입할 수 있습니다."):"학교의 약관·개인정보 안내가 확정되지 않아 신규 가입을 준비 중입니다.",source.Operator,source.Contact,source.Documents);
    }
    public string? Validate(RegistrationAcceptance? acceptance)
    {
        if(!Current.CanRegister)return Current.Message;
        if(acceptance?.Revision!=Current.Revision)return "약관이 변경되었거나 확인 정보가 없습니다. 가입 화면을 다시 열어 확인하세요.";
        var ids=acceptance.AcceptedIds;
        if(ids is null||ids.Length>Current.Documents.Length||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length
           ||ids.Any(id=>!Current.Documents.Any(d=>d.Id==id))||Current.Documents.Any(d=>d.Required&&!ids.Contains(d.Id,StringComparer.Ordinal)))
            return "필수 약관·개인정보 안내를 확인하고 체크하세요. 선택 항목은 동의하지 않아도 가입할 수 있습니다.";
        return null;
    }
    public static void EnsureSchema(SqliteConnection db)
    {
        using var command=db.CreateCommand();command.CommandText="""
            CREATE TABLE IF NOT EXISTS RegistrationPolicies(Revision TEXT PRIMARY KEY,PolicyJson TEXT NOT NULL,ArchivedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS RegistrationAgreements(SubjectId TEXT NOT NULL,Revision TEXT NOT NULL REFERENCES RegistrationPolicies(Revision),AcceptedIds TEXT NOT NULL,AcceptedAt INTEGER NOT NULL,PRIMARY KEY(SubjectId,Revision));
            """;command.ExecuteNonQuery();
    }
    public void Record(SqliteConnection db,SqliteTransaction tx,string subject,RegistrationAcceptance acceptance)
    {
        if(Validate(acceptance) is{} error)throw new InvalidOperationException(error);
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="""
            INSERT OR IGNORE INTO RegistrationPolicies VALUES($revision,$policy,$now);
            INSERT INTO RegistrationAgreements VALUES($subject,$revision,$ids,$now);
            """;
        command.Parameters.AddWithValue("$revision",Current.Revision);command.Parameters.AddWithValue("$policy",JsonSerializer.Serialize(Current,Json));
        command.Parameters.AddWithValue("$subject",subject);command.Parameters.AddWithValue("$ids",JsonSerializer.Serialize(acceptance.AcceptedIds,Json));command.Parameters.AddWithValue("$now",now);command.ExecuteNonQuery();
    }
    record PolicySource(bool ApprovedForProduction,string Operator,string Contact,RegistrationDocument[] Documents);
}

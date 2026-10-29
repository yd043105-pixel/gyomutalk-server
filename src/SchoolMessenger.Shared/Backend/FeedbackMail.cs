using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SchoolMessenger.Shared;

public sealed record FeedbackEnvelope(string Id,string Subject,string Body);

public sealed class FeedbackMail(Func<SqliteConnection> open,string service,IDataProtectionProvider protection,IConfiguration config,Action<string,string>? validateDestination=null):BackgroundService
{
    readonly IDataProtector protector=protection.CreateProtector("SchoolMessenger.Feedback.v1."+service);
    static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static SqliteCommand Command(SqliteConnection db,string sql,params (string,object?)[] values)
    {var c=db.CreateCommand();c.CommandText=sql;foreach(var(key,value) in values)c.Parameters.AddWithValue(key,value??DBNull.Value);return c;}
    public static void EnsureSchema(SqliteConnection db)
    {
        using var c=Command(db,"""
            CREATE TABLE IF NOT EXISTS FeedbackMail(
              ReportId TEXT PRIMARY KEY REFERENCES FeedbackReports(Id) ON DELETE CASCADE,
              Attempts INTEGER NOT NULL DEFAULT 0,NextAttempt INTEGER NOT NULL DEFAULT 0,SentAt INTEGER,LastError TEXT);
            CREATE INDEX IF NOT EXISTS IX_FeedbackMail_Pending ON FeedbackMail(NextAttempt) WHERE SentAt IS NULL;
            """);c.ExecuteNonQuery();
    }
    public static string Subject(long at,string name)
    {
        var clean=new string(name.Where(c=>!char.IsControl(c)&&CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.Format).Take(50).ToArray()).Trim();
        if(clean.Length==0)clean="사용자";
        var local=TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at),TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        return "[교무톡 오류로그]"+local.ToString("MM월dd일HH시mm분",CultureInfo.InvariantCulture)+" "+clean+" 오류";
    }
    public object Status()
    {
        using var db=open();using var c=Command(db,"SELECT COUNT(*),SUM(CASE WHEN SentAt IS NOT NULL THEN 1 ELSE 0 END),SUM(CASE WHEN SentAt IS NULL AND Attempts>=8 THEN 1 ELSE 0 END) FROM FeedbackMail");
        using var r=c.ExecuteReader();r.Read();var total=r.GetInt32(0);var sent=r.IsDBNull(1)?0:r.GetInt32(1);var failed=r.IsDBNull(2)?0:r.GetInt32(2);
        return new{enabled=config.GetValue<bool>("FeedbackMail:Enabled"),configured=Configured(),pending=total-sent-failed,sent,failed};
    }
    bool Configured()=>!string.IsNullOrWhiteSpace(config["FeedbackMail:Host"])&&config.GetValue<int>("FeedbackMail:Port",587)==587
        &&MailAddress.TryCreate(config["FeedbackMail:Sender"],out _)&&MailAddress.TryCreate(config["FeedbackMail:Recipient"],out _)
        &&!string.IsNullOrEmpty(config["FeedbackMail:Username"])&&!string.IsNullOrEmpty(config["FeedbackMail:Password"]);

    // One sender per data root; durable reservation survives shutdown. SMTP acceptance/ack cannot be atomic.
    public async Task<bool> DeliverOne(Func<FeedbackEnvelope,CancellationToken,Task> send,CancellationToken token=default)
    {
        string id,payload,name;long created;
        using(var db=open())using(var tx=db.BeginTransaction())
        {
            using var c=Command(db,"SELECT f.Id,f.Payload,p.Name,f.CreatedAt FROM FeedbackMail m JOIN FeedbackReports f ON f.Id=m.ReportId JOIN "+(service=="office"?"Users":"People")+" p ON p.Id=f.OwnerId WHERE m.SentAt IS NULL AND m.Attempts<8 AND m.NextAttempt<=$now AND p.Active=1 ORDER BY f.CreatedAt,f.Id LIMIT 1",("$now",Now));c.Transaction=tx;
            using(var r=c.ExecuteReader()){if(!r.Read())return false;id=r.GetString(0);payload=r.GetString(1);name=r.GetString(2);created=r.GetInt64(3);}
            using var reserve=Command(db,"UPDATE FeedbackMail SET Attempts=Attempts+1,NextAttempt=$next WHERE ReportId=$id",("$next",Now+300_000),("$id",id));reserve.Transaction=tx;reserve.ExecuteNonQuery();tx.Commit();
        }
        try
        {
            var report=System.Text.Json.JsonSerializer.Deserialize<SchoolMessenger.Contracts.FeedbackRequest>(protector.Unprotect(payload),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            var version=Regex.IsMatch(report.Version,@"^\d{1,4}\.\d{1,4}\.\d{1,4}(?:[.-][A-Za-z0-9.-]{1,20})?$")?report.Version:"확인 필요";
            var body="교무톡 오류가 접수되었습니다.\r\n접수 번호: "+service+"-"+id+"\r\n프로그램: "+report.Product+"\r\n버전: "+version+"\r\n기능: "+report.Area+
                "\r\n\r\n신고 원문·메시지·첨부·비밀번호는 이메일에 포함하지 않습니다.\r\n관리자 프로그램의 오류 신고 현황에서 재현 설명을 확인하세요.";
            // ErrorCode is rejected by the public report API. Only server-created diagnostics use this field.
            if(report.ErrorCode is not null&&Regex.IsMatch(report.ErrorCode,"^[A-Za-z0-9_]{1,80}$"))body+="\r\n자동 기술 진단:\r\n"+report.Details;
            await send(new(service+"-"+id,Subject(created,name),body),token);
            using var db=open();using var done=Command(db,"UPDATE FeedbackMail SET SentAt=$now,LastError=NULL WHERE ReportId=$id",("$now",Now),("$id",id));done.ExecuteNonQuery();
        }
        catch(Exception error) when(error is not OperationCanceledException||!token.IsCancellationRequested)
        {
            using var db=open();using var retry=Command(db,"UPDATE FeedbackMail SET LastError='delivery-failed',NextAttempt=$now+MIN(3600000,60000*(1 << MIN(Attempts-1,6))) WHERE ReportId=$id AND SentAt IS NULL",("$now",Now),("$id",id));retry.ExecuteNonQuery();
        }
        return true;
    }
    public static MailMessage CreateMessage(FeedbackEnvelope envelope,string sender,string recipient)
    {
        var message=new MailMessage(sender,recipient){Subject=envelope.Subject,Body=envelope.Body,IsBodyHtml=false,SubjectEncoding=Encoding.UTF8,BodyEncoding=Encoding.UTF8};
        message.Headers.Add("Message-ID","<yy-feedback-"+envelope.Id+"@school-messenger.invalid>");return message;
    }
    async Task Send(FeedbackEnvelope envelope,CancellationToken token)
    {
        var host=config["FeedbackMail:Host"]!;var recipient=config["FeedbackMail:Recipient"]!;
        validateDestination?.Invoke(host,recipient);
        using var message=CreateMessage(envelope,config["FeedbackMail:Sender"]!,recipient);
        using var smtp=new SmtpClient(host,587){EnableSsl=true,UseDefaultCredentials=false,Credentials=new NetworkCredential(config["FeedbackMail:Username"],config["FeedbackMail:Password"])};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await smtp.SendMailAsync(message,timeout.Token);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{if(config.GetValue<bool>("FeedbackMail:Enabled")&&Configured())await DeliverOne(Send,stoppingToken);}catch(Exception)when(!stoppingToken.IsCancellationRequested){}
            try{await Task.Delay(TimeSpan.FromMinutes(1),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}

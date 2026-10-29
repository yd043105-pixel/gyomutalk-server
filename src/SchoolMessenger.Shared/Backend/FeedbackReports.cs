using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

// Original reports stay on the school's server. Only an admin-reviewed reproduction summary is exportable.
public static class FeedbackReports
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static readonly string[] Products=["windows","teacher-android","teacher-ios","school-android","school-ios","teacher-macos"];
    static readonly string[] Areas=["login","messages","notices","timetable","counseling","rooms","files","other"];
    static readonly string[] PrivacyAreas=["privacy-access","privacy-rectify","privacy-erase","privacy-restrict"];
    static long Now=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static SqliteCommand Command(SqliteConnection db,string sql,SqliteTransaction? tx=null,params (string,object?)[] values)
    {var command=db.CreateCommand();command.CommandText=sql;command.Transaction=tx;foreach(var(key,value) in values)command.Parameters.AddWithValue(key,value??DBNull.Value);return command;}
    static IResult Error(string message,int status=400)=>Results.Json(new{error=message},statusCode:status);
    static string User(HttpContext c)=>c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    static bool Text(string? value,int min,int max)=>value is not null&&value.Trim().Length>=min&&value.Length<=max&&!value.Any(c=>char.IsControl(c)&&c is not '\n' and not '\r' and not '\t');
    public static void Map(WebApplication app,Func<SqliteConnection> open,IDataProtectionProvider protection,string service)
    {
        var protector=protection.CreateProtector("SchoolMessenger.Feedback.v1."+service);
        using(var db=open())using(var schema=Command(db,"""
            CREATE TABLE IF NOT EXISTS FeedbackReports(
              Id TEXT PRIMARY KEY,OwnerId TEXT NOT NULL,ClientId TEXT NOT NULL,Fingerprint TEXT NOT NULL,Payload TEXT NOT NULL,
              Status TEXT NOT NULL DEFAULT 'received',Revision INTEGER NOT NULL DEFAULT 1,Summary TEXT,Approved INTEGER NOT NULL DEFAULT 0,
              CreatedAt INTEGER NOT NULL,UpdatedAt INTEGER NOT NULL,UNIQUE(OwnerId,ClientId));
            CREATE INDEX IF NOT EXISTS IX_Feedback_Owner ON FeedbackReports(OwnerId,CreatedAt);
            """))schema.ExecuteNonQuery();
        using(var db=open())FeedbackMail.EnsureSchema(db);
        app.Use(async(context,next)=>
        {
            Exception? failure=null;
            try{await next();}catch(Exception error){failure=error;throw;}
            finally
            {
                var code=failure is null?context.Response.StatusCode:failure is BadHttpRequestException bad?bad.StatusCode:500;
                if(app.Configuration.GetValue<bool>("Feedback:AutomaticReportsEnabled")&&code>=500&&context.User.Identity?.IsAuthenticated==true
                    &&!context.RequestAborted.IsCancellationRequested&&failure is not OperationCanceledException
                    &&context.Request.Path.StartsWithSegments("/api")&&!context.Request.Path.StartsWithSegments("/api/feedback")&&!context.Request.Path.StartsWithSegments("/api/admin/feedback")
                    &&context.GetEndpoint() is RouteEndpoint endpoint)
                {
                    try
                    {
                        var product=context.Request.Headers["X-YY-Product"].ToString();if(!Products.Contains(product))product=service=="office"?"office-client":"school-client";
                        var version=context.Request.Headers["X-YY-Version"].ToString();if(!System.Text.RegularExpressions.Regex.IsMatch(version,@"^\d{1,4}\.\d{1,4}\.\d{1,4}(?:[.-][A-Za-z0-9.-]{1,20})?$"))version="unknown";
                        var route=endpoint.RoutePattern.RawText??"/api";var errorCode=failure?.GetType().Name??("HTTP_"+code);
                        if(!System.Text.RegularExpressions.Regex.IsMatch(errorCode,"^[A-Za-z0-9_]{1,80}$"))errorCode="HTTP_500";
                        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("automatic|"+product+"|"+version+"|"+route+"|"+code+"|"+errorCode)));
                        using var db=open();using var tx=db.BeginTransaction();
                        using var duplicate=Command(db,"SELECT COUNT(*) FROM FeedbackReports WHERE OwnerId=$owner AND CreatedAt>$day",tx,("$owner",User(context)),("$day",Now-86_400_000));
                        var count=Convert.ToInt64(duplicate.ExecuteScalar());
                        using var recent=Command(db,"SELECT COUNT(*) FROM FeedbackReports WHERE OwnerId=$owner AND Fingerprint=$hash AND CreatedAt>$recent",tx,("$owner",User(context)),("$hash",fingerprint),("$recent",Now-1_800_000));
                        using var total=Command(db,"SELECT COUNT(*) FROM FeedbackReports",tx);
                        if(count<10&&Convert.ToInt64(recent.ExecuteScalar())==0&&Convert.ToInt64(total.ExecuteScalar())<10000)
                        {
                            var segment=route.Split('/',StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()??"";
                            var area=segment switch{"messages" or "chats"=>"messages","notices" or "announcements" or "external-announcements"=>"notices","timetable" or "timetables"=>"timetable","counseling"=>"counseling","rooms" or "room-reservations"=>"rooms","attachments" or "submission-requests"=>"files","login" or "password" or "activate"=>"login",_=>"other"};
                            var trace=System.Text.RegularExpressions.Regex.IsMatch(context.TraceIdentifier,"^[A-Za-z0-9:._-]{1,100}$")?context.TraceIdentifier:"확인 필요";
                            var locations=failure is null?"":string.Join("\n",new System.Diagnostics.StackTrace(failure,true).GetFrames().Where(f=>f.GetMethod()?.DeclaringType?.Assembly.GetName().Name is "SchoolMessenger.Server" or "SchoolMessenger.AnnouncementServer").Take(3).Select(f=>{var method=f.GetMethod();var label=method?.DeclaringType?.FullName+"."+method?.Name+" · 줄 "+f.GetFileLineNumber();return label[..Math.Min(160,label.Length)];}));
                            var report=new FeedbackRequest(Guid.NewGuid().ToString("N"),"bug",product,area,"서버 처리 오류 자동 접수","오류 종류: "+errorCode+"\n경로 양식: "+route+"\n요청 추적 번호: "+trace+"\n코드 위치(0은 정보 없음):\n"+locations,version,errorCode);
                            var id=Guid.NewGuid().ToString("N");
                            using var insert=Command(db,"INSERT INTO FeedbackReports(Id,OwnerId,ClientId,Fingerprint,Payload,CreatedAt,UpdatedAt) VALUES($id,$owner,$client,$hash,$payload,$now,$now);INSERT INTO FeedbackMail(ReportId) VALUES($id)",tx,("$id",id),("$owner",User(context)),("$client",report.ClientId),("$hash",fingerprint),("$payload",protector.Protect(JsonSerializer.Serialize(report,Json))),("$now",Now));insert.ExecuteNonQuery();
                        }
                        tx.Commit();
                    }
                    catch{app.Logger.LogWarning("Automatic feedback persistence failed; original request outcome is preserved");}
                }
            }
        });

        app.MapPost("/api/feedback",IResult(FeedbackRequest request,HttpContext c)=>
        {
            if(!Guid.TryParseExact(request.ClientId,"N",out _)||request.Kind is not ("bug" or "suggestion" or "privacy")||!Products.Contains(request.Product)
                ||!(request.Kind=="privacy"?PrivacyAreas.Contains(request.Area):Areas.Contains(request.Area))
                ||!Text(request.Title,3,100)||!Text(request.Details,10,4000)||!Text(request.Version,1,40)||request.ErrorCode is not null)return Error("분류·제목(3~100자)·재현 설명(10~4000자)을 확인하세요.");
            var payload=JsonSerializer.Serialize(request,Json);var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            using var db=open();using var tx=db.BeginTransaction();
            using(var existing=Command(db,"SELECT Id,Fingerprint,Status FROM FeedbackReports WHERE OwnerId=$owner AND ClientId=$client",tx,("$owner",User(c)),("$client",request.ClientId)))
            using(var reader=existing.ExecuteReader())if(reader.Read())return reader.GetString(1)==fingerprint?Results.Ok(new{id=reader.GetString(0),status=reader.GetString(2)}):Error("같은 접수 번호로 내용을 바꿀 수 없습니다.",409);
            using(var count=Command(db,"SELECT COUNT(*) FROM FeedbackReports WHERE OwnerId=$owner AND CreatedAt>$since",tx,("$owner",User(c)),("$since",Now-86_400_000)))
                if(Convert.ToInt64(count.ExecuteScalar())>=10)return Error("하루에 최대 10건까지 접수할 수 있습니다.",429);
            using(var count=Command(db,"SELECT COUNT(*) FROM FeedbackReports",tx))if(Convert.ToInt64(count.ExecuteScalar())>=10000)return Error("접수 보관함이 가득 찼습니다. 관리자에게 문의하세요.",503);
            var id=Guid.NewGuid().ToString("N");
            using(var insert=Command(db,"INSERT INTO FeedbackReports(Id,OwnerId,ClientId,Fingerprint,Payload,CreatedAt,UpdatedAt) VALUES($id,$owner,$client,$hash,$payload,$now,$now)",tx,
                ("$id",id),("$owner",User(c)),("$client",request.ClientId),("$hash",fingerprint),("$payload",protector.Protect(payload)),("$now",Now)))insert.ExecuteNonQuery();
            if(request.Kind=="bug"){using var mail=Command(db,"INSERT INTO FeedbackMail(ReportId) VALUES($id)",tx,("$id",id));mail.ExecuteNonQuery();}
            tx.Commit();return Results.Ok(new{id,status="received"});
        }).RequireAuthorization().WithMetadata(new RequestSizeLimitAttribute(32_768));

        object[] Read(string? owner)
        {
            using var db=open();using var command=Command(db,"SELECT f.Id,f.Payload,f.Status,f.Revision,f.Summary,f.Approved,f.CreatedAt,f.UpdatedAt,m.SentAt,m.Attempts,f.OwnerId FROM FeedbackReports f LEFT JOIN FeedbackMail m ON m.ReportId=f.Id WHERE ($owner IS NULL OR f.OwnerId=$owner) ORDER BY f.CreatedAt DESC,f.Id LIMIT 100",null,("$owner",owner));
            using var reader=command.ExecuteReader();var rows=new List<object>();
            while(reader.Read())rows.Add(new{id=reader.GetString(0),ownerId=owner is null?reader.GetString(10):null,report=JsonSerializer.Deserialize<FeedbackRequest>(protector.Unprotect(reader.GetString(1)),Json),status=reader.GetString(2),revision=reader.GetInt32(3),developmentSummary=reader.IsDBNull(4)?"":protector.Unprotect(reader.GetString(4)),approvedForDevelopment=reader.GetInt32(5)==1,createdAt=reader.GetInt64(6),updatedAt=reader.GetInt64(7),mailStatus=reader.IsDBNull(9)?"none":!reader.IsDBNull(8)?"sent":reader.GetInt32(9)>=8?"failed":"pending"});
            return rows.ToArray();
        }
        app.MapGet("/api/feedback",(HttpContext c)=>Read(User(c))).RequireAuthorization();
        app.MapGet("/api/admin/feedback",()=>Read(null)).RequireAuthorization("Admin");
        app.MapGet("/api/admin/feedback/mail-status",()=>app.Services.GetRequiredService<FeedbackMail>().Status()).RequireAuthorization("Admin");
        app.MapPost("/api/admin/feedback/{id}/retry-mail",IResult(string id)=>
        {
            if(!Guid.TryParseExact(id,"N",out _))return Error("접수 번호를 확인하세요.");
            using var db=open();using var retry=Command(db,"UPDATE FeedbackMail SET Attempts=0,NextAttempt=0,LastError=NULL WHERE ReportId=$id AND SentAt IS NULL",null,("$id",id));
            return retry.ExecuteNonQuery()==1?Results.Ok():Error("재시도할 미발송 신고가 없습니다.",409);
        }).RequireAuthorization("Admin");
        app.MapPost("/api/admin/feedback/{id}/review",IResult(string id,FeedbackReview request)=>
        {
            if(!Guid.TryParseExact(id,"N",out _)||request.Revision<1||request.Status is not ("received" or "investigating" or "verified" or "deferred" or "resolved")
                ||request.DevelopmentSummary is null||request.DevelopmentSummary.Length>2000||request.ApproveForDevelopment&&!Text(request.DevelopmentSummary,10,2000))return Error("상태와 개인정보를 제거한 개발용 재현 설명을 확인하세요.");
            using var db=open();using var tx=db.BeginTransaction();
            using(var lookup=Command(db,"SELECT Payload FROM FeedbackReports WHERE Id=$id",tx,("$id",id)))
            {
                if(lookup.ExecuteScalar() is not string stored)return Error("접수가 없습니다.",404);
                if(JsonSerializer.Deserialize<FeedbackRequest>(protector.Unprotect(stored),Json)?.Kind=="privacy"&&request.ApproveForDevelopment)
                    return Error("개인정보 권리 요청은 개발 자료로 승인하거나 내보낼 수 없습니다.");
            }
            using var command=Command(db,"UPDATE FeedbackReports SET Status=$status,Summary=$summary,Approved=$approved,Revision=Revision+1,UpdatedAt=$now WHERE Id=$id AND Revision=$revision",tx,
                ("$status",request.Status),("$summary",protector.Protect(request.DevelopmentSummary)),("$approved",request.ApproveForDevelopment?1:0),("$now",Now),("$id",id),("$revision",request.Revision));
            if(command.ExecuteNonQuery()!=1)return Error("다른 관리자가 수정했거나 접수가 없습니다. 다시 조회하세요.",409);
            tx.Commit();return Results.Ok(new{saved=true});
        }).RequireAuthorization("Admin").WithMetadata(new RequestSizeLimitAttribute(24_576));
        app.MapGet("/api/admin/feedback/development-export",()=>
        {
            using var db=open();using var command=Command(db,"SELECT Id,Revision,Summary,UpdatedAt,Payload FROM FeedbackReports WHERE Approved=1 AND Status NOT IN ('resolved','deferred') ORDER BY UpdatedAt,Id LIMIT 100");
            using var reader=command.ExecuteReader();var reports=new List<object>();
            while(reader.Read())
            {
                if(JsonSerializer.Deserialize<FeedbackRequest>(protector.Unprotect(reader.GetString(4)),Json)?.Kind=="privacy")continue;
                reports.Add(new{id=service+"-"+reader.GetString(0),revision=reader.GetInt32(1),summary=protector.Unprotect(reader.GetString(2)),updatedAt=reader.GetInt64(3)});
            }
            return Results.Ok(new{schema=1,service,exportedAt=Now,reports});
        }).RequireAuthorization("Admin");
    }
}

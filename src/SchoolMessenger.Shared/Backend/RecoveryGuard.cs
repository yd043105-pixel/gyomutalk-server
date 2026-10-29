using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

public static class RecoveryGuard
{
    public sealed record Confirmation(bool Confirmed);
    public static void Use(WebApplication app,Func<SqliteConnection> open)
    {
        using var initial=open();using var query=initial.CreateCommand();query.CommandText="SELECT ReviewRequired FROM BackendRecoveryState WHERE Id=1";
        var pending=query.ExecuteScalar() is long flag&&flag==1?1:0;
        bool Reviewer(ClaimsPrincipal user)
        {
            if(user.IsInRole("admin"))return true; // Public server has a separate, school-controlled administrator.
            if(!user.IsInRole("Admin"))return false;
            using var db=open();using var root=db.CreateCommand();root.CommandText="SELECT 1 FROM BackendRoot WHERE UserId=$id";
            root.Parameters.AddWithValue("$id",user.FindFirstValue(ClaimTypes.NameIdentifier)!);return root.ExecuteScalar() is not null;
        }
        app.Use(async(context,next)=>
        {
            var path=context.Request.Path;
            if(Volatile.Read(ref pending)==1 && (path.StartsWithSegments("/api")||path.StartsWithSegments("/bridge")||path.StartsWithSegments("/hub")||path.StartsWithSegments("/remote"))
                &&path!="/api/session"&&path!="/api/login"&&path!="/api/logout"
                &&!(path.StartsWithSegments("/api")&&Reviewer(context.User)))
            {context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="백업 복원 후 학교 명부·보호자 관계·권한을 확인 중입니다. 관리자가 확인을 마치면 이용할 수 있습니다."});return;}
            await next();
        });
        app.MapGet("/api/admin/recovery-status",()=>new{reviewRequired=Volatile.Read(ref pending)==1}).RequireAuthorization("Admin");
        app.MapPost("/api/admin/recovery-confirm",(Confirmation confirmation,HttpContext context)=>
        {
            if(!confirmation.Confirmed)return Results.BadRequest(new{error="복원 후 학교 명부·보호자 관계·권한 확인이 필요합니다."});
            using var db=open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT COUNT(*) FROM BackendRoot";var roots=Convert.ToInt32(command.ExecuteScalar());
            if(roots>0){command.CommandText="SELECT 1 FROM BackendRoot WHERE UserId=$id";command.Parameters.AddWithValue("$id",context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);if(command.ExecuteScalar() is null)return Results.StatusCode(403);command.Parameters.Clear();}
            command.CommandText="UPDATE BackendRecoveryState SET ReviewRequired=0 WHERE Id=1";command.ExecuteNonQuery();
            BackendOperations.Audit(db,tx,context.User.FindFirstValue(ClaimTypes.NameIdentifier),"recovery.confirm","success",context.TraceIdentifier);tx.Commit();Volatile.Write(ref pending,0);return Results.Ok();
        }).RequireAuthorization("Admin");
    }
}

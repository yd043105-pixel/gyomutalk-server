using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SchoolMessenger.Shared;

public static class BackendOperations
{
    static void Execute(Func<SqliteConnection> open, string sql, params (string Name,object? Value)[] parameters)
    {
        using var db=open();using var command=db.CreateCommand();command.CommandText=sql;
        foreach(var (name,value) in parameters)command.Parameters.AddWithValue(name,value??DBNull.Value);command.ExecuteNonQuery();
    }
    public static void Run(Func<SqliteConnection> open,string name,Action operation)
    {
        Execute(open,"INSERT INTO BackendJobs(Name,StartedAt,Attempts) VALUES($name,$now,1) ON CONFLICT(Name) DO UPDATE SET StartedAt=$now,Attempts=Attempts+1",("$name",name),("$now",DeviceSessions.Now));
        try { operation();Execute(open,"UPDATE BackendJobs SET LastSuccess=$now,ErrorCode=NULL WHERE Name=$name",("$name",name),("$now",DeviceSessions.Now)); }
        catch(Exception error){Execute(open,"UPDATE BackendJobs SET LastFailure=$now,ErrorCode=$code WHERE Name=$name",("$name",name),("$now",DeviceSessions.Now),("$code",error.GetType().Name));throw;}
    }
    public static long? LastSuccess(Func<SqliteConnection> open,string name)
    {
        using var db=open();using var command=db.CreateCommand();command.CommandText="SELECT LastSuccess FROM BackendJobs WHERE Name=$name";command.Parameters.AddWithValue("$name",name);
        var value=command.ExecuteScalar();return value is long time?time:null;
    }
    public static void Audit(SqliteConnection db,SqliteTransaction? tx,string? actor,string operation,string result,string trace)
    {
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="INSERT INTO BackendAudit(At,ActorId,Event,Result,TraceId) VALUES($now,$actor,$event,$result,$trace)";
        command.Parameters.AddWithValue("$now",DeviceSessions.Now);command.Parameters.AddWithValue("$actor",(object?)actor??DBNull.Value);
        command.Parameters.AddWithValue("$event",operation);command.Parameters.AddWithValue("$result",result);command.Parameters.AddWithValue("$trace",trace);command.ExecuteNonQuery();
    }
    public static void UseAudit(WebApplication app,Func<SqliteConnection> open,string service)
    {
        app.Use(async(context,next)=>
        {
            var watch=Stopwatch.StartNew();
            try{await next();}
            finally
            {
                var route=(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                if(route is not null&&(context.Request.Path.StartsWithSegments("/api")||context.Request.Path.StartsWithSegments("/bridge")))
                {
                    // A route template contains no student names, query parameters, credentials or payloads.
                    app.Logger.LogInformation("Backend request {Service} {Event} {TraceId} {Result} {DurationMs}",service,context.Request.Method+" "+route,context.TraceIdentifier,context.Response.StatusCode,watch.ElapsedMilliseconds);
                    if(context.Request.Method is "POST" or "PATCH" or "DELETE")
                    {
                        try{using var db=open();Audit(db,null,context.User.FindFirstValue(ClaimTypes.NameIdentifier),context.Request.Method+" "+route,context.Response.StatusCode.ToString(),context.TraceIdentifier);}
                        catch(Exception error){app.Logger.LogCritical("Backend audit unavailable ({ErrorCode}, {TraceId})",error.GetType().Name,context.TraceIdentifier);}
                    }
                }
            }
        });
    }
    public static void Map(WebApplication app,Func<SqliteConnection> open)
    {
        app.MapGet("/api/admin/backend-status",()=>
        {
            using var db=open();using var command=db.CreateCommand();command.CommandText="SELECT Name,StartedAt,LastSuccess,LastFailure,Attempts,ErrorCode FROM BackendJobs ORDER BY Name";
            using var reader=command.ExecuteReader();var jobs=new List<object>();
            object? Value(int index)=>reader.IsDBNull(index)?null:reader.GetValue(index);
            while(reader.Read())jobs.Add(new{name=Value(0),startedAt=Value(1),lastSuccess=Value(2),lastFailure=Value(3),attempts=Value(4),errorCode=Value(5)});
            reader.Close();command.CommandText="SELECT sqlite_version()";var sqliteVersion=command.ExecuteScalar()?.ToString();
            return Results.Ok(new{jobs,sqliteVersion});
        }).RequireAuthorization("Admin");
    }
}

using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

public static class MessageRecall
{
    // The same SQLite write transaction serializes opening a message and recalling it.
    public static bool Read(SqliteConnection connection,SqliteTransaction tx,string id,string user)
    {
        using var find=Store.Command(connection,"SELECT SenderId FROM Messages WHERE Id=$id AND NOT EXISTS(SELECT 1 FROM MessageRecalls WHERE MessageId=$id) AND (SenderId=$user OR EXISTS(SELECT 1 FROM Recipients WHERE MessageId=$id AND UserId=$user))",tx,("$id",id),("$user",user));
        if(find.ExecuteScalar() is not string sender)return false;
        using var mark=Store.Command(connection,"UPDATE Recipients SET ReadAt=$now WHERE MessageId=$id AND UserId=$user AND ReadAt IS NULL",tx,("$now",Maintenance.Now),("$id",id),("$user",user));
        if(mark.ExecuteNonQuery()>0)MessageDelivery.Enqueue(connection,tx,"read:"+id+":"+user,sender,"ReadChanged",id,Maintenance.Now);
        return true;
    }
    public static void Map(RouteGroupBuilder api,Store store)
    {
        api.MapPost("/messages/{id}/recall",IResult(string id,HttpContext context)=>
        {
            var user=context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
            using var connection=store.Open();using var tx=connection.BeginTransaction();
            using var own=Store.Command(connection,"SELECT 1 FROM Messages WHERE Id=$id AND SenderId=$user AND NOT EXISTS(SELECT 1 FROM ChatEntries WHERE MessageId=$id) AND Fingerprint<>'system-reminder'",tx,("$id",id),("$user",user));
            if(own.ExecuteScalar() is null)return Results.NotFound();
            using var existing=Store.Command(connection,"SELECT 1 FROM MessageRecalls WHERE MessageId=$id",tx,("$id",id));
            if(existing.ExecuteScalar() is not null)return Results.Ok(new{recalled=true});
            using var unread=Store.Command(connection,"SELECT EXISTS(SELECT 1 FROM Recipients WHERE MessageId=$id AND ReadAt IS NULL)",tx,("$id",id));
            if(Convert.ToInt32(unread.ExecuteScalar())==0)return Results.Json(new{error="모든 수신자가 읽은 메시지는 회수할 수 없습니다."},statusCode:409);
            var targets=new List<string>{user};using(var recipients=Store.Command(connection,"SELECT UserId FROM Recipients WHERE MessageId=$id",tx,("$id",id)))using(var reader=recipients.ExecuteReader())while(reader.Read())targets.Add(reader.GetString(0));
            foreach(var target in targets.Distinct())MessageDelivery.Enqueue(connection,tx,"recall:"+id+":"+target,target,"MessageRecalled",id,Maintenance.Now);
            foreach(var sql in new[]{"INSERT INTO MessageRecalls VALUES($id,$now)","UPDATE Messages SET Title='회수된 메시지',Body='' WHERE Id=$id","DELETE FROM Todos WHERE SourceId=$id AND Status='suggested'","UPDATE SubmissionRequests SET Closed=1 WHERE MessageId=$id","UPDATE Attachments SET ExpiresAt=MIN(ExpiresAt,$now) WHERE MessageId=$id"})
            {using var edit=Store.Command(connection,sql,tx,("$id",id),("$now",Maintenance.Now));edit.ExecuteNonQuery();}
            tx.Commit();return Results.Ok(new{recalled=true});
        });
    }
}

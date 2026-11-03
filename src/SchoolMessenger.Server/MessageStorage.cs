using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

// Messages and chat live on the people's PCs; the server only passes them on. Once a person's PC confirmed its copy the server
// stops handing that person the content, so the same account on another PC cannot read it. Once the sender and every recipient
// confirmed, title and body are gone, and never later than attachments. Receipts and attachment links remain.
public static class MessageStorage
{
    const long Retention=2_592_000_000;
    // The request's name stays with a file collection, which is a work record rather than a message.
    const string Blanked="Body='',Title=CASE WHEN EXISTS(SELECT 1 FROM SubmissionRequests sr WHERE sr.MessageId=Messages.Id) THEN Title ELSE '' END";
    public const string Kept="((m.SenderId=$user AND m.SenderStoredAt IS NOT NULL) OR EXISTS(SELECT 1 FROM Recipients kept WHERE kept.MessageId=m.Id AND kept.UserId=$user AND kept.StoredAt IS NOT NULL))";
    public static void Migrate(Store store)
    {
        if(!store.Query("PRAGMA table_info(Recipients)",r=>r.GetString(1)).Contains("StoredAt"))store.Execute("ALTER TABLE Recipients ADD COLUMN StoredAt INTEGER");
        if(!store.Query("PRAGMA table_info(Messages)",r=>r.GetString(1)).Contains("SenderStoredAt"))store.Execute("ALTER TABLE Messages ADD COLUMN SenderStoredAt INTEGER");
        // Messages from before this storage model get the full grace period from the upgrade, so PCs can fetch them first.
        store.Execute("CREATE TABLE IF NOT EXISTS MessageStorage(Since INTEGER NOT NULL)");
        store.Execute("INSERT INTO MessageStorage SELECT $now WHERE NOT EXISTS(SELECT 1 FROM MessageStorage)",("$now",Maintenance.Now));
        store.Execute("UPDATE Messages SET "+Blanked+" WHERE Body='' AND Title<>''");
    }
    static void Release(SqliteConnection connection,SqliteTransaction tx,string condition,params (string,object)[] values)
    {
        using(var body=Store.Command(connection,"UPDATE Messages SET "+Blanked+" WHERE Body<>'' AND "+condition,tx,values))body.ExecuteNonQuery();
        using var format=Store.Command(connection,"DELETE FROM MessageFormats WHERE MessageId IN (SELECT Id FROM Messages WHERE Body='')",tx);format.ExecuteNonQuery();
    }
    public static void Expire(Store store)
    {
        using var connection=store.Open();using var tx=connection.BeginTransaction();
        Release(connection,tx,"MAX(CreatedAt,(SELECT Since FROM MessageStorage))<=$cutoff",("$cutoff",Maintenance.Now-Retention));
        using(var ballots=Store.Command(connection,"DELETE FROM SurveyBallots WHERE SurveyId IN (SELECT Id FROM Surveys WHERE Deadline<=$cutoff)",tx,("$cutoff",Maintenance.Now-Retention)))ballots.ExecuteNonQuery();
        tx.Commit();
        // The write-ahead log still holds what was just wiped until it is emptied.
        using var truncate=connection.CreateCommand();truncate.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";truncate.ExecuteNonQuery();
    }
    const string RoomSaved="Id IN (SELECT MessageId FROM ChatEntries WHERE RoomId=$room) AND SenderStoredAt IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=Messages.Id AND r.StoredAt IS NULL)";
    static void Confirm(SqliteConnection connection,SqliteTransaction tx,string room,string user,long sequence)
    {
        using(var mine=Store.Command(connection,"UPDATE Recipients SET StoredAt=COALESCE(StoredAt,$now) WHERE UserId=$user AND MessageId IN (SELECT MessageId FROM ChatEntries WHERE RoomId=$room AND Sequence<=$sequence)",tx,("$user",user),("$room",room),("$sequence",sequence),("$now",Maintenance.Now)))mine.ExecuteNonQuery();
        using(var sent=Store.Command(connection,"UPDATE Messages SET SenderStoredAt=COALESCE(SenderStoredAt,$now) WHERE SenderId=$user AND Id IN (SELECT MessageId FROM ChatEntries WHERE RoomId=$room AND Sequence<=$sequence)",tx,("$user",user),("$room",room),("$sequence",sequence),("$now",Maintenance.Now)))sent.ExecuteNonQuery();
        Release(connection,tx,RoomSaved,("$room",room));
    }
    // Someone who left a room will never save the rest, and must not hold back everyone else's copies.
    public static void Leave(Store store,string room,string user)
    {
        using var connection=store.Open();using var tx=connection.BeginTransaction();
        Confirm(connection,tx,room,user,long.MaxValue);tx.Commit();
    }
    public static void Map(RouteGroupBuilder api,Store store)
    {
        api.MapPost("/messages/{id}/stored",IResult(string id,HttpContext context)=>
        {
            var user=context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
            using var connection=store.Open();using var tx=connection.BeginTransaction();
            using var sender=Store.Command(connection,"UPDATE Messages SET SenderStoredAt=COALESCE(SenderStoredAt,$now) WHERE Id=$id AND SenderId=$user",tx,("$id",id),("$user",user),("$now",Maintenance.Now));
            using var recipient=Store.Command(connection,"UPDATE Recipients SET StoredAt=COALESCE(StoredAt,$now) WHERE MessageId=$id AND UserId=$user",tx,("$id",id),("$user",user),("$now",Maintenance.Now));
            if(sender.ExecuteNonQuery()+recipient.ExecuteNonQuery()==0)return Results.NotFound();
            Release(connection,tx,"Id=$id AND SenderStoredAt IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Recipients r WHERE r.MessageId=$id AND r.StoredAt IS NULL)",("$id",id));
            tx.Commit();return Results.Ok();
        });
        api.MapPost("/chats/{id}/stored",IResult(string id,ChatRead request,HttpContext context)=>
        {
            var user=context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;
            if(store.Query("SELECT 1 FROM ChatMembers WHERE RoomId=$room AND UserId=$user",r=>r.GetInt32(0),("$room",id),("$user",user)).Count==0)return Results.NotFound();
            using var connection=store.Open();using var tx=connection.BeginTransaction();
            Confirm(connection,tx,id,user,request.Sequence);tx.Commit();return Results.Ok();
        });
    }
}

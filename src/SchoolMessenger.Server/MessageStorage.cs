using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

// Message bodies live on the people's PCs. The server holds a body only until the sender's and every recipient's PC
// confirm they saved it, and never longer than attachments are kept. Titles, receipts and attachment links remain.
public static class MessageStorage
{
    const long Retention=2_592_000_000;
    const string Releasable="Body<>'' AND NOT EXISTS(SELECT 1 FROM ChatEntries ce WHERE ce.MessageId=Messages.Id)";
    public static void Migrate(Store store)
    {
        if(!store.Query("PRAGMA table_info(Recipients)",r=>r.GetString(1)).Contains("StoredAt"))store.Execute("ALTER TABLE Recipients ADD COLUMN StoredAt INTEGER");
        if(!store.Query("PRAGMA table_info(Messages)",r=>r.GetString(1)).Contains("SenderStoredAt"))store.Execute("ALTER TABLE Messages ADD COLUMN SenderStoredAt INTEGER");
        // Messages from before this storage model get the full grace period from the upgrade, so PCs can fetch them first.
        store.Execute("CREATE TABLE IF NOT EXISTS MessageStorage(Since INTEGER NOT NULL)");
        store.Execute("INSERT INTO MessageStorage SELECT $now WHERE NOT EXISTS(SELECT 1 FROM MessageStorage)",("$now",Maintenance.Now));
    }
    static void Release(SqliteConnection connection,SqliteTransaction tx,string condition,params (string,object)[] values)
    {
        using(var body=Store.Command(connection,"UPDATE Messages SET Body='' WHERE "+Releasable+" AND "+condition,tx,values))body.ExecuteNonQuery();
        using var format=Store.Command(connection,"DELETE FROM MessageFormats WHERE MessageId IN (SELECT Id FROM Messages WHERE Body='')",tx);format.ExecuteNonQuery();
    }
    public static void Expire(Store store)
    {
        using var connection=store.Open();using var tx=connection.BeginTransaction();
        Release(connection,tx,"MAX(CreatedAt,(SELECT Since FROM MessageStorage))<=$cutoff",("$cutoff",Maintenance.Now-Retention));tx.Commit();
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
    }
}

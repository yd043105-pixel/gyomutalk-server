using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Server;

public sealed class MessageDelivery(Store store, IHubContext<MessageHub> hub, ILogger<MessageDelivery> log) : BackgroundService
{
    public static void Enqueue(SqliteConnection db, SqliteTransaction tx, string key, string recipient, string kind, string resource, long now)
    {
        using var command = Store.Command(db, """
            INSERT OR IGNORE INTO MessageEvents(EventKey,RecipientId,Kind,ResourceId,CreatedAt)
            VALUES($key,$recipient,$kind,$resource,$now)
            """, tx, ("$key", key), ("$recipient", recipient), ("$kind", kind), ("$resource", resource), ("$now", now));
        command.ExecuteNonQuery();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var events = store.Query("SELECT Sequence,RecipientId,Kind,ResourceId,Attempts FROM MessageEvents WHERE DeliveredAt IS NULL AND NextAttempt<=$now ORDER BY Sequence LIMIT 100",
                    r => (Sequence:r.GetInt64(0), Recipient:r.GetString(1), Kind:r.GetString(2), Resource:r.GetString(3), Attempts:r.GetInt32(4)), ("$now", Maintenance.Now));
                foreach (var item in events)
                {
                    try
                    {
                        if (store.GetUser(item.Recipient) is { Active:true })
                            await hub.Clients.Group(item.Recipient).SendAsync(item.Kind, item.Resource, stoppingToken);
                        // Delivered means a realtime hint was dispatched, never a user read receipt or device ACK.
                        store.Execute("UPDATE MessageEvents SET DeliveredAt=$now,Attempts=Attempts+1 WHERE Sequence=$id",
                            ("$now", Maintenance.Now), ("$id", item.Sequence));
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        store.Execute("UPDATE MessageEvents SET Attempts=Attempts+1,NextAttempt=$next WHERE Sequence=$id",
                            ("$next", Maintenance.Now + Math.Min(300_000L, 1000L << Math.Min(item.Attempts, 8))), ("$id", item.Sequence));
                        log.LogWarning("Message hint retry ({ErrorCode}, {Sequence})", error.GetType().Name, item.Sequence);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning("Message hint worker unavailable ({ErrorCode})", error.GetType().Name); }
            try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}

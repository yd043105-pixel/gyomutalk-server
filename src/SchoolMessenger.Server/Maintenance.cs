using SchoolMessenger.Shared;
using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace SchoolMessenger.Server;

[Authorize]
public sealed class MessageHub(Presence presence, Store store) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var id = Context.UserIdentifier!;
        var identity = new LiveConnection(Context, id, Context.User!.FindFirstValue("version"),Context.User!.FindFirstValue("sid"));
        if (!identity.Valid(store)) { Context.Abort(); return; }
        presence.Live[Context.ConnectionId] = identity;
        presence.Connections[Context.ConnectionId] = id;
        presence.InitializeActivity(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, id);
        await Clients.All.SendAsync("PresenceChanged");
        await base.OnConnectedAsync();
    }
    public override async Task OnDisconnectedAsync(Exception? error)
    {
        presence.Connections.TryRemove(Context.ConnectionId, out _);
        presence.Live.TryRemove(Context.ConnectionId, out _);
        presence.RemoveActivity(Context.ConnectionId);
        await Clients.All.SendAsync("PresenceChanged");
        await base.OnDisconnectedAsync(error);
    }
    public async Task<bool> SetAway(bool away)
    {
        if(!presence.Live.TryGetValue(Context.ConnectionId,out var identity)||!identity.Valid(store))
        {Context.Abort();throw new HubException("로그인 상태를 확인하세요.");}
        if(presence.ChangeActivity(Context.ConnectionId,away))await Clients.All.SendAsync("PresenceChanged");
        return presence.ConnectionAway(Context.ConnectionId);
    }
}
public sealed partial class Presence
{
    sealed record Activity(bool Away,long ChangedAt);
    readonly ConcurrentDictionary<string,Activity> activity=new();
    public ConcurrentDictionary<string, LiveConnection> Live { get; } = new();
    public ConcurrentDictionary<string, string> Connections { get; } = new();
    public bool Online(string userId) => Connections.Values.Contains(userId);
    public bool Away(string userId)
    {var ids=Connections.Where(c=>c.Value==userId).Select(c=>c.Key).ToArray();return ids.Length>0&&ids.All(ConnectionAway);}
    public void InitializeActivity(string connection)=>activity[connection]=new(false,Environment.TickCount64-3000);
    public void RemoveActivity(string connection)=>activity.TryRemove(connection,out _);
    public bool ConnectionAway(string connection)=>activity.TryGetValue(connection,out var state)&&state.Away;
    public bool ChangeActivity(string connection,bool away)
    {
        if(!Connections.ContainsKey(connection)||!activity.TryGetValue(connection,out var state)||state.Away==away)return false;
        var now=Environment.TickCount64;
        // State transitions are bounded per connection; repeated frames never broadcast.
        return now-state.ChangedAt>=3000&&activity.TryUpdate(connection,new(away,now),state);
    }
}
public sealed record LiveConnection(HubCallerContext Context, string UserId, string? Version,string? Session)
{
    public bool Valid(Store store) { using var db=store.Open();return store.GetUser(UserId) is { Active:true } user && user.SessionVersion.ToString()==Version && DeviceSessions.Valid(db,UserId,Version,Session); }
}
public sealed class Maintenance(Store store, IWebHostEnvironment environment, IConfiguration config,
    ILogger<Maintenance> logger) : BackgroundService
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public DateTimeOffset? LastCleanup { get; private set; }
    public DateTimeOffset? LastBackup { get; private set; }
    public string? LastError { get; private set; }
    public void Cleanup()
    {
        lock (AttachmentBackup.Gate) CleanupCore();
    }
    void CleanupCore()
    {
        var expired = store.Query("""
            SELECT Id FROM Attachments WHERE DeletedAt IS NULL AND
            ((ExpiresAt IS NOT NULL AND ExpiresAt <= $now) OR (MessageId IS NULL AND UploadedAt <= $orphan AND NOT EXISTS(SELECT 1 FROM SubmissionFiles sf WHERE sf.AttachmentId=Attachments.Id) AND NOT EXISTS(SELECT 1 FROM ScheduledAttachments sa JOIN ScheduledMessages sm ON sm.Id=sa.ScheduleId WHERE sa.AttachmentId=Attachments.Id AND sm.State='pending')))
            """, r => r.GetString(0), ("$now", Now), ("$orphan", Now - 86_400_000));
        var failed=false;
        foreach (var id in expired)
        {
            try { File.Delete(Path.Combine(store.Files, id));
            store.Execute("UPDATE Attachments SET DeletedAt=$now WHERE Id=$id", ("$now", Now), ("$id", id));
            using var verified=store.Open();StoredFiles.State(verified,"office",id,"expired"); }
            catch(IOException){failed=true;} catch(UnauthorizedAccessException){failed=true;}
        }
        // Recover files left behind by interrupted uploads before their database insert.
        var known = store.Query("SELECT Id FROM Attachments WHERE DeletedAt IS NULL", r => r.GetString(0)).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(store.Files))
            if (!known.Contains(Path.GetFileName(path)) && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                File.Delete(path);
        if(failed)throw new IOException("파일 정리 재시도가 필요합니다.");
        using (var backupDb = store.Open()) AttachmentBackup.Prune(backupDb, store.BackupDirectory, false);
        LastCleanup = DateTimeOffset.UtcNow;
    }
    public void Backup()
    {
        BackendOperations.Run(store.Open,"backup",store.Backup);
        LastBackup = DateTimeOffset.UtcNow;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = environment.IsDevelopment() ? Math.Clamp(config.GetValue("School:CleanupSeconds", 3600), 1, 3600) : 3600;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                BackendOperations.Run(store.Open,"cleanup",Cleanup);
                var backup=BackendOperations.LastSuccess(store.Open,"backup");
                if (backup is null || DateTimeOffset.FromUnixTimeMilliseconds(backup.Value).UtcDateTime.Date != DateTime.UtcNow.Date) Backup();
                LastError = null;
            }
            catch (Exception error)
            {
                LastError = "파일 정리 또는 백업 실패. 서버 기록을 확인하세요.";
                logger.LogError("Maintenance failed ({ErrorCode})",error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

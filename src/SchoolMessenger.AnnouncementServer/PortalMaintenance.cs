using SchoolMessenger.Shared;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.AnnouncementServer;

public sealed class PortalMaintenance(PortalStore store,IConfiguration config,IHostEnvironment environment,ILogger<PortalMaintenance> logger):BackgroundService
{

    public void Cleanup()
    {
        lock (AttachmentBackup.Gate) CleanupCore();
    }
    void CleanupCore()
    {
        var now=PortalSecurity.Now;
        PortalCounseling.Cleanup(store);
        var expired=store.Query("SELECT Id FROM Files WHERE DeletedAt IS NULL AND (ExpiresAt<=$now OR NoticeId IS NULL AND UploadedAt<=$cutoff)",r=>r.GetString(0),("$now",now),("$cutoff",now-86_400_000));
        var failed=false;
        foreach(var id in expired){try{File.Delete(Path.Combine(store.Files,id));store.Execute("UPDATE Files SET DeletedAt=$now WHERE Id=$id",("$now",now),("$id",id));using var verified=store.Open();StoredFiles.State(verified,"portal",id,"expired");}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}}
        foreach(var path in Directory.EnumerateFiles(store.Files))
            if(File.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-1)&&store.Query("SELECT 1 FROM Files WHERE Id=$id",r=>r.GetInt32(0),("$id",Path.GetFileName(path))).Count==0)File.Delete(path);
        if(failed)throw new IOException("파일 정리 재시도가 필요합니다.");
        using (var backupDb = store.Open()) AttachmentBackup.Prune(backupDb, store.BackupDirectory, true);
        var day=DateTime.UtcNow.ToString("yyyyMMdd");var last=BackendOperations.LastSuccess(store.Open,"backup");if(last is not null && DateTimeOffset.FromUnixTimeMilliseconds(last.Value).UtcDateTime.Date==DateTime.UtcNow.Date)return;
        var folder=store.BackupDirectory;Directory.CreateDirectory(folder);
        BackendOperations.Run(store.Open,"backup",()=>{using var source=store.Open();BackendDatabase.Backup(source,Path.Combine(folder,"announcements-"+day+".db"));if(store.AttachmentBackupEnabled)AttachmentBackup.Copy(source,store.Files,folder,true);});
        foreach(var old in Directory.EnumerateFiles(folder,"announcements-*.db"))if(File.GetLastWriteTimeUtc(old)<DateTime.UtcNow.AddDays(-14))File.Delete(old);

    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds=environment.IsDevelopment()?Math.Max(1,config.GetValue("Portal:CleanupSeconds",60)):60;
        while(!stoppingToken.IsCancellationRequested)
        {
            try{BackendOperations.Run(store.Open,"graduation",()=>PortalGraduation.Run(store));}catch(Exception error){logger.LogError("Graduation erasure retry required ({ErrorCode})",error.GetType().Name);}
            try{BackendOperations.Run(store.Open,"cleanup",Cleanup);}catch(Exception error){logger.LogError("Public announcement maintenance failed ({ErrorCode})",error.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(seconds),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}

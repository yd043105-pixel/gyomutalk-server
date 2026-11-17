using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    // Swap requests the timetable manager has not looked at: a notice when one arrives and every five minutes after,
    // like unread mail, until the requests page has shown them.
    static readonly TimeSpan SwapReminder=TimeSpan.FromMinutes(5);
    readonly HashSet<string> swapsNoticed=[];HashSet<string>? swapsSeen;string? swapsSeenUser;
    bool? swapManager;string? swapRoleFor;DateTime swapRoleAt,swapNoticeAt;
    string SwapsSeenFile(string user)=>Path.Combine(LocalDirectory,"swap-requests-seen-"+user+".txt");
    HashSet<string> SwapsSeen(string user)
    {
        if(swapsSeen is null||swapsSeenUser!=user)
        {
            swapsSeenUser=user;try{swapsSeen=File.Exists(SwapsSeenFile(user))?File.ReadAllLines(SwapsSeenFile(user)).Where(l=>l.Length>0).ToHashSet():[];}catch(IOException){swapsSeen=[];}
        }
        return swapsSeen;
    }
    // The requests page calls this with what it showed; only requests still waiting are kept in the file.
    internal void MarkSwapsSeen(IEnumerable<string> shown,IEnumerable<string> pending)
    {
        if(Api?.Session?.User?.Id is not{} user)return;var seen=SwapsSeen(user);var changed=false;
        foreach(var id in shown)changed|=seen.Add(id);var waiting=pending.ToHashSet();changed|=seen.RemoveWhere(id=>!waiting.Contains(id))>0;
        if(!changed)return;try{Directory.CreateDirectory(LocalDirectory);File.WriteAllLines(SwapsSeenFile(user),seen);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    async Task RemindSwaps()
    {
        var source=Api;if(source?.Session?.User?.Id is not{} me||TimetableButton.Visibility!=Visibility.Visible)return;
        try
        {
            // Whether this teacher handles the timetable is looked up again every five minutes.
            if(swapManager is null||swapRoleFor!=me||DateTime.UtcNow-swapRoleAt>SwapReminder){swapManager=(await source.Get<JsonElement>("api/timetable/setup")).GetProperty("canManage").GetBoolean();swapRoleFor=me;swapRoleAt=DateTime.UtcNow;}
            if(swapManager!=true||source!=Api)return;
            var seen=SwapsSeen(me);
            var waiting=(await source.Get<TimetableSwap[]>("api/timetable/swaps")).Where(s=>s.Status is "pending" or "review"&&s.Owner!=me&&!seen.Contains(s.Id)).OrderBy(s=>s.CreatedAt).ToArray();
            if(waiting.Length==0||source!=Api)return;
            var fresh=false;foreach(var swap in waiting)fresh|=swapsNoticed.Add(swap.Id);
            if(!fresh&&DateTime.UtcNow-swapNoticeAt<SwapReminder)return;
            swapNoticeAt=DateTime.UtcNow;var first=waiting[0];
            var where=first.Changes.Length>0?$" · {first.Changes[0].Position.Date} {first.Changes[0].Before.ClassName} {first.Changes[0].Position.Period}교시":"";
            ShowToast(waiting.Length==1?"수업 교체 신청을 확인하세요":$"확인하지 않은 수업 교체 신청 {waiting.Length}건",first.OwnerName+where,"swaps");
        }
        // The reminder never gets in the way of the rest of the refresh.
        catch(Exception e)when(e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException){}
    }
}

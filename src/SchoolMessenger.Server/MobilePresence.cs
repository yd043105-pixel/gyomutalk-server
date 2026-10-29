using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed partial class Presence
{
    sealed record Phone(string UserId,string Version,long ExpiresAt);
    readonly ConcurrentDictionary<string,Phone> phones=new();
    public bool Mobile(string userId,Store store)
    {
        foreach(var entry in phones.Where(p=>p.Value.UserId==userId))
        {
            var phone=entry.Value;
            using var db=store.Open();
            if(phone.ExpiresAt>Environment.TickCount64&&store.GetUser(userId) is {Active:true} user&&user.SessionVersion.ToString()==phone.Version&&DeviceSessions.Valid(db,userId,phone.Version,entry.Key))return true;
            ((ICollection<KeyValuePair<string,Phone>>)phones).Remove(entry);
        }
        return false;
    }
    public static void MapMobile(RouteGroupBuilder api,Store store)
    {
        api.MapPost("/presence/mobile",async(HttpContext c,Presence presence,IHubContext<MessageHub> hub)=>
        {
            var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            foreach(var expired in presence.phones.Where(p=>p.Value.ExpiresAt<=Environment.TickCount64))
                ((ICollection<KeyValuePair<string,Phone>>)presence.phones).Remove(expired);
            var was=presence.Mobile(id,store);
            // Authentication supplies the owner and session. No client-provided user id is accepted.
            presence.phones[c.User.FindFirstValue("sid")!]=new(id,c.User.FindFirstValue("version")!,Environment.TickCount64+90_000);
            if(!was)await hub.Clients.All.SendAsync("PresenceChanged");
            return Results.Ok();
        });
        api.MapPost("/presence/mobile/leave",async(HttpContext c,Presence presence,IHubContext<MessageHub> hub)=>
        {
            var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if(presence.phones.TryRemove(c.User.FindFirstValue("sid")!,out _)&&!presence.Mobile(id,store))await hub.Clients.All.SendAsync("PresenceChanged");
            return Results.Ok();
        });
    }
}

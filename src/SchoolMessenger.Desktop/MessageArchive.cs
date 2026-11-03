using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SchoolMessenger.Desktop;

// This PC copy is the only home of messages, chat and anonymous answers: the server just passes them on.
// With no copy here there is nothing to show, even under the same account.
// DPAPI protects against other Windows accounts; it does not protect a compromised signed-in account.
public sealed class MessageArchive(string root,Api api)
{
    readonly string directory=Path.Combine(root,"messages",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(api.Address.AbsoluteUri+"|"+api.Session!.User!.Id))));
    readonly HashSet<string> recalled=new();
    readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<string,ChatEntry[]> rooms=new();
    string Named(string prefix,string id){if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("번호가 올바르지 않습니다.");return Path.Combine(directory,prefix+id+".dpapi");}
    void Write<T>(string path,T value)
    {
        Directory.CreateDirectory(directory);var temporary=path+".tmp";
        try{File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),null,DataProtectionScope.CurrentUser));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static T? Read<T>(string path)
    {
        if(!File.Exists(path))return default;
        try{return JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser));}
        catch(Exception error)when(error is IOException or JsonException or CryptographicException){return default;}
    }
    // ponytail: one file per room rewritten on every save; split by month if rooms grow past a few MB.
    public ChatEntry[] ChatEntries(string room)
    {
        if(!rooms.TryGetValue(room,out var entries))rooms[room]=entries=Read<ChatEntry[]>(Named("chat-",room))??[];
        return entries;
    }
    public string? ChatPreview(string room)=>ChatEntries(room).LastOrDefault() is{} last?SchoolMessenger.Shared.MessageText.Excerpt(last.Body,80):null;
    // A room's number counts what can be read here: messages only another PC holds would stay unread for good.
    public async Task<ChatRoom[]> LocalRooms(ChatRoom[] server)
    {
        var me=api.Session!.User!.Id;var result=new List<ChatRoom>();
        foreach(var room in server)
        {
            if(room.Unread>0)try{await KeepRoom(room.Id);}catch(ApiRequestException){}
            result.Add(room with{Unread=ChatEntries(room.Id).Count(e=>e.Sequence>room.LastRead&&e.SenderId!=me),Preview=ChatPreview(room.Id)??room.Preview});
        }
        return result.ToArray();
    }
    public void DropRoom(string room){rooms.Remove(room);File.Delete(Named("chat-",room));}
    // Saved first and confirmed after, so a crash in between only means fetching the same messages again.
    public async Task KeepRoom(string room)
    {
        await gate.WaitAsync();
        try
        {
            var kept=ChatEntries(room).ToDictionary(e=>e.Sequence);long newest=0;long? before=null;
            while(true)
            {
                var page=await api.Get<ChatEntry[]>($"api/chats/{room}/messages"+(before is{}?"?before="+before:""));
                foreach(var entry in page){kept[entry.Sequence]=entry;newest=Math.Max(newest,entry.Sequence);}
                if(page.Length<100)break;before=page[0].Sequence;
            }
            if(newest==0)return;
            var all=kept.Values.OrderBy(e=>e.Sequence).ToArray();Write(Named("chat-",room),all);rooms[room]=all;
            // A server not yet updated cannot take the confirmation; it hands the same messages out again until it can.
            try{await api.Send<JsonElement>(HttpMethod.Post,$"api/chats/{room}/stored",new{sequence=newest});}catch(ApiRequestException){}
        }
        finally{gate.Release();}
    }
    public int BallotCount(string survey)=>Read<SurveyResponse[]>(Named("survey-",survey))?.Length??0;
    // Anonymous answers move here from the server. The answers kept here are all the owner will ever see.
    public async Task<SurveyResults> KeepBallots(SurveyResults server,string survey)
    {
        await gate.WaitAsync();
        try
        {
            var path=Named("survey-",survey);var merged=(Read<SurveyResponse[]>(path)??[]).Concat(server.Responses).DistinctBy(r=>r.Id).OrderBy(r=>r.Id,StringComparer.Ordinal).ToArray();
            if(server.Responses.Length>0)
            {
                Write(path,merged);
                foreach(var part in server.Responses.Select(r=>r.Id).Chunk(500))await api.Send<JsonElement>(HttpMethod.Post,$"api/surveys/{survey}/ballots/stored",new{ids=part});
            }
            return server with{Responses=merged};
        }
        finally{gate.Release();}
    }
    public bool Has(string id)=>File.Exists(FilePath(id));
    string FilePath(string id){if(!Guid.TryParseExact(id,"N",out _))throw new InvalidOperationException("메시지 번호가 올바르지 않습니다.");return Path.Combine(directory,id+".dpapi");}
    public void Save(Detail value)
    {
        if(recalled.Contains(value.Message.Id))return;
        Directory.CreateDirectory(directory);var path=FilePath(value.Message.Id);var temporary=path+".tmp";
        try{File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),null,DataProtectionScope.CurrentUser));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Remove(string id){recalled.Add(id);File.Delete(FilePath(id));}
    public Detail? Load(string id)
    {
        var path=FilePath(id);if(!File.Exists(path))return null;
        try{return JsonSerializer.Deserialize<Detail>(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser));}
        catch(Exception error)when(error is IOException or JsonException or CryptographicException){return null;}
    }
    // Server answers carry fresh receipts and attachment state; the body comes from here once the server let it go.
    public Detail Merge(Detail server)
    {
        if(!server.Message.Purged){Save(server);return server;}
        return Load(server.Message.Id) is{} local?server with{Message=server.Message with{Title=local.Message.Title,Body=local.Message.Body,Format=local.Message.Format,Purged=false}}:server;
    }
    public IEnumerable<Detail> Search(string query)
    {
        if(!Directory.Exists(directory))yield break;
        foreach(var path in Directory.EnumerateFiles(directory,"*.dpapi"))
            if(Load(Path.GetFileNameWithoutExtension(path)) is{} value&&(value.Message.Title.Contains(query,StringComparison.CurrentCultureIgnoreCase)||value.Message.Body.Contains(query,StringComparison.CurrentCultureIgnoreCase)))yield return value;
    }
    async Task Keep(string id)
    {
        if(Load(id) is null)
        {
            // A network failure propagates so the sync cursor stays put; a refusal (recalled, no access) is final.
            Detail value;try{value=await api.Get<Detail>("api/messages/"+id+"?peek=true");}catch(ApiRequestException){return;}
            if(value.Message.Purged)return;Save(value);
        }
        try{await api.Send<JsonElement>(HttpMethod.Post,"api/messages/"+id+"/stored");}catch(ApiRequestException){}
    }
    public async Task Synchronize()
    {
        Directory.CreateDirectory(directory);var cursorPath=Path.Combine(directory,"cursor");
        var cursor=File.Exists(cursorPath)&&long.TryParse(File.ReadAllText(cursorPath),out var saved)?saved:0;
        for(var page=0;page<10;page++)
        {
            var changes=await api.Get<JsonElement>("api/messages/changes?after="+cursor);
            foreach(var entry in changes.GetProperty("events").EnumerateArray())
                switch(entry.GetProperty("kind").GetString())
                {
                    case "MessageRecalled":Remove(entry.GetProperty("resourceId").GetString()!);break;
                    case "NewMessage" or "SentMessage":await Keep(entry.GetProperty("resourceId").GetString()!);break;
                    case "ChatChanged":try{await KeepRoom(entry.GetProperty("resourceId").GetString()!);}catch(ApiRequestException){}break;
                }
            cursor=changes.GetProperty("cursor").GetInt64();
            var temporary=cursorPath+".tmp";File.WriteAllText(temporary,cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));File.Move(temporary,cursorPath,true);
            if(!changes.GetProperty("hasMore").GetBoolean())break;
        }
        var me=api.Session!.User!.Id;
        foreach(var survey in await api.Get<SurveyItem[]>("api/surveys"))
            if(survey.Anonymous&&survey.OwnerId==me&&survey.ResponseCount>BallotCount(survey.Id))await KeepBallots(await api.Get<SurveyResults>($"api/surveys/{survey.Id}/results"),survey.Id);
        // Once per PC: keep chat that was on the server before PCs became its store.
        var chatsBackfilled=Path.Combine(directory,"chats-backfilled");
        if(!File.Exists(chatsBackfilled))
        {
            foreach(var room in await api.Get<ChatRoom[]>("api/chats"))try{await KeepRoom(room.Id);}catch(ApiRequestException){}
            File.WriteAllText(chatsBackfilled,"");
        }
        // Once per PC: keep the messages this account received or sent before PCs became the message store.
        var backfilled=Path.Combine(directory,"backfilled");if(File.Exists(backfilled))return;
        foreach(var box in new[]{"received","sent"})
            for(var offset=0;;offset+=100)
            {
                var rows=await api.Get<MessageItem[]>("api/messages?box="+box+"&offset="+offset);
                foreach(var row in rows)await Keep(row.Id);
                if(rows.Length<100)break;
            }
        File.WriteAllText(backfilled,"");
    }
    internal void Verify(Detail value)
    {
        var bytes=File.ReadAllBytes(FilePath(value.Message.Id));
        var restored=JsonSerializer.Deserialize<Detail>(ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser));
        if(JsonSerializer.Serialize(restored?.Message)!=JsonSerializer.Serialize(value.Message)||bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value.Message.Body))>=0)throw new InvalidOperationException("메시지 사본 암호화·복원 검증 실패");
        if(Merge(value with{Message=value.Message with{Body="",Format=null,Purged=true}}).Message.Body!=value.Message.Body)throw new InvalidOperationException("서버가 본문을 지운 뒤 PC 사본으로 복원하지 못했습니다.");
        Remove(value.Message.Id);
        Save(value);
        if(File.Exists(FilePath(value.Message.Id)))throw new InvalidOperationException("회수된 메시지 사본이 다시 저장되었습니다.");
    }
}

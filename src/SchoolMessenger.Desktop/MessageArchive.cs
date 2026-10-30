using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolMessenger.Desktop;

// This PC copy is the message's home: the server drops a body once every party's PC has saved it.
// DPAPI protects against other Windows accounts; it does not protect a compromised signed-in account.
public sealed class MessageArchive(string root,Api api)
{
    readonly string directory=Path.Combine(root,"messages",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(api.Address.AbsoluteUri+"|"+api.Session!.User!.Id))));
    readonly HashSet<string> recalled=new();
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
        return Load(server.Message.Id) is{} local?server with{Message=server.Message with{Body=local.Message.Body,Format=local.Message.Format,Purged=false}}:server;
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
                }
            cursor=changes.GetProperty("cursor").GetInt64();
            var temporary=cursorPath+".tmp";File.WriteAllText(temporary,cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));File.Move(temporary,cursorPath,true);
            if(!changes.GetProperty("hasMore").GetBoolean())break;
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

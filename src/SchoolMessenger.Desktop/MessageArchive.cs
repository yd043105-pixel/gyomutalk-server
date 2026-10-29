using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchoolMessenger.Desktop;

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
    public async Task Synchronize()
    {
        Directory.CreateDirectory(directory);var cursorPath=Path.Combine(directory,"cursor");
        var cursor=File.Exists(cursorPath)&&long.TryParse(File.ReadAllText(cursorPath),out var saved)?saved:0;
        for(var page=0;page<10;page++)
        {
            var changes=await api.Get<JsonElement>("api/messages/changes?after="+cursor);
            foreach(var entry in changes.GetProperty("events").EnumerateArray())
                if(entry.GetProperty("kind").GetString()=="MessageRecalled")Remove(entry.GetProperty("resourceId").GetString()!);
            cursor=changes.GetProperty("cursor").GetInt64();
            var temporary=cursorPath+".tmp";File.WriteAllText(temporary,cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));File.Move(temporary,cursorPath,true);
            if(!changes.GetProperty("hasMore").GetBoolean())break;
        }
    }
    internal void Verify(Detail value)
    {
        var bytes=File.ReadAllBytes(FilePath(value.Message.Id));
        var restored=JsonSerializer.Deserialize<Detail>(ProtectedData.Unprotect(bytes,null,DataProtectionScope.CurrentUser));
        if(JsonSerializer.Serialize(restored?.Message)!=JsonSerializer.Serialize(value.Message)||bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value.Message.Body))>=0)throw new InvalidOperationException("메시지 사본 암호화·복원 검증 실패");
        Remove(value.Message.Id);
        Save(value);
        if(File.Exists(FilePath(value.Message.Id)))throw new InvalidOperationException("회수된 메시지 사본이 다시 저장되었습니다.");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SchoolMessenger.Shared;

// What the administrator program tells phones and teacher PCs through the server: when the Android app checks for
// news once a day, and where the server has moved. It lives beside the server data so the server needs no restart.
public static class SchoolAppSettings
{
    public sealed record Moving(string Address,string Fingerprint,long Until);
    public sealed record Settings(string? CheckTime=null,Moving? Moving=null);
    public const string FileName="school-app.json",DefaultCheckTime="18:00";
    public const long MoveNoticeMilliseconds=7*86_400_000L;
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    // Servers check this on every API request, so the parsed file is kept until it changes on disk.
    static (string Path,DateTime Written,Settings Value)? cached;
    public static Settings Read(string root)
    {
        try
        {
            var path=Path.Combine(root,FileName);var file=new FileInfo(path);if(!file.Exists)return new();
            if(cached is{} hit&&hit.Path==path&&hit.Written==file.LastWriteTimeUtc)return hit.Value;
            var value=JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),Json)??new();cached=(path,file.LastWriteTimeUtc,value);return value;
        }
        catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException){return new();}
    }
    public static void Write(string root,Settings settings)
    {
        Directory.CreateDirectory(root);var path=Path.Combine(root,FileName);var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(settings,Json));File.Move(temp,path,true);
    }
    public static bool ValidTime(string? value)=>TimeOnly.TryParseExact(value,"HH:mm",out _);
    public static string CheckTime(Settings settings)=>ValidTime(settings.CheckTime)?settings.CheckTime!:DefaultCheckTime;
    // The certificate fingerprint is empty when the new server has a publicly trusted certificate (a school domain).
    public static bool ValidMove(string? address,string? fingerprint)
        =>Uri.TryCreate(address,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.AbsolutePath=="/"&&uri.Query.Length==0&&uri.UserInfo.Length==0
            &&(fingerprint is null or ""||fingerprint is{Length:64}&&fingerprint.All(Uri.IsHexDigit));
    public static Moving? ActiveMove(Settings settings,long now)=>settings.Moving is{} move&&move.Until>now&&ValidMove(move.Address,move.Fingerprint)?move:null;
}

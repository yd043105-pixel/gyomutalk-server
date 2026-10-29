using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace SchoolMessenger.Desktop;

// A school server made by the administrator program uses its own certificate. Trust it only after a person
// compares the connection code shown on the server PC; afterwards only that exact certificate is accepted.
internal static class CertificatePins
{
    static readonly object gate=new();
    static readonly Dictionary<string,string> pending=new();
    static string? folder;
    static string File=>Path.Combine(folder??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"YeoyangMessenger"),"certificate-pins.json");
    public static void UseFolder(string directory)=>folder=directory;
    static string Key(Uri address)=>address.Scheme+"://"+address.Authority.ToLowerInvariant();
    public static string Fingerprint(X509Certificate certificate)=>Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    // 64 bits of the SHA-256 fingerprint, grouped for reading aloud.
    public static string Code(string fingerprint)=>string.Join("-",Enumerable.Range(0,4).Select(i=>fingerprint.Substring(i*4,4)));
    static Dictionary<string,string> Load()
    {
        try{return System.IO.File.Exists(File)?JsonSerializer.Deserialize<Dictionary<string,string>>(System.IO.File.ReadAllText(File))??new():new();}
        catch(Exception e)when(e is IOException or JsonException){return new();}
    }
    public static bool Validate(Uri address,X509Certificate? certificate,SslPolicyErrors errors)
    {
        if(errors==SslPolicyErrors.None)return true;
        return certificate is not null&&ValidateFingerprint(address,Fingerprint(certificate));
    }
    public static bool ValidateFingerprint(Uri address,string fingerprint)
    {
        lock(gate)
        {
            if(Load().TryGetValue(Key(address),out var pinned))return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pinned),Convert.FromHexString(fingerprint));
            pending[Key(address)]=fingerprint;return false;
        }
    }
    public static string? Pending(string address)
    {
        if(!Uri.TryCreate(address.TrimEnd('/')+"/",UriKind.Absolute,out var uri))return null;
        lock(gate){if(Load().ContainsKey(Key(uri)))return null;return pending.TryGetValue(Key(uri),out var value)?value:null;}
    }
    public static void Trust(string address,string fingerprint)
    {
        var uri=new Uri(address.TrimEnd('/')+"/");
        lock(gate)
        {
            var pins=Load();pins[Key(uri)]=fingerprint;pending.Remove(Key(uri));
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);var temp=File+".tmp";System.IO.File.WriteAllText(temp,JsonSerializer.Serialize(pins));System.IO.File.Move(temp,File,true);
        }
    }
    public static HttpClientHandler Handler(Uri address,HttpClientHandler? handler=null)
    {
        handler??=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false};
        handler.ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>Validate(address,certificate,errors);return handler;
    }
    public static void Apply(HttpConnectionOptions options,Uri address)
    {
        options.HttpMessageHandlerFactory=inner=>{if(inner is HttpClientHandler handler)Handler(address,handler);return inner;};
        options.WebSocketConfiguration=socket=>socket.RemoteCertificateValidationCallback=(_,certificate,_,errors)=>Validate(address,certificate,errors);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SchoolMessenger.Desktop;

// "간편 설정": a school runs the server with only its name, an administrator account and the principal's
// approval document. Certificate, listener, LAN filter, discovery and the school review record are derived here.
internal sealed record SimpleSetup(string Service,string School,string AdminUsername,string Data,string Backup,string Approval,string ApprovalSha256,DateTimeOffset ReviewedAt,string SchoolId);

internal static class SimpleOperation
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    // 8443 is often taken by Korean security/certificate agents on school PCs, so use a less common port.
    public static int Port(string service)=>service=="office"?18443:18444;
    public static void EnsurePortFree(int port)
    {
        try{using var probe=new TcpListener(IPAddress.Any,port);probe.Start();probe.Stop();}
        catch(SocketException)
        {
            var owner=System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e=>e.Port==port);
            throw new InvalidOperationException("포트 "+port+"을(를) 다른 프로그램이 사용 중입니다. 다른 서버 프로그램이나 보안 프로그램을 확인하거나, 이미 실행 중인 교무톡 서버가 있는지 확인하세요."+(owner?"":" (Windows 예약 포트일 수 있습니다.)"));
        }
    }
    // Verification binds to loopback so a developer PC does not get a firewall prompt.
    internal static string Bind="0.0.0.0";
    public static string Listener(string service)=>"https://"+Bind+":"+Port(service);
    public static (IPAddress Address,int Prefix)[] LocalNetworks()=>NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses)
        .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&!a.Address.ToString().StartsWith("169.254.",StringComparison.Ordinal))
        .Select(a=>(a.Address,a.PrefixLength)).Distinct().ToArray();
    public static string AllowedNetworks()=>string.Join(",",LocalNetworks().Select(n=>new IPNetwork(Mask(n.Address,n.Prefix),n.Prefix).ToString()).Append("127.0.0.1/32").Distinct());
    static IPAddress Mask(IPAddress address,int prefix)
    {
        var bytes=address.GetAddressBytes();for(var i=0;i<4;i++){var bits=Math.Clamp(prefix-i*8,0,8);bytes[i]&=(byte)(0xFF<<(8-bits));}return new IPAddress(bytes);
    }
    public static string[] Addresses(string service)=>LocalNetworks().Select(n=>"https://"+n.Address+":"+Port(service)).DefaultIfEmpty("https://localhost:"+Port(service)).ToArray();
    public static string DefaultData(string directory,string service)=>Path.Combine(directory,"school",service);
    public static string DefaultBackup(string data,string service)
    {
        // Another fixed drive survives a failed system disk; fall back to a local folder and flag it in the checklist.
        var system=Path.GetPathRoot(data);
        var other=DriveInfo.GetDrives().Where(d=>d.IsReady&&d.DriveType==DriveType.Fixed&&!string.Equals(d.Name,system,StringComparison.OrdinalIgnoreCase)).OrderByDescending(d=>d.AvailableFreeSpace).FirstOrDefault();
        return other is null?Path.Combine(Path.GetDirectoryName(data.TrimEnd('\\'))!,"backups",service):Path.Combine(other.Name,"GyomutalkBackup",service);
    }
    public static bool SameDrive(string data,string backup)=>string.Equals(Path.GetPathRoot(Path.GetFullPath(data)),Path.GetPathRoot(Path.GetFullPath(backup)),StringComparison.OrdinalIgnoreCase);
    public static string NewSchoolId()=>"school-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    public static (string Path,string Sha256) KeepApproval(string directory,string service,string source)
    {
        var info=new FileInfo(source);
        if(!info.Exists)throw new InvalidOperationException("승인 문서 파일을 찾을 수 없습니다.");
        if(info.Length is 0 or >8*1024*1024)throw new InvalidOperationException("승인 문서는 8MB 이하의 파일이어야 합니다.");
        // The review record forbids linked or synced paths, so keep a local copy next to the server settings.
        var folder=Path.Combine(directory,"approval");Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,service+"-approval"+info.Extension.ToLowerInvariant());File.Copy(source,target,true);
        return (target,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))));
    }
    public static string WriteReview(string directory,SimpleSetup s)
    {
        var evidence=new{path=s.Approval,sha256=s.ApprovalSha256};
        var review=new
        {
            schema=1,status="approved",school=s.School,schoolId=s.SchoolId,service=s.Service,basis="school-education-41-7",institutionType="education-site",
            reviewAuthority="학교장",reviewedAt=s.ReviewedAt,expiresAt=s.ReviewedAt.AddYears(1),listenUrls=new[]{Listener(s.Service)},
            storageCountries=new[]{"KR"},managementCountries=new[]{"KR"},
            documents=new Dictionary<string,object>{["decision"]=evidence,["security"]=evidence,["education-applicability"]=evidence},
            externalServices=Array.Empty<object>()
        };
        var path=Path.Combine(directory,"review-"+s.Service+".json");var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(review,Json));File.Move(temp,path,true);return path;
    }
    public static (string Thumbprint,string Fingerprint) Certificate(string directory,string service)
    {
        var record=Path.Combine(directory,"certificate-"+service+".txt");
        using var store=new X509Store(StoreName.My,StoreLocation.CurrentUser);store.Open(OpenFlags.ReadWrite);
        if(File.Exists(record)&&store.Certificates.Find(X509FindType.FindByThumbprint,File.ReadAllText(record).Trim(),false).FirstOrDefault(c=>c.HasPrivateKey&&c.NotAfter>DateTime.Now.AddDays(30)) is{} existing)
            return (existing.Thumbprint,CertificatePins.Fingerprint(existing));
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request=new CertificateRequest("CN=Gyomutalk School Server ("+service+")",key,HashAlgorithmName.SHA256);
        var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");names.AddDnsName(Environment.MachineName);names.AddIpAddress(IPAddress.Loopback);
        foreach(var network in LocalNetworks())names.AddIpAddress(network.Address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")],false));
        // Teachers pin this certificate, so a long life avoids re-confirming codes; replacing it changes the code.
        using var created=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(10));
        var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var stored=X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx,password),password,X509KeyStorageFlags.PersistKeySet|X509KeyStorageFlags.UserKeySet);
        store.Add(stored);File.WriteAllText(record,stored.Thumbprint);
        return (stored.Thumbprint,CertificatePins.Fingerprint(stored));
    }
    public static void EnableDiscovery(string data,string service)
    {
        // Keeps a choice the administrator already made in the discovery settings screen.
        var file=Path.Combine(data,"discovery.json");if(File.Exists(file)||service!="office")return;
        Directory.CreateDirectory(data);File.WriteAllText(file,JsonSerializer.Serialize(new{enabled=true,address=Addresses(service)[0]+"/"},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

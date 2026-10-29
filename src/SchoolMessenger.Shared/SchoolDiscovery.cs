using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SchoolMessenger.Shared;

// Discovery is an untrusted hint. TLS and the user's first-server confirmation are still required.
public static class SchoolDiscovery
{
    public const int Port=45983;
    public const string Product="Yeoyang.Office.Discovery.1";
    public record Packet(string Product,string Nonce,string? Address=null);
    public record Candidate(string Address,string SchoolName,IPAddress Source);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static bool Address(string? text,out Uri? address)
    {
        address=null;
        if(text is not{Length:>0 and <=240}||!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/")return false;
        address=uri;return true;
    }
    public static byte[] Encode(Packet packet)=>JsonSerializer.SerializeToUtf8Bytes(packet,Json);
    public static Packet? Decode(byte[] bytes)
    {
        if(bytes.Length>512)return null;
        try{var p=JsonSerializer.Deserialize<Packet>(bytes,Json);return p?.Product==Product&&p.Nonce is{Length:32}&&p.Nonce.All(Uri.IsHexDigit)?p:null;}
        catch(JsonException){return null;}
    }
    static readonly object networkGate=new();
    static UnicastIPAddressInformation[] interfaces=[];
    static long networkSample=-30_001;
    public static IEnumerable<UnicastIPAddressInformation> Interfaces()
    {
        lock(networkGate)
        {
            if(Environment.TickCount64-networkSample>=30_000)
            {
                interfaces=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
                    .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&a.IPv4Mask is not null).Take(16).ToArray();
                networkSample=Environment.TickCount64;
            }
            return interfaces;
        }
    }
    public static bool Local(IPAddress address)=>IPAddress.IsLoopback(address)||Interfaces().Any(a=>SameSubnet(a,address));
    static bool SameSubnet(UnicastIPAddressInformation a,IPAddress other)
    {
        if(other.AddressFamily!=AddressFamily.InterNetwork)return false;
        var local=a.Address.GetAddressBytes();var remote=other.GetAddressBytes();var mask=a.IPv4Mask.GetAddressBytes();
        return Enumerable.Range(0,4).All(i=>(local[i]&mask[i])==(remote[i]&mask[i]));
    }
    public static IPEndPoint[] Targets(int port=Port)=>Interfaces().Select(a=>
    {
        var ip=a.Address.GetAddressBytes();var mask=a.IPv4Mask.GetAddressBytes();
        return new IPEndPoint(new IPAddress(Enumerable.Range(0,4).Select(i=>(byte)(ip[i]|~mask[i])).ToArray()),port);
    }).Append(new IPEndPoint(IPAddress.Loopback,port)).DistinctBy(e=>e.Address).ToArray();
    public static async Task<Candidate[]> Search(CancellationToken cancellation=default,IPEndPoint[]? targets=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var udp=new UdpClient(new IPEndPoint(IPAddress.Any,0)){EnableBroadcast=true};
        var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));var query=Encode(new(Product,nonce));
        foreach(var target in targets??Targets())try{await udp.SendAsync(query,target,deadline.Token);}catch(SocketException){}
        var found=new Dictionary<string,(Uri Address,IPAddress Source)>();
        try
        {
            while(found.Count<8)
            {
                var reply=await udp.ReceiveAsync(deadline.Token);var packet=Decode(reply.Buffer);
                if(packet?.Nonce!=nonce||!Local(reply.RemoteEndPoint.Address)||!Address(packet.Address,out var address))continue;
                found.TryAdd(address!.AbsoluteUri,(address,reply.RemoteEndPoint.Address));
            }
        }
        catch(OperationCanceledException)when(!cancellation.IsCancellationRequested){}
        var results=await Task.WhenAll(found.Values.Select(c=>Verify(c.Address,c.Source,cancellation)));
        return results.OfType<Candidate>().OrderBy(c=>c.Address).ToArray();
    }
    static async Task<Candidate?> Verify(Uri address,IPAddress source,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            // A peer cannot redirect discovery probes to an unrelated Internet or internal host.
            var addresses=await Dns.GetHostAddressesAsync(address.DnsSafeHost,deadline.Token);
            if(!addresses.Any(a=>a.Equals(source)))return null;
            using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,ConnectCallback=async(context,token)=>
            {
                var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(source,context.DnsEndPoint.Port,token);return new NetworkStream(socket,true);}catch{socket.Dispose();throw;}
            }};
            using var http=new HttpClient(handler){MaxResponseContentBufferSize=2048};
            using var response=await http.GetAsync(new Uri(address,"server-info"),deadline.Token);
            if(!response.IsSuccessStatusCode)return null;
            var data=JsonSerializer.Deserialize<Identity>(await response.Content.ReadAsStringAsync(deadline.Token),Json);
            return data?.Product==Product&&data.SchoolName is{Length:>0 and <=80}&&!data.SchoolName.Any(char.IsControl)?new(address.AbsoluteUri,data.SchoolName,source):null;
        }
        catch(Exception e)when(e is HttpRequestException or OperationCanceledException or SocketException or JsonException){return null;}
    }
    public record Identity(string Product,string SchoolName);
}

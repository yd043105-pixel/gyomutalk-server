using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SchoolMessenger.Shared;

var checks=new List<string>();
void Require(bool value,string text){if(!value)throw new Exception(text);}
var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
Require(SchoolDiscovery.Decode(SchoolDiscovery.Encode(new(SchoolDiscovery.Product,nonce)))?.Nonce==nonce,"valid query rejected");
foreach(var bytes in new[]{new byte[513],"null"u8.ToArray(),"{}"u8.ToArray(),SchoolDiscovery.Encode(new("other",nonce)),SchoolDiscovery.Encode(new(SchoolDiscovery.Product,"no"))})Require(SchoolDiscovery.Decode(bytes) is null,"malformed discovery accepted");
foreach(var address in new[]{"http://10.0.0.1/","https://user:pass@10.0.0.1/","https://10.0.0.1/private","https://10.0.0.1/?secret=x"})Require(!SchoolDiscovery.Address(address,out _),"unsafe address accepted");
Require(SchoolDiscovery.Address("https://school.example:7443/",out _),"valid address rejected");checks.Add("Packet and address boundaries reject malformed, oversized and credential-bearing hints");

async Task<(SchoolDiscovery.Candidate[] Found,int Connections)> SearchFake(string scheme,bool wrongNonce)
{
    using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
    var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();var port=((IPEndPoint)tcp.LocalEndpoint).Port;int connections=0;
    using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(9));
    var reply=Task.Run(async()=>
    {
        var packet=await udp.ReceiveAsync(stop.Token);var query=SchoolDiscovery.Decode(packet.Buffer)!;
        await udp.SendAsync(SchoolDiscovery.Encode(new(SchoolDiscovery.Product,wrongNonce?new string('F',32):query.Nonce,$"{scheme}://localhost:{port}/")),packet.RemoteEndPoint,stop.Token);
    });
    var accept=Task.Run(async()=>
    {
        try
        {
            using var socket=await tcp.AcceptTcpClientAsync(stop.Token);Interlocked.Increment(ref connections);
            using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");request.CertificateExtensions.Add(names.Build());
            using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
            using var tls=new SslStream(socket.GetStream());await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},stop.Token);
        }
        catch(Exception e)when(e is OperationCanceledException or System.Security.Authentication.AuthenticationException or IOException or SocketException){}
    });
    try
    {
        var found=await SchoolDiscovery.Search(targets:[(IPEndPoint)udp.Client.LocalEndPoint!]);await reply;
        return(found,connections);
    }
    finally{stop.Cancel();tcp.Stop();await accept;}
}
var mismatch=await SearchFake("https",true);Require(mismatch.Found.Length==0&&mismatch.Connections==0,"mismatched nonce caused a connection");checks.Add("An unrelated nonce never causes an HTTPS connection");
var plain=await SearchFake("http",false);Require(plain.Found.Length==0&&plain.Connections==0,"plaintext discovery caused a connection");checks.Add("Plain HTTP hints are ignored without contacting their advertised service");
var selfSigned=await SearchFake("https",false);Require(selfSigned.Found.Length==0&&selfSigned.Connections==1,"untrusted certificate was accepted or TLS verification never ran");checks.Add("Untrusted TLS certificates are rejected with normal certificate verification enabled");
Console.WriteLine(JsonSerializer.Serialize(new{checks,loopbackOnly=true,trustStoreChanged=false}));

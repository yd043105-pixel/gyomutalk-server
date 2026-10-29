using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SchoolMessenger.Shared;

var checks=new List<string>();
void Require(bool condition,string message){if(!condition)throw new Exception(message);}
void Pass(string text){checks.Add(text);Console.WriteLine("PASS "+text);}
SchoolDirectory.Entry A=new("SCHOOL-A","같은고등학교","전남 여수","https://office-a.example/","https://news-a.example/");
SchoolDirectory.Entry B=new("SCHOOL-B","같은고등학교","서울 중구","https://office-b.example/","");
byte[] Catalog(params SchoolDirectory.Entry[] entries)=>JsonSerializer.SerializeToUtf8Bytes(new{schema=1,schools=entries.Select(e=>new{id=e.Id,name=e.Name,region=e.Region,officeAddress=e.OfficeAddress,announcementsAddress=e.AnnouncementsAddress})});
void Rejected(byte[] value){try{SchoolDirectory.Parse(value);}catch(InvalidOperationException){return;}throw new Exception("Unsafe directory was accepted");}
var schools=SchoolDirectory.Parse(Catalog(A,B)).Schools;
Require(SchoolDirectory.Search(schools,"같은고","office").Length==2,"same-name schools disappeared");
Require(SchoolDirectory.Search(schools,"여수","office").Single().Id==A.Id,"region filter failed");
Require(SchoolDirectory.Search(schools,"","announcements").Single().Id==A.Id,"role filter failed");
Pass("Same-name schools remain distinct by school ID and region; each app sees its supported server role");
foreach(var address in new[]{"http://office.example/","https://u:p@office.example/","https://office.example/api/","https://office.example/?token=secret","https://office.example/#part"})Rejected(Catalog(A with{OfficeAddress=address}));
Rejected(Catalog(A,A));Rejected(Catalog(A,B with{OfficeAddress=A.OfficeAddress}));Rejected(Catalog(A with{AnnouncementsAddress=A.OfficeAddress}));Rejected(Catalog(A with{Name="bad\nname"}));Rejected(Catalog(A with{Id="bad/id"}));
Pass("Invalid addresses, credentials, paths, duplicate IDs, shared school endpoints and control characters are rejected");
foreach(var text in new[]{"null","{}","{\"schema\":1,\"schema\":1,\"schools\":[]}","{\"schema\":2,\"schools\":[]}","{\"schema\":1,\"schools\":[{}]}","{\"schema\":1,\"schools\":\"bad\"}"})Rejected(Encoding.UTF8.GetBytes(text));
Rejected(new byte[1024*1024+1]);
Pass("Malformed, duplicate-field, unsupported-schema and oversized directories fail closed");
Require(SchoolDirectory.Search(Enumerable.Range(0,110).Select(i=>A with{Id="SCHOOL-"+i,Name="학교"+i}),"","office").Length==100,"search not bounded");
try{SchoolDirectory.Search(schools,new string('x',81),"office");throw new Exception("query unbounded");}catch(InvalidOperationException){}
var included=SchoolDirectory.Bundled();
Require(included.SourceUrl.Length==0||Uri.TryCreate(included.SourceUrl,UriKind.Absolute,out var publisher)&&publisher.Scheme=="https","school-owned HTTPS or offline directory rejected");
Pass("Bundled directory is embedded, queries are bounded, and at most 100 matches are displayed");
var offline=await SchoolDirectory.Refresh(new("",[A]),handler:new Fake(_=>throw new Exception("Offline school list must not contact a central publisher")));
Require(offline.Schools.Single().Id==A.Id,"school-owned offline list changed");
Pass("A school's bundled offline directory works without contacting or requiring approval from a central publisher");
byte[] Identity(string id,string service)=>JsonSerializer.SerializeToUtf8Bytes(new{product="Yeoyang.Office.Discovery.1",schoolName=A.Name,directoryProduct=SchoolDirectory.Product,schoolId=id,service});
var calls=new List<string>();
Fake Handler(byte[] body,HttpStatusCode status=HttpStatusCode.OK)=>new(request=>{Require(request.Method==HttpMethod.Get,"credentials could be sent during selection");Require(request.Headers.Authorization is null&&!request.Headers.Contains("Cookie"),"selection leaked credentials");calls.Add(request.RequestUri!.AbsoluteUri);return new(status){Content=new ByteArrayContent(body)};});
await SchoolDirectory.Verify(A,"office",handler:Handler(Identity(A.Id,"office")));
await SchoolDirectory.Verify(A,"announcements",handler:Handler(Identity(A.Id,"announcements")));
Require(calls.SequenceEqual(new[]{"https://office-a.example/server-info","https://news-a.example/server-info"}),"unexpected probe destinations");
Pass("School selection verifies only the chosen endpoint and sends neither cookies nor credentials");
foreach(var pair in new[]{(B.Id,"office"),(A.Id,"announcements")})
{
    var rejected=false;try{await SchoolDirectory.Verify(A,"office",handler:Handler(Identity(pair.Item1,pair.Item2)));}catch(InvalidOperationException){rejected=true;}Require(rejected,"wrong school or role accepted");
}
Pass("A reachable HTTPS endpoint with another school ID or server role is rejected before login");
foreach(var body in new[]{new byte[2049],Encoding.UTF8.GetBytes("{}")}){var rejected=false;try{await SchoolDirectory.Verify(A,"office",handler:Handler(body));}catch(Exception){rejected=true;}Require(rejected,"unsafe identity accepted");}
var redirected=false;try{await SchoolDirectory.Verify(A,"office",handler:Handler(Identity(A.Id,"office"),HttpStatusCode.TemporaryRedirect));}catch(HttpRequestException){redirected=true;}Require(redirected,"redirect accepted");
Pass("Redirects and oversized or incomplete school identity responses are rejected");
var bundled=new SchoolDirectory.Catalog("https://publisher.example/schools.json",[A]);
var remote=JsonSerializer.SerializeToUtf8Bytes(new{schema=1,sourceUrl="https://different-publisher.example/file",schools=Array.Empty<object>()});
var refreshed=await SchoolDirectory.Refresh(bundled,handler:Handler(remote));Require(refreshed.SourceUrl==bundled.SourceUrl,"remote origin replaced trusted publisher");
Pass("Downloaded school lists cannot change the application's trusted publisher origin");
var tcp=new TcpListener(IPAddress.Loopback,0);tcp.Start();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(6));
var serve=Task.Run(async()=>
{
    try{using var socket=await tcp.AcceptTcpClientAsync(stop.Token);using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var names=new SubjectAlternativeNameBuilder();names.AddDnsName("localhost");request.CertificateExtensions.Add(names.Build());using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));using var ssl=new SslStream(socket.GetStream());await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},stop.Token);}catch(Exception error)when(error is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException){}
});
var untrusted=false;try{await SchoolDirectory.Verify(A with{OfficeAddress="https://localhost:"+((IPEndPoint)tcp.LocalEndpoint).Port},"office",stop.Token);}catch(HttpRequestException){untrusted=true;}finally{stop.Cancel();tcp.Stop();await serve;}
Require(untrusted,"untrusted TLS certificate accepted");Pass("Normal TLS validation rejects a real untrusted certificate without changing the trust store");
var slow=new Fake(_=>new(HttpStatusCode.OK){Content=new StreamContent(new WaitingStream())});
var timer=System.Diagnostics.Stopwatch.StartNew();var expired=false;
try{await SchoolDirectory.Verify(A,"office",handler:slow);}catch(OperationCanceledException){expired=true;}
Require(expired&&timer.Elapsed<TimeSpan.FromSeconds(9),"response body can wait forever after headers");
Pass("A stalled response body is cancelled by the six-second deadline after headers have arrived");
var registration=SchoolRegistration.Create(A.Id,A.Name,A.Region,"061-000-0000",A.OfficeAddress,A.AnnouncementsAddress);
using(var request=JsonDocument.Parse(registration.ToJson())){Require(request.RootElement.GetProperty("status").GetString()=="pending","request self-approved");Require(request.RootElement.GetProperty("contactPhone").GetString()=="061-000-0000","school verification contact missing");}
Require(registration.Mailto("request.json").StartsWith("mailto:yd043105@gmail.com?subject="),"recipient changed");Require(!registration.Mailto("request.json").Contains("061-000-0000"),"phone leaked in draft URI");
Pass("School requests remain pending; the mail draft uses the fixed developer address and excludes verification contact");
foreach(var phone in new[]{"010-0000-0000","061-000-0000\r\nBcc:other@example.invalid","not a phone"}){var rejected=false;try{SchoolRegistration.Create(A.Id,A.Name,A.Region,phone,A.OfficeAddress,A.AnnouncementsAddress);}catch(InvalidOperationException){rejected=true;}Require(rejected,"private or injected phone accepted");}
Pass("School requests reject personal mobile numbers and contact-field injection");
Console.WriteLine(checks.Count+" school directory checks passed.");

sealed class Fake(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(response(request));
}
sealed class WaitingStream:Stream
{
    public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(Timeout.Infinite,token);return 0;}
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}

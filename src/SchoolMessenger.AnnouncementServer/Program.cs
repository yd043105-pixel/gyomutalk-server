using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using SchoolMessenger.AnnouncementServer;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;
using static SchoolMessenger.AnnouncementServer.PortalSecurity;

var checkDeploymentReview=args.Contains("--deployment-review-check");
var builder = WebApplication.CreateBuilder(args.Where(a=>a!="--deployment-review-check").ToArray());
if(checkDeploymentReview){TunnelOrigin.Validate(builder.Configuration,builder.Environment.IsDevelopment());Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(DeploymentReview.Load(builder.Configuration,builder.Environment.IsDevelopment(),"announcements").Summary()));return;}
if(OperationsCommand.TryRun(builder.Configuration,"Portal","announcements.db"))return;
TunnelOrigin.Validate(builder.Configuration,builder.Environment.IsDevelopment());
if(BackendRecovery.TryRun(builder.Configuration,"Portal","announcements.db"))return;
var deploymentReview=DeploymentReview.Load(builder.Configuration,builder.Environment.IsDevelopment(),"announcements");
builder.Services.AddSingleton(deploymentReview);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 105L*1024*1024);
if (builder.Configuration["Portal:CertificateThumbprint"] is {Length:>0} thumbprint)
{
    var managed = builder.Configuration["Portal:CertificateStore"] == "CurrentUser";
    using var certificates = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My,managed?System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser:System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
    certificates.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
    var certificate = certificates.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,thumbprint,!managed).FirstOrDefault(c=>c.HasPrivateKey)
        ?? throw new InvalidOperationException("유효한 공지 서버 인증서와 개인키가 필요합니다.");
    builder.WebHost.ConfigureKestrel(o=>o.ConfigureHttpsDefaults(https=>https.ServerCertificate=certificate));
}
if (builder.Configuration["Portal:BridgeKey"]?.Length is not >= 32 && !builder.Configuration.GetSection("Portal:BridgeKeys").GetChildren().Any(k=>k.Value?.Length>=32)) throw new InvalidOperationException("Portal__BridgeKey 또는 Portal__BridgeKeys에 별도 32자 이상 서비스 키를 설정하세요.");
using var store = new PortalStore(builder.Configuration);
var resources=new ServerResources(store.Root,"Portal",store.Open,builder.Configuration,builder.Environment.IsDevelopment());
PortalGraduation.Initialize(store);
var registrationPolicy=new RegistrationPolicy(builder.Configuration["Registration:PolicyFile"],builder.Environment.IsDevelopment(),builder.Environment.ContentRootPath);
using(var policyDatabase=store.Open())RegistrationPolicy.EnsureSchema(policyDatabase);
var dataPath = builder.Services.AddDataProtection().SetApplicationName("YyAnnouncements").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(store.Root,"keys")));
if (OperatingSystem.IsWindows()) dataPath.ProtectKeysWithDpapi();
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(s=>new FeedbackMail(store.Open,"announcements",s.GetRequiredService<IDataProtectionProvider>(),builder.Configuration,(host,recipient)=>deploymentReview.Demand("error-mail",host,587,recipient)));
builder.Services.AddHostedService(s=>s.GetRequiredService<FeedbackMail>());
builder.Services.AddSingleton<PasswordHasher<Account>>(); builder.Services.Configure<PasswordHasherOptions>(o=>o.IterationCount=220_000);
builder.Services.AddHostedService<PortalMaintenance>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o=>o.MultipartBodyLengthLimit=105L*1024*1024);
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF-TOKEN";o.Cookie.Name="YyAnnouncements.Csrf";o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=CookieSecurePolicy.SameAsRequest;});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o=>
{
    o.Cookie.Name="YyAnnouncements.Session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan=TimeSpan.FromHours(8);o.SlidingExpiration=false;
    o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
    o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
    o.Events.OnValidatePrincipal=async c=>{var person=store.Person(c.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);using var sessionDb=store.Open();if(person is not {Active:true} || person.PasswordHash is null || PortalGraduation.Expired(store,person.Id) || person.Version.ToString()!=c.Principal!.FindFirstValue("version") || !DeviceSessions.Valid(sessionDb,person.Id,c.Principal!.FindFirstValue("version"),c.Principal!.FindFirstValue("sid")) || person.Role=="teacher"&&!VerifiedTeacher(store,person)){c.RejectPrincipal();await c.HttpContext.SignOutAsync();}};
});
builder.Services.AddAuthorization(o=>
{
    o.AddPolicy("Admin",p=>p.RequireRole("admin"));
    o.AddPolicy("Teacher",p=>p.RequireRole("teacher"));
});
builder.Services.AddRateLimiter(o=>
{
    o.RejectionStatusCode=429;
    o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(c=>RateLimitPartition.GetFixedWindowLimiter(c.User.Identity?.IsAuthenticated==true?"user:"+Id(c):"ip:"+c.Connection.RemoteIpAddress,_=>new FixedWindowRateLimiterOptions{PermitLimit=600,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("login",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=Math.Clamp(builder.Configuration.GetValue("Portal:LoginLimitPerMinute",120),20,300),Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    o.AddPolicy("upload",_=>RateLimitPartition.GetConcurrencyLimiter("uploads",_=>new ConcurrencyLimiterOptions{PermitLimit=4,QueueLimit=0}));
    o.AddPolicy("family-code",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=100,Window=TimeSpan.FromMinutes(15),QueueLimit=0}));
    o.AddPolicy("counseling",c=>RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirstValue(ClaimTypes.NameIdentifier)??c.Request.Query["teacherId"].ToString()+":"+c.Connection.RemoteIpAddress,_=>new FixedWindowRateLimiterOptions{PermitLimit=120,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
var app=builder.Build();
var hasher=app.Services.GetRequiredService<PasswordHasher<Account>>();
if(store.Query("SELECT Id FROM People WHERE Role='admin'",r=>r.GetString(0)).Count==0)
{
    var password=builder.Configuration["Portal:AdminPassword"];if(!Password(password))throw new InvalidOperationException("첫 실행: Portal__AdminPassword에 12~128자 비밀번호를 지정하세요.");
    var username=builder.Configuration["Portal:AdminUsername"]??"admin";if(!PortalSecurity.Username(username))throw new InvalidOperationException("관리자 아이디는 영문·숫자·._- 3~32자입니다.");
    var administrator=new Account(Guid.NewGuid().ToString("N"),username,"학교 공지 관리자","admin",null,null,false,true,1,null,0,0);
    store.Execute("INSERT INTO People(Id,Username,Name,Role,PasswordHash) VALUES($id,'admin',$name,'admin',$hash)",("$id",administrator.Id),("$name",administrator.Name),("$hash",hasher.HashPassword(administrator,password!)));
}
File.WriteAllText(Path.Combine(store.Root,".initialized"),"initialized");
var hosts=(builder.Configuration["Portal:AllowedHosts"]??"localhost,127.0.0.1").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
if(!app.Environment.IsDevelopment() && hosts.All(h=>h is "localhost" or "127.0.0.1")) app.Logger.LogWarning("외부 공개 전 Portal__AllowedHosts에 학교 도메인을 설정하세요.");
app.UseExceptionHandler(error=>error.Run(async c=>
{
    var failure=c.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    c.Response.StatusCode=failure is BadHttpRequestException{StatusCode:413}?413:500;
    if(c.Response.StatusCode>=500)app.Logger.LogError("Request processing failed ({ErrorCode}, {TraceId})",failure?.GetType().Name??"Unknown",c.TraceIdentifier);
    else app.Logger.LogWarning("Request rejected ({ErrorCode}, {TraceId})",failure?.GetType().Name??"Unknown",c.TraceIdentifier);
    await c.Response.WriteAsJsonAsync(new{error=c.Response.StatusCode==413?"공지 업로드 요청이 너무 큽니다. 이미지는 한 장당 200KB까지입니다.":"서버 처리 실패. 같은 요청 번호로 다시 시도하거나 관리자에게 문의하세요."});
}));
app.Use(async(c,next)=>
{
    if(!hosts.Contains(c.Request.Host.Host,StringComparer.OrdinalIgnoreCase)){c.Response.StatusCode=400;return;}
    if(!c.Request.IsHttps && c.Connection.RemoteIpAddress is {} ip && !System.Net.IPAddress.IsLoopback(ip)){c.Response.StatusCode=400;return;}
    if(c.Request.Headers.TryGetValue("Origin",out var origins) && (origins.Count!=1 || origins[0]!=$"{c.Request.Scheme}://{c.Request.Host}")){c.Response.StatusCode=403;return;}
    if(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/bridge"))
    {
        c.Response.Headers.CacheControl="no-store";var limit=c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        var upload=c.Request.Path.Value!.EndsWith("/attachments",StringComparison.Ordinal);
        if(limit is{IsReadOnly:false})limit.MaxRequestBodySize=upload?240000:c.Request.Path=="/bridge/timetable"?8_388_608:2_097_152;
        if(upload&&c.Request.ContentLength>240000){await Error("공지 업로드 요청이 너무 큽니다. 이미지는 한 장당 200KB까지입니다.",413).ExecuteAsync(c);return;}
    }
    c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.Headers["X-Frame-Options"]="DENY";c.Response.Headers["Referrer-Policy"]="no-referrer";
    c.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    c.Response.Headers["Permissions-Policy"]="camera=(), microphone=(), geolocation=()";
    c.Response.Headers.CacheControl="no-store";
    if(c.Request.IsHttps)c.Response.Headers["Strict-Transport-Security"]="max-age=31536000";
    await next();
});
TunnelOrigin.Use(app);
app.UseDefaultFiles();app.UseStaticFiles();
if (app.Environment.IsDevelopment() && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot")) });
app.UseRouting();
BackendOperations.UseAudit(app,store.Open,"AnnouncementServer");app.UseAuthentication();app.UseRateLimiter();app.UseAuthorization();
app.Use(async(c,next)=>{try{PortalGraduation.ProcessDue(store);}catch(Exception error){app.Logger.LogError("Graduation erasure retry required ({ErrorCode})",error.GetType().Name);}await next();});
RecoveryGuard.Use(app,store.Open);
resources.Use(app);resources.Map(app);
app.Use(async(c,next)=>
{
    if(c.Request.Path.StartsWithSegments("/bridge")&&!await BridgeAuthentication.Verify(c,app.Configuration,(service,nonce,expires)=>
    {
        store.Execute("DELETE FROM BridgeNonces WHERE ExpiresAt<$now",("$now",Now));
        return store.Execute("INSERT OR IGNORE INTO BridgeNonces(Service,Nonce,ExpiresAt) VALUES($service,$nonce,$expires)",( "$service",service),("$nonce",nonce),("$expires",expires))==1;
    })){c.Response.StatusCode=401;return;}
    if(c.Request.Path.StartsWithSegments("/api")&&c.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
    {
        var token=c.Request.Headers["X-CSRF-TOKEN"];
        if(token.Count!=1||string.IsNullOrWhiteSpace(token.ToString())){await Error("요청 인증 만료. 다시 로그인하세요.").ExecuteAsync(c);return;}
        try{await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);}catch(AntiforgeryValidationException){await Error("요청 인증 만료. 다시 로그인하세요.").ExecuteAsync(c);return;}
    }
    if(c.User.Identity?.IsAuthenticated==true&&c.Request.Path.StartsWithSegments("/api")&&store.Person(Id(c)) is{Role:"parent"} parent&&!store.Regular(parent)
        &&c.Request.Path!="/api/session"&&c.Request.Path!="/api/login"&&c.Request.Path!="/api/register"&&c.Request.Path!="/api/registration/classes"&&c.Request.Path!="/api/registration/policy"&&c.Request.Path!="/api/logout"&&c.Request.Path!="/api/feedback"&&!c.Request.Path.StartsWithSegments("/api/sessions")&&c.Request.Path!="/api/children"&&!c.Request.Path.StartsWithSegments("/api/settings"))
    {await Error("임시회원입니다. 설정에서 자녀 고유번호를 입력하면 학교 소식과 시간표를 볼 수 있습니다.",403).ExecuteAsync(c);return;}
    await next();
});
app.MapGet("/health",()=>Results.Ok(new{status="ok",service="announcements"}));
app.MapGet("/server-info",()=>new{product="Yeoyang.Office.Discovery.1",schoolName=deploymentReview.SchoolName??app.Configuration["Portal:Name"]??"학교",directoryProduct="SchoolMessenger.School.1",schoolId=deploymentReview.SchoolId??app.Configuration["Operations:SchoolId"]??"",service="announcements"});
OperationalHealth.Map(app,store.Open,store.Root,"Portal");
app.MapGet("/api/session",(HttpContext c,IAntiforgery csrf)=>new PortalSession(app.Configuration["Portal:Name"]??"우리 학교",csrf.GetAndStoreTokens(c).RequestToken!,c.User.Identity?.IsAuthenticated==true?store.Public(store.Person(Id(c))!):null));
app.MapPost("/api/login",async Task<IResult>(Login request,HttpContext c)=>
{
    if(!Username(request.Username)||request.Password?.Length is not (>=1 and <=128))return Error("계정과 비밀번호를 확인하세요.",401);
    var person=store.Username(request.Username!);
    if(person is not{Active:true,PasswordHash:not null}||person.LockedUntil>Now||PortalGraduation.Expired(store,person.Id))return Error("계정과 비밀번호를 확인하세요.",401);
    var verified=hasher.VerifyHashedPassword(person,person.PasswordHash,request.Password!);
    if(verified==PasswordVerificationResult.Failed){store.Execute("UPDATE People SET FailedAttempts=FailedAttempts+1,LockedUntil=CASE WHEN FailedAttempts+1>=5 THEN $until ELSE 0 END WHERE Id=$id",("$until",Now+900_000),("$id",person.Id));return Error("계정과 비밀번호를 확인하세요.",401);}
    if(person.Role=="teacher"&&!VerifiedTeacher(store,person))return Error("학교 교직원 연결 확인을 기다리거나 관리자에게 문의하세요.",403);
    if(verified==PasswordVerificationResult.SuccessRehashNeeded)store.Execute("UPDATE People SET PasswordHash=$hash WHERE Id=$id AND PasswordHash=$old",("$hash",hasher.HashPassword(person,request.Password!)),("$id",person.Id),("$old",person.PasswordHash));
    store.Execute("UPDATE People SET FailedAttempts=0,LockedUntil=0 WHERE Id=$id",("$id",person.Id));
    var expires=DateTimeOffset.UtcNow.Add(request.RememberLogin?TimeSpan.FromDays(30):TimeSpan.FromHours(8));
    using var sessionDb=store.Open();var sid=DeviceSessions.Create(sessionDb,person.Id,person.Version.ToString(),expires);
    await c.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,person.Id),new Claim(ClaimTypes.Name,person.Name),new Claim(ClaimTypes.Role,person.Role),new Claim("version",person.Version.ToString()),new Claim("sid",sid)},CookieAuthenticationDefaults.AuthenticationScheme)),new AuthenticationProperties{IsPersistent=request.RememberLogin,ExpiresUtc=expires});
    return Results.Ok(store.Public(person));
}).RequireRateLimiting("login");
app.MapGet("/api/registration/policy",()=>registrationPolicy.Current).RequireRateLimiting("login");
app.MapPost("/api/register",(InviteRegistration request)=>string.IsNullOrEmpty(request.Code)?PortalEnrollment.Register(store,hasher,request,registrationPolicy):PortalAdministration.Register(store,hasher,request,registrationPolicy)).RequireRateLimiting("login");
var api=app.MapGroup("/api").RequireAuthorization();
api.MapPost("/logout",async(HttpContext c)=>{store.Execute("UPDATE People SET Version=Version+1 WHERE Id=$id",("$id",Id(c)));await c.SignOutAsync();return Results.Ok();});
var admin=app.MapGroup("/api/admin").RequireAuthorization("Admin");
var protection=app.Services.GetRequiredService<IDataProtectionProvider>();
PortalAdministration.Map(admin,store,protection);
PortalSections.Initialize(store);
PortalSections.Map(api,admin,app.MapGroup("/bridge"),store);
PortalElectives.Initialize(store);
PortalElectives.Map(api,app.MapGroup("/bridge"),store);
PortalEnrollment.Map(app,api,admin,store,protection);
PortalGuardianInvites.Map(app,api,store);
PortalNotices.Map(api,app.MapGroup("/bridge"),store,app.Configuration,app.Environment);
PortalTimetables.Map(api,app.MapGroup("/bridge"),store);
PortalCounseling.Map(api,app.MapGroup("/bridge"),store);
PortalGraduation.Map(admin,app.MapGroup("/bridge"),store);
try{PortalGraduation.Run(store);}catch(Exception error){app.Logger.LogError("Graduation erasure retry required ({ErrorCode})",error.GetType().Name);}
app.Services.GetServices<IHostedService>().OfType<PortalMaintenance>().Single().Cleanup();
BackendOperations.Map(app,store.Open);
DeploymentReview.Map(app,deploymentReview);
DeviceSessions.Map(app,store.Open);
FeedbackReports.Map(app,store.Open,protection,"announcements");
ManagedServerLifetime.Attach(app);
app.Run();

record Login(string? Username,string? Password,bool RememberLogin=false);

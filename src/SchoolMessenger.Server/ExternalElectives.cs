using System.Net.Http.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

public sealed partial class ExternalAnnouncements
{
    bool ElectiveManager(string id)=>store.GetUser(id) is{Active:true} user&&(user.IsAdmin||store.Query("SELECT 1 FROM ElectiveManagers WHERE UserId=$id",r=>r.GetInt32(0),("$id",id)).Count>0);
    static void MapElectives(WebApplication app,ExternalAnnouncements service)
    {
        service.store.Execute("CREATE TABLE IF NOT EXISTS ElectiveManagers(UserId TEXT PRIMARY KEY REFERENCES Users(Id))");
        var api=app.MapGroup("/api/elective-management").RequireAuthorization();
        api.MapGet("/access",(HttpContext c)=>new{canManage=service.ElectiveManager(Owner(c)),isAdmin=service.store.GetUser(Owner(c))!.IsAdmin});
        api.MapGet("/setup",async Task<IResult>(HttpContext c)=>await service.ElectiveProxy(c,HttpMethod.Get,"/setup"));
        api.MapGet("",async Task<IResult>(HttpContext c)=>await service.ElectiveProxy(c,HttpMethod.Get,""));
        api.MapGet("/{id}",(string id,HttpContext c)=>service.ElectiveProxy(c,HttpMethod.Get,"/"+Uri.EscapeDataString(id)));
        api.MapPost("",(ElectiveRoundRequest request,HttpContext c)=>service.ElectiveProxy(c,HttpMethod.Post,"",request));
        api.MapPost("/{id}",(string id,ElectiveRoundRequest request,HttpContext c)=>service.ElectiveProxy(c,HttpMethod.Post,"/"+Uri.EscapeDataString(id),request));
        api.MapPost("/{id}/state",(string id,ElectiveTransition request,HttpContext c)=>service.ElectiveProxy(c,HttpMethod.Post,"/"+Uri.EscapeDataString(id)+"/state",request));
        app.MapGet("/api/admin/elective-managers",()=>service.store.Users().Where(u=>u.Active).Select(u=>new{u.Id,u.Name,u.Department,enabled=service.ElectiveManager(u.Id),u.IsAdmin})).RequireAuthorization("Admin");
        app.MapPost("/api/admin/elective-managers",IResult(ElectiveManagerChange request)=>
        {
            if(service.store.GetUser(request.UserId) is not{Active:true,IsAdmin:false})return Error("활성 일반 교직원을 선택하세요. 관리자는 기본 업무 권한이 있습니다.");
            service.store.Execute(request.Enabled?"INSERT OR IGNORE INTO ElectiveManagers(UserId) VALUES($id)":"DELETE FROM ElectiveManagers WHERE UserId=$id",("$id",request.UserId));return Results.Ok();
        }).RequireAuthorization("Admin");
    }
    async Task<IResult> ElectiveProxy(HttpContext context,HttpMethod method,string route,object? body=null)
    {
        var actor=Owner(context);if(!ElectiveManager(actor))return Error("수강신청 업무 담당자 권한이 필요합니다.",403);
        if(http is null)return Error("학교 소식 서버 연결을 먼저 설정하세요.",503);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // The actor comes from the authenticated office session, never from a client-supplied field.
            using var request=new HttpRequestMessage(method,"bridge/electives"+route+"?actor="+Uri.EscapeDataString(actor));
            if(method!=HttpMethod.Get)request.Content=JsonContent.Create(body,options:Json);
            using var response=await http.SendAsync(request,timeout.Token);var text=await response.Content.ReadAsStringAsync(timeout.Token);
            return Results.Content(text,"application/json",statusCode:(int)response.StatusCode);
        }
        catch(Exception error)when(error is HttpRequestException or OperationCanceledException)
        {return Error("수강신청 서버 응답을 확인하지 못했습니다. 현황을 새로고침하고 같은 요청으로 다시 시도하세요.",503);}
    }
}

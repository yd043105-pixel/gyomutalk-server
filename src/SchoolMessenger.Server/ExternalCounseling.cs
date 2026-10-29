using System.Net.Http.Json;
using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

public sealed partial class ExternalAnnouncements
{
    static void MapCounseling(WebApplication app,ExternalAnnouncements service)
    {
        var api=app.MapGroup("/api/counseling").RequireAuthorization();
        api.MapGet("",(string? date,HttpContext c)=>service.Counseling(c,HttpMethod.Get,"",date));
        api.MapPost("/slots",(CounselingOpen request,HttpContext c)=>service.Counseling(c,HttpMethod.Post,"/slots",body:request));
        api.MapPost("/slots/{id}/close",(string id,HttpContext c)=>service.Counseling(c,HttpMethod.Post,"/slots/"+Uri.EscapeDataString(id)+"/close"));
        api.MapPost("/bookings/{id}/cancel",(string id,HttpContext c)=>service.Counseling(c,HttpMethod.Post,"/bookings/"+Uri.EscapeDataString(id)+"/cancel"));
    }
    async Task<IResult> Counseling(HttpContext context,HttpMethod method,string route,string? date=null,object? body=null)
    {
        if(http is null||synced<Now-60_000)return Error("학교 소식 서버 연결을 확인하고 다시 시도하세요.",503);
        var user=store.GetUser(Owner(context));var teacher=Publisher(Owner(context));
        if(user is not {Active:true}||teacher is null||StaffAccounts.Pending(store,user.Id))return Error("관리자에게 담당 학급과 학교 소식 교사 계정 연결을 요청하세요.",403);
        // No caller-supplied teacher identity, cookies, or authorization headers cross the bridge.
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request=new HttpRequestMessage(method,"bridge/counseling"+route+"?teacherId="+teacher.Id+(date is null?"":"&date="+Uri.EscapeDataString(date)));
            if(method!=HttpMethod.Get)request.Content=JsonContent.Create(body??new{},options:Json);
            using var response=await http.SendAsync(request,cancellation.Token);
            var text=await response.Content.ReadAsStringAsync(cancellation.Token);
            return Results.Content(text,"application/json",statusCode:(int)response.StatusCode);
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException)
        {return Error("상담 서버 응답을 확인하지 못했습니다. 새로고침해 예약 현황을 확인한 뒤 같은 요청으로 다시 시도하세요.",503);}
    }
}

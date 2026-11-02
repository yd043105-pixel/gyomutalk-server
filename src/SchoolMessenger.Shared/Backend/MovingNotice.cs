using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SchoolMessenger.Shared;

public static class MovingNotice
{
    // A server that has moved keeps answering only with the new address, so nobody keeps writing to the old data while
    // the apps switch over. server-info stays open and carries the same notice.
    public static void Use(WebApplication app,string root)=>app.Use(async(context,next)=>
    {
        if(context.Request.Path.StartsWithSegments("/api")&&SchoolAppSettings.ActiveMove(SchoolAppSettings.Read(root),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is{} move)
        {
            context.Response.StatusCode=StatusCodes.Status410Gone;context.Response.Headers.CacheControl="no-store";
            await context.Response.WriteAsJsonAsync(new{error="학교 서버가 새 주소로 이전되었습니다. 앱이 새 주소로 다시 연결합니다.",moving=move});return;
        }
        await next();
    });
    public static object Info(string root)
    {
        var settings=SchoolAppSettings.Read(root);
        return new{checkTime=SchoolAppSettings.CheckTime(settings),moving=SchoolAppSettings.ActiveMove(settings,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())};
    }
}

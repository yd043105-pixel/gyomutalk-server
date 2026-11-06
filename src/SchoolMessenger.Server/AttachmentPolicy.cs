using System.Security.Claims;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

public sealed class AttachmentPolicy
{
    readonly Store store;
    readonly object gate=new();
    int gib;
    public AttachmentPolicy(Store store)
    {
        this.store=store;
        store.Execute("CREATE TABLE IF NOT EXISTS AttachmentPolicy(Id INTEGER PRIMARY KEY CHECK(Id=1), LimitGiB INTEGER NOT NULL CHECK(LimitGiB BETWEEN 2 AND 10))");
        gib=store.Query("SELECT LimitGiB FROM AttachmentPolicy WHERE Id=1",r=>r.GetInt32(0)).SingleOrDefault(AttachmentLimits.DefaultGiB);
    }
    public long Limit=>Volatile.Read(ref gib)*AttachmentLimits.GiB;
    public static void Map(RouteGroupBuilder api,RouteGroupBuilder admin,AttachmentPolicy policy)
    {
        api.MapGet("/attachments/policy",()=>Results.Ok(new { maxBytes=policy.Limit }));
        admin.MapPost("/attachment-policy",(AttachmentPolicyChange change,HttpContext context)=>
        {
            if(change.LimitGiB is <AttachmentLimits.DefaultGiB or >AttachmentLimits.MaximumGiB)
                return Results.BadRequest(new {error="첨부 상한은 2~10GiB 사이의 정수로 설정하세요."});
            lock(policy.gate)
            {
                using var db=policy.store.Open();using var tx=db.BeginTransaction();
                using var command=Store.Command(db,"INSERT INTO AttachmentPolicy VALUES(1,$limit) ON CONFLICT(Id) DO UPDATE SET LimitGiB=excluded.LimitGiB",tx,("$limit",change.LimitGiB));
                command.ExecuteNonQuery();
                StaffAccounts.Audit(db,tx,context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,"attachment.limit",change.LimitGiB.ToString());
                tx.Commit();Volatile.Write(ref policy.gib,change.LimitGiB);
            }
            return Results.Ok(new { maxBytes=change.LimitGiB*AttachmentLimits.GiB });
        });
    }
    record AttachmentPolicyChange(int LimitGiB);
}

using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace SchoolMessenger.Server;

// Which optional menus this school uses. Messages, files and the staff directory are the messenger itself and always stay on.
// A switched-off feature disappears from every connected teacher program; its data is kept and comes back when it is switched on again.
public static class SchoolFeatures
{
    public static readonly string[] Names = ["timetable", "chat", "tasks", "survey", "calendar", "rooms", "remote"];
    record Change(string? Name, bool? Enabled);

    public static void Map(WebApplication app, Store store, Timetables timetables)
    {
        store.Execute("CREATE TABLE IF NOT EXISTS SchoolFeatures(Name TEXT PRIMARY KEY,Enabled INTEGER NOT NULL CHECK(Enabled IN (0,1)))");
        // The timetable keeps its own switch: while it is off the server also refuses its data and requests.
        Dictionary<string, bool> All()
        {
            var off = store.Query("SELECT Name FROM SchoolFeatures WHERE Enabled=0", r => r.GetString(0)).ToHashSet();
            return Names.ToDictionary(name => name, name => name == "timetable" ? timetables.FeatureEnabled : !off.Contains(name));
        }
        app.MapGet("/api/features", () => Results.Ok(All())).RequireAuthorization();
        app.MapPost("/api/admin/features", async Task<IResult> (Change change, HttpContext c, IHubContext<MessageHub> hub) =>
        {
            if (change.Name is not { } name || !Names.Contains(name) || change.Enabled is not bool value)
                return Results.BadRequest(new { error = "기능과 사용 여부를 선택하세요." });
            if (name == "timetable") await timetables.SetFeatureEnabled(value, c);
            else
            {
                using var db = store.Open(); using var tx = db.BeginTransaction();
                using var command = Store.Command(db, "INSERT INTO SchoolFeatures VALUES($name,$enabled) ON CONFLICT(Name) DO UPDATE SET Enabled=excluded.Enabled", tx, ("$name", name), ("$enabled", value ? 1 : 0));
                command.ExecuteNonQuery();
                StaffAccounts.Audit(db, tx, c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, "feature." + name + (value ? ".enable" : ".disable"), "features");
                tx.Commit();
            }
            try { await hub.Clients.All.SendAsync("FeaturesChanged"); } catch { /* Clients also read the saved setting when they sign in. */ }
            return Results.Ok(All());
        }).RequireAuthorization("Admin");
    }
}

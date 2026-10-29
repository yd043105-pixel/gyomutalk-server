using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

public static class DeviceSessions
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string Create(SqliteConnection db, string user, string version, DateTimeOffset expires)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO DeviceSessions(Id,TokenHash,UserId,UserVersion,CreatedAt,ExpiresAt) VALUES($id,$hash,$user,$version,$now,$expires)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$user", user); command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$now", Now); command.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery(); return token;
    }
    public static bool Valid(SqliteConnection db, string user, string? version, string? token)
    {
        if (token is not { Length:64 } || !token.All(Uri.IsHexDigit)) return false;
        using var command = db.CreateCommand();
        command.CommandText = "SELECT 1 FROM DeviceSessions WHERE TokenHash=$hash AND UserId=$user AND UserVersion=$version AND RevokedAt IS NULL AND ExpiresAt>$now";
        command.Parameters.AddWithValue("$hash", Hash(token)); command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$version", version ?? ""); command.Parameters.AddWithValue("$now", Now);
        return command.ExecuteScalar() is not null;
    }
    public static void Map(WebApplication app, Func<SqliteConnection> open)
    {
        app.MapGet("/api/sessions", (HttpContext context) =>
        {
            using var db = open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT Id,CreatedAt,ExpiresAt,TokenHash FROM DeviceSessions WHERE UserId=$user AND UserVersion=$version AND RevokedAt IS NULL AND ExpiresAt>$now ORDER BY CreatedAt DESC";
            query.Parameters.AddWithValue("$user", context.User.FindFirstValue(ClaimTypes.NameIdentifier)!); query.Parameters.AddWithValue("$now", Now);query.Parameters.AddWithValue("$version",context.User.FindFirstValue("version")!);
            using var reader = query.ExecuteReader(); var result = new List<object>();
            while (reader.Read()) result.Add(new { id=reader.GetString(0), createdAt=reader.GetInt64(1), expiresAt=reader.GetInt64(2), current=reader.GetString(3)==Hash(context.User.FindFirstValue("sid") ?? "") });
            return Results.Ok(result);
        }).RequireAuthorization();
        app.MapDelete("/api/sessions/{id}", (string id, HttpContext context) =>
        {
            using var db=open(); using var command=db.CreateCommand();
            command.CommandText="UPDATE DeviceSessions SET RevokedAt=$now WHERE Id=$id AND UserId=$user AND RevokedAt IS NULL";
            command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$user",context.User.FindFirstValue(ClaimTypes.NameIdentifier)!); command.Parameters.AddWithValue("$now",Now);
            return command.ExecuteNonQuery()==1 ? Results.Ok() : Results.NotFound();
        }).RequireAuthorization();
    }
}

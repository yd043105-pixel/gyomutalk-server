using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace SchoolMessenger.Shared;

public static class StoredFiles
{
    public static void State(SqliteConnection db, string kind, string id, string state, string? hash = null, SqliteTransaction? transaction = null)
    {
        using var command=db.CreateCommand(); command.Transaction=transaction;
        command.CommandText="""
            INSERT INTO FileVerification(Kind,FileId,State,Sha256,UpdatedAt) VALUES($kind,$id,$state,$hash,$now)
            ON CONFLICT(Kind,FileId) DO UPDATE SET State=excluded.State,Sha256=COALESCE(excluded.Sha256,FileVerification.Sha256),UpdatedAt=excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$kind",kind); command.Parameters.AddWithValue("$id",id); command.Parameters.AddWithValue("$state",state);
        command.Parameters.AddWithValue("$hash",(object?)hash ?? DBNull.Value); command.Parameters.AddWithValue("$now",DeviceSessions.Now); command.ExecuteNonQuery();
    }
    public static string Hash(string path) { using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file)); }
    public static bool Ready(SqliteConnection db, string kind, string id, string path)
    {
        using var command=db.CreateCommand();command.CommandText="SELECT State,Sha256 FROM FileVerification WHERE Kind=$kind AND FileId=$id";
        command.Parameters.AddWithValue("$kind",kind);command.Parameters.AddWithValue("$id",id);
        string? state=null,hash=null;using(var reader=command.ExecuteReader())if(reader.Read()){state=reader.GetString(0);hash=reader.IsDBNull(1)?null:reader.GetString(1);}
        // Legacy files retain their existing authorization/expiry policy; no invented AV assertion.
        if(state is null)return File.Exists(path);
        if(state!="ready"||hash is null||!File.Exists(path))return false;
        try{if(Hash(path)==hash)return true;}catch(IOException){return false;}
        State(db,kind,id,"rejected");return false;
    }
}

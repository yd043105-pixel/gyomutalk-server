using System.Text.Json;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

internal sealed class RosterClassifications
{
    readonly Store store;

    public RosterClassifications(Store store)
    {
        this.store = store;
        store.Execute("""
            CREATE TABLE IF NOT EXISTS RosterClassifications(
                UserId TEXT PRIMARY KEY REFERENCES Users(Id) ON DELETE CASCADE,
                Grades TEXT NOT NULL,
                Duties TEXT NOT NULL
            )
            """);
    }

    public Dictionary<string, RosterClassification> ReadAll() =>
        store.Query("SELECT UserId,Grades,Duties FROM RosterClassifications", reader =>
            (Id: reader.GetString(0), Classification: new RosterClassification(
                JsonSerializer.Deserialize<int[]>(reader.GetString(1))!,
                JsonSerializer.Deserialize<string[]>(reader.GetString(2))!)))
            .ToDictionary(row => row.Id, row => row.Classification);

    public void Save(string id, RosterClassification classification, string actor)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var command = classification.Grades.Length == 0 && classification.Duties.Length == 0
            ? Store.Command(db, "DELETE FROM RosterClassifications WHERE UserId=$id", tx, ("$id", id))
            : Store.Command(db, """
                INSERT INTO RosterClassifications(UserId,Grades,Duties) VALUES($id,$grades,$duties)
                ON CONFLICT(UserId) DO UPDATE SET Grades=excluded.Grades,Duties=excluded.Duties
                """, tx, ("$id", id), ("$grades", JsonSerializer.Serialize(classification.Grades)),
                ("$duties", JsonSerializer.Serialize(classification.Duties)));
        command.ExecuteNonQuery();
        StaffAccounts.Audit(db, tx, actor, "staff.roster-classification", id);
        tx.Commit();
    }
}

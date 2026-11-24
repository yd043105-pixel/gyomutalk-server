using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Server;

internal sealed class RosterFolderStore
{
    readonly Store store;

    public RosterFolderStore(Store store)
    {
        this.store = store;
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var table = Store.Command(db, """
            CREATE TABLE IF NOT EXISTS RosterFolderSettings(
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                Revision INTEGER NOT NULL,
                Folders TEXT NOT NULL
            )
            """, tx);
        table.ExecuteNonQuery();
        using (var columns = Store.Command(db, "SELECT 1 FROM pragma_table_info('RosterFolderSettings') WHERE name='Ordered'", tx))
            if (columns.ExecuteScalar() is null) { using var add = Store.Command(db, "ALTER TABLE RosterFolderSettings ADD COLUMN Ordered INTEGER NOT NULL DEFAULT 0", tx); add.ExecuteNonQuery(); }
        using var exists = Store.Command(db, "SELECT 1 FROM RosterFolderSettings WHERE Id=1", tx);
        if (exists.ExecuteScalar() is null)
        {
            using var seed = Store.Command(db, "INSERT INTO RosterFolderSettings(Id,Revision,Folders) VALUES(1,0,$folders)", tx,
                ("$folders", JsonSerializer.Serialize(ImportLegacy(db, tx))));
            seed.ExecuteNonQuery();
        }
        tx.Commit();
    }

    static RosterFolder[] ImportLegacy(SqliteConnection db, SqliteTransaction tx)
    {
        using var exists = Store.Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='RosterClassifications'", tx);
        if (exists.ExecuteScalar() is null) return [];
        using var legacy = Store.Command(db, "SELECT UserId,Grades,Duties FROM RosterClassifications", tx);
        using var reader = legacy.ExecuteReader();
        var grades = new List<(string Label, string Id)>();
        var duties = new List<(string Label, string Id)>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            grades.AddRange(JsonSerializer.Deserialize<int[]>(reader.GetString(1))!.Select(grade => ($"{grade}학년", id)));
            duties.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(2))!.Select(duty => (duty, id)));
        }
        var folders = new List<RosterFolder>();
        void Add(string name, List<(string Label, string Id)> items)
        {
            if (items.Count == 0) return;
            var parent = Guid.NewGuid().ToString("N");
            folders.Add(new(parent, null, name, []));
            folders.AddRange(items.GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(group => new RosterFolder(Guid.NewGuid().ToString("N"), parent, group.Key,
                    group.Select(item => item.Id).Distinct().ToArray())));
        }
        Add("학년", grades);
        Add("담당업무", duties);
        return folders.ToArray();
    }

    // A deleted person leaves every folder; the revision moves so an open folder editor reloads before saving.
    public static void Remove(SqliteConnection db, SqliteTransaction tx, string userId)
    {
        using var read = Store.Command(db, "SELECT Folders FROM RosterFolderSettings WHERE Id=1", tx);
        if (read.ExecuteScalar() is not string json) return;
        var folders = JsonSerializer.Deserialize<RosterFolder[]>(json)!;
        if (!folders.Any(folder => folder.MemberIds.Contains(userId))) return;
        using var write = Store.Command(db, "UPDATE RosterFolderSettings SET Revision=Revision+1,Folders=$folders WHERE Id=1", tx,
            ("$folders", JsonSerializer.Serialize(folders.Select(folder => folder with { MemberIds = folder.MemberIds.Where(id => id != userId).ToArray() }))));
        write.ExecuteNonQuery();
    }

    public RosterFolders Read() =>
        store.Query("SELECT Revision,Folders,Ordered FROM RosterFolderSettings WHERE Id=1",
            reader => new RosterFolders(reader.GetInt32(0), JsonSerializer.Deserialize<RosterFolder[]>(reader.GetString(1))!, reader.GetBoolean(2))).Single();

    public IResult Save(RosterFolders request, string actor)
    {
        static IResult Error(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);
        if (request.Revision < 0 || request.Folders is null || request.Folders.Length > 1000)
            return Error("폴더는 최대 1,000개까지 저장할 수 있습니다.");
        var folders = new Dictionary<string, RosterFolder>();
        foreach (var folder in request.Folders)
        {
            if (folder is null || !Guid.TryParseExact(folder.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(folder.Name) || folder.Name.Length > 40 || folder.Name.Any(char.IsControl) ||
                folder.MemberIds is null || folder.MemberIds.Length > 20000 || folder.MemberIds.Any(string.IsNullOrWhiteSpace))
                return Error("폴더 이름은 1~40자로 입력하고 구성원을 확인하세요.");
            if (!folders.TryAdd(folder.Id, folder with { Name = folder.Name.Trim(), MemberIds = folder.MemberIds.Distinct().ToArray() }))
                return Error("중복된 폴더가 있습니다.");
        }
        foreach (var folder in folders.Values)
            if (folder.ParentId is { } parent && (!folders.TryGetValue(parent, out var root) || root.ParentId is not null))
                return Error("조직도는 학교 아래 두 단계까지 만들 수 있습니다.");
        if (folders.Values.GroupBy(folder => folder.ParentId).Any(group =>
            group.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Count()))
            return Error("같은 위치에 이름이 같은 폴더가 있습니다.");
        if (folders.Values.Any(folder => folder.ParentId is null && folder.Name == "미분류"))
            return Error("‘미분류’는 교무톡에서 사용하는 이름입니다.");
        if (folders.Values.Sum(folder => folder.MemberIds.Length) > 20000)
            return Error("조직도 전체에 교직원을 최대 20,000번까지 배치할 수 있습니다.");
        var assignedIds = folders.Values.SelectMany(folder => folder.MemberIds).ToHashSet();
        if (!assignedIds.IsSubsetOf(store.Users().Select(user => user.Id)))
            return Error("학교에 등록된 교직원만 폴더에 배치할 수 있습니다.");

        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var update = Store.Command(db, """
            UPDATE RosterFolderSettings SET Revision=Revision+1,Folders=$folders,Ordered=MAX(Ordered,$ordered) WHERE Id=1 AND Revision=$revision
            """, tx, ("$folders", JsonSerializer.Serialize(folders.Values)), ("$revision", request.Revision), ("$ordered", request.Ordered ? 1 : 0));
        if (update.ExecuteNonQuery() != 1) return Error("다른 관리자가 조직도를 수정했습니다. 조직도 폴더 관리를 다시 열어 주세요.", 409);
        StaffAccounts.Audit(db, tx, actor, "staff.roster-folders", "school");
        tx.Commit();
        // Once an administrator has set an order it stays, even if an older program saves the folders later.
        return Results.Ok(Read());
    }
}

using System.Text.Json;
using SchoolMessenger.Contracts;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Server;

public sealed partial class Timetables
{
    void InitializeSwaps() => Data.Execute("""
        CREATE TABLE IF NOT EXISTS TimetableSwaps(
            Id TEXT PRIMARY KEY, Owner TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt INTEGER NOT NULL, Payload TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS TimetableSwapStatus ON TimetableSwaps(Status, CreatedAt);
        CREATE TABLE IF NOT EXISTS TimetableBundles(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL);
        """);

    void MapSwaps(RouteGroupBuilder api)
    {
        api.MapGet("/swaps", async Task<IResult> (HttpContext c) => await WithSchedule(c, () => ListSwaps(User(c))));
        api.MapPost("/swap-plan", (TimetableSwapPlanRequest request, HttpContext c) =>
            WithSchedule(c, () => PlanSwap(request, User(c))));
        api.MapPost("/swaps", (TimetableSwapCreate request, HttpContext c) =>
            WithSchedule(c, () => CreateSwap(request, User(c))));
        api.MapPost("/swaps/{id}/decision", (string id, TimetableSwapDecision request, HttpContext c) =>
            WithSchedule(c, () => DecideSwap(id, request, User(c))));
        api.MapGet("/bundles", async Task<IResult> (HttpContext c) => await WithSchedule(c, () => Results.Ok(Bundles())));
        api.MapPost("/bundles", (TimetableBundle request, HttpContext c) =>
            WithSchedule(c, () => SaveBundle(request, User(c))));
        api.MapDelete("/bundles/{id}", (string id, HttpContext c) => WithSchedule(c, () =>
        {
            RequireManager(User(c));
            Data.Execute("DELETE FROM TimetableBundles WHERE Id=$id", ("$id", id));
            return Results.Ok();
        }));
    }

    async Task<IResult> WithSchedule(HttpContext context, Func<IResult> action)
    {
        await gate.WaitAsync(context.RequestAborted);
        try { return Safe(action); }
        finally { gate.Release(); }
    }

    static T Read<T>(string payload) => JsonSerializer.Deserialize<T>(payload, TimetableData.Json)!;
    static string Payload<T>(T value) => JsonSerializer.Serialize(value, TimetableData.Json);
    static bool ValidId(string? id) => Guid.TryParseExact(id, "N", out _);
    static string[] SwapIds(string[]? ids, int limit = 30)
    {
        ids ??= [];
        if (ids.Length > limit || ids.Any(id => !ValidId(id)) || ids.Distinct().Count() != ids.Length)
            throw new FilePolicyException("교체 신청 목록을 확인하세요.");
        return ids;
    }

    TimetableBundle[] Bundles() =>
        Data.Query("SELECT Payload FROM TimetableBundles", r => Read<TimetableBundle>(r.GetString(0))).ToArray();
    TimetableSwap? FindSwap(string id) => Data.Query("SELECT Payload FROM TimetableSwaps WHERE Id=$id",
        r => Read<TimetableSwap>(r.GetString(0)), ("$id", id)).SingleOrDefault();
    TimetableSwap GetSwap(string id) => FindSwap(id) ?? throw new FilePolicyException("교체 신청을 찾을 수 없습니다.", 404);

    IResult ListSwaps(string user)
    {
        var manager = Manager(user);
        var rows = Data.Query("""
            SELECT Payload FROM TimetableSwaps WHERE Status='pending'
            UNION ALL SELECT Payload FROM (
                SELECT Payload FROM TimetableSwaps WHERE Status<>'pending' AND (Owner=$user OR $manager=1)
                ORDER BY CreatedAt DESC LIMIT 150)
            """, r => Read<TimetableSwap>(r.GetString(0)), ("$user", user), ("$manager", manager ? 1 : 0));
        var pending = rows.Where(r => r.Status == "pending").ToDictionary(r => r.Id);
        var blocked = new Dictionary<string, bool>();
        bool NeedsReview(TimetableSwap swap)
        {
            if (blocked.TryGetValue(swap.Id, out var value)) return value;
            value = swap.Changes.Any(c => string.CompareOrdinal(c.Position.Date, TimetableData.Today) < 0) || swap.Dependencies.Any(id => pending.TryGetValue(id, out var preceding)
                ? NeedsReview(preceding) : FindSwap(id)?.Status != "approved");
            return blocked[swap.Id] = value;
        }
        return Results.Ok(rows.OrderByDescending(r => r.CreatedAt).Select(r => r with
        {
            Status = r.Status == "pending" && NeedsReview(r) ? "review" : r.Status,
            Reason = manager || r.Owner == user ? r.Reason : ""
        }));
    }

    bool Owns(TimetableSlot lesson, string user, string? uniqueName) =>
        lesson.TeacherId == user || lesson.TeacherId.Length == 0 && lesson.TeacherName == uniqueName;

    IResult PlanSwap(TimetableSwapPlanRequest request, string user)
    {
        if (request.Dates is not { Length: >= 1 and <= 2 } || request.Dates.Any(string.IsNullOrEmpty))
            throw new FilePolicyException("조회할 날짜를 선택하세요.");
        var preceding = OrderSwaps(SwapIds(request.Dependencies, 20));
        var projection = Project(Data.Batches(), request.Dates, preceding);
        var dates = request.Dates.ToHashSet();
        var uniqueName = UniqueName(user);
        var bundles = projection.Bundles.SelectMany(b => b.Slots.Select(p => (Position: p, b.Name)))
            .ToDictionary(b => b.Position, b => b.Name);
        var slots = projection.Lessons.Where(p => dates.Contains(p.Key.Date) && p.Value.Subject.Length > 0)
            .OrderBy(p => p.Key.Date).ThenBy(p => p.Value.ClassName).ThenBy(p => p.Key.Period)
            .Select(p => new TimetablePlanSlot(p.Key, p.Value, Owns(p.Value, user, uniqueName), bundles.GetValueOrDefault(p.Key)));
        return Results.Ok(new TimetableSwapPlan(Data.Revision, slots.ToArray()));
    }

    IResult CreateSwap(TimetableSwapCreate request, string user)
    {
        var reason = request.Reason?.Trim() ?? "";
        if (!ValidId(request.Id) || request.Exchanges is not { Length: >= 1 and <= 12 } ||
            request.Exchanges.Any(e => e?.First is null || e.Second is null) ||
            reason.Length > 500 || reason.Any(char.IsControl))
            throw new FilePolicyException("교체할 수업과 신청 내용을 확인하세요.");
        var dependencies = SwapIds(request.Dependencies, 20);
        if (FindSwap(request.Id) is { } existing)
        {
            if (existing.Owner != user || !existing.Exchanges.SequenceEqual(request.Exchanges) ||
                !existing.Dependencies.SequenceEqual(dependencies) || existing.Reason != reason)
                throw new FilePolicyException("같은 신청 번호로 다른 내용을 저장할 수 없습니다.", 409);
            return Results.Ok(existing);
        }
        if (request.Revision != Data.Revision)
            throw new FilePolicyException("공식 시간표가 바뀌었습니다. 교체할 수업을 다시 조회하세요.", 409);
        if (Data.Query("SELECT COUNT(*) FROM TimetableSwaps WHERE Status='pending'", r => r.GetInt32(0))[0] >= 300)
            throw new FilePolicyException("미처리 신청이 많습니다. 수업계에 확인해 주세요.", 429);

        var positions = request.Exchanges.SelectMany(e => new[] { e.First, e.Second }).ToArray();
        if (positions.Any(p => p.Period is < 1 or > 12 || !ValidId(p.ClassId) ||
                string.IsNullOrEmpty(p.Date) || string.CompareOrdinal(p.Date, TimetableData.Today) < 0) ||
            positions.Distinct().Count() != positions.Length ||
            request.Exchanges.Any(e => e.First.ClassId != e.Second.ClassId))
            throw new FilePolicyException("같은 학급의 서로 다른 수업을 한 번씩 선택하세요. 지난 수업은 바꿀 수 없습니다.");

        var preceding = OrderSwaps(dependencies);
        var projection = Project(Data.Batches(), positions.Select(p => p.Date), preceding);
        var uniqueName = UniqueName(user);
        var ownPositions = positions.Where(p => projection.Lessons.TryGetValue(p, out var lesson) &&
            lesson.Subject.Length > 0 && Owns(lesson, user, uniqueName)).ToHashSet();
        var connected = projection.Bundles.Where(b => b.Slots.Any(ownPositions.Contains)).SelectMany(b => b.Slots)
            .Concat(ownPositions).ToHashSet();
        if (ownPositions.Count == 0 || request.Exchanges.Any(e => !connected.Contains(e.First) && !connected.Contains(e.Second)))
            throw new FilePolicyException("내 수업 또는 내 수업과 함께 움직이는 묶음을 신청하세요.", 403);

        var changes = request.Exchanges.SelectMany(e =>
        {
            if (!projection.Lessons.TryGetValue(e.First, out var first) || first.Subject.Length == 0 ||
                !projection.Lessons.TryGetValue(e.Second, out var second) || second.Subject.Length == 0)
                throw new FilePolicyException("시간표에 있는 두 수업을 선택하세요.", 409);
            return new[] { new TimetableSwapChange(e.First, first, second), new TimetableSwapChange(e.Second, second, first) };
        }).ToArray();
        if (changes.All(c => SameLesson(c.Before, c.After))) throw new FilePolicyException("교체하면 달라지는 수업이 있어야 합니다.");
        var swap = new TimetableSwap(request.Id, user, school.GetUser(user)!.Name, "pending", reason, Now,
            dependencies, request.Exchanges, changes);
        ApplySwap(projection, swap);
        Data.Execute("INSERT INTO TimetableSwaps VALUES($id,$owner,'pending',$now,$payload)",
            ("$id", swap.Id), ("$owner", user), ("$now", swap.CreatedAt), ("$payload", Payload(swap)));
        return Results.Ok(swap);
    }

    IResult DecideSwap(string id, TimetableSwapDecision request, string user)
    {
        if (request.Status is not ("cancelled" or "rejected")) throw new FilePolicyException("신청 처리 상태를 확인하세요.");
        var swap = GetSwap(id);
        if (request.Status == "rejected") RequireManager(user);
        else if (swap.Owner != user) throw new FilePolicyException("본인 신청만 취소할 수 있습니다.", 403);
        if (swap.Status != "pending") throw new FilePolicyException("이미 처리된 신청입니다.", 409);
        var result = Reviewed(swap, user, request.Status);
        Data.Execute("UPDATE TimetableSwaps SET Status=$status,Payload=$payload WHERE Id=$id AND Status='pending'",
            ("$status", result.Status), ("$payload", Payload(result)), ("$id", id));
        return Results.Ok(result);
    }

    TimetableSwap Reviewed(TimetableSwap swap, string reviewer, string status) =>
        swap with { Status = status, Reviewer = reviewer, ReviewerName = school.GetUser(reviewer)?.Name, ReviewedAt = Now };

    IResult SaveBundle(TimetableBundle request, string user)
    {
        RequireManager(user);
        if (!ValidId(request.Id) || request.Name?.Trim() is not { Length: >= 1 and <= 60 } ||
            request.Name.Any(char.IsControl) || request.Kind is not ("consecutive" or "simultaneous") ||
            request.Slots is not { Length: >= 2 and <= 12 } || request.Slots.Any(p => p is null) ||
            request.Slots.Distinct().Count() != request.Slots.Length)
            throw new FilePolicyException("묶음 이름과 수업을 확인하세요.");
        var bundle = request with { Name = request.Name.Trim(), Slots = request.Slots.OrderBy(p => p.Period).ToArray() };
        var rows = Data.Batches();
        var projection = Project(rows, bundle.Slots.Select(p => p.Date), []);
        if (bundle.Slots.Any(p => !projection.Lessons.TryGetValue(p, out var lesson) || lesson.Subject.Length == 0))
            throw new FilePolicyException("게시된 시간표에 있는 수업을 선택하세요.");
        var overlaps = projection.Bundles.Any(b => b.Id != bundle.Id && b.Slots.Intersect(bundle.Slots).Any());
        if (overlaps) throw new FilePolicyException("이미 다른 묶음에 포함된 수업입니다.", 409);
        ValidateBundle(bundle, projection.Grades);
        Data.Execute("INSERT INTO TimetableBundles VALUES($id,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload",
            ("$id", bundle.Id), ("$payload", Payload(bundle)));
        return Results.Ok(bundle);
    }

    // Every pending predecessor must be explicitly included. Published predecessors are already in the official timetable.
    TimetableSwap[] OrderSwaps(string[] ids)
    {
        var selected = ids.ToHashSet();
        var complete = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<TimetableSwap>();
        void Visit(string id)
        {
            if (complete.Contains(id)) return;
            var swap = GetSwap(id);
            if (swap.Status == "approved" && !selected.Contains(id)) { complete.Add(id); return; }
            if (swap.Status != "pending" || !selected.Contains(id) || !visiting.Add(id) ||
                swap.Changes.Any(c => string.CompareOrdinal(c.Position.Date, TimetableData.Today) < 0))
                throw new FilePolicyException("대기 중인 선행 신청을 함께 선택하세요. 반려·취소된 신청에 연결된 교체는 다시 검토해야 합니다.", 409);
            foreach (var preceding in swap.Dependencies) Visit(preceding);
            visiting.Remove(id);
            complete.Add(id);
            result.Add(swap);
        }
        foreach (var id in ids) Visit(id);
        return result.ToArray();
    }

    sealed record SwapProjection(Dictionary<TimetablePosition, TimetableSlot> Lessons,
        TimetableBundle[] Bundles, Dictionary<string, int> Grades, Dictionary<string, string> TeacherIds);

    SwapProjection Project(TimetableBatch[] rows, IEnumerable<string> requestedDates, TimetableSwap[] preceding)
    {
        var dates = requestedDates.Concat(preceding.SelectMany(s => s.Changes.Select(c => c.Position.Date))).Distinct().ToArray();
        if (dates.Length > 31) throw new FilePolicyException("한 번에 확인할 수 있는 날짜는 최대 31일입니다.");
        var grades = Directory().Classes.ToDictionary(c => c.Id, c => c.Grade);
        var teacherIds = school.Users().Where(u => u.Active).GroupBy(u => u.Name).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id);
        var rooms = grades.Keys.ToArray();
        var lessons = dates.SelectMany(date => TimetableData.Resolve(rows, date, rooms).Slots
            .Select(s => (Key: new TimetablePosition(date, s.ClassId, s.Period), Slot: s)))
            .ToDictionary(s => s.Key, s => s.Slot);
        var projection = new SwapProjection(lessons, Bundles(), grades, teacherIds);
        foreach (var swap in preceding) ApplySwap(projection, swap);
        return projection;
    }

    static bool SameLesson(TimetableSlot a, TimetableSlot b) => a.Subject == b.Subject &&
        (a.TeacherId.Length > 0 && b.TeacherId.Length > 0 ? a.TeacherId == b.TeacherId : a.TeacherName == b.TeacherName);

    static (string Start, string End)? Clock(TimetablePosition position, Dictionary<string, int> grades)
    {
        var period = grades.TryGetValue(position.ClassId, out var grade)
            ? SchoolDay.Lessons(grade).FirstOrDefault(p => p.Number == position.Period) : null;
        return period is null ? null : (period.Start, period.End);
    }

    static void ValidateBundle(TimetableBundle bundle, Dictionary<string, int> grades)
    {
        var first = bundle.Slots[0];
        var sameDay = bundle.Slots.All(p => p.Date == first.Date);
        var correct = bundle.Kind == "consecutive"
            ? bundle.Slots.Select((p, i) => p.ClassId == first.ClassId && p.Period == first.Period + i).All(ok => ok)
            : bundle.Slots.All(p => p.Period == first.Period && Clock(p, grades) == Clock(first, grades));
        if (!sameDay || !correct)
            throw new FilePolicyException("묶음수업은 같은 날짜의 연속 교시 또는 같은 시간에 진행되는 수업으로 옮겨야 합니다.", 409);
    }

    static void ApplySwap(SwapProjection projection, TimetableSwap swap)
    {
        var moves = swap.Exchanges.SelectMany(e => new[] { (e.First, e.Second), (e.Second, e.First) })
            .ToDictionary(e => e.Item1, e => e.Item2);
        foreach (var change in swap.Changes)
            if (!projection.Lessons.TryGetValue(change.Position, out var current) || !SameLesson(current, change.Before))
                throw new FilePolicyException("교체 대상이 바뀌었습니다. 신청을 다시 검토하세요.", 409);

        for (var i = 0; i < projection.Bundles.Length; i++)
        {
            var bundle = projection.Bundles[i];
            if (!bundle.Slots.Any(moves.ContainsKey)) continue;
            if (!bundle.Slots.All(moves.ContainsKey))
                throw new FilePolicyException($"‘{bundle.Name}’ 묶음수업은 전체를 함께 옮겨야 합니다.", 409);
            var moved = bundle with { Slots = bundle.Slots.Select(p => moves[p]).ToArray() };
            ValidateBundle(moved, projection.Grades);
            projection.Bundles[i] = moved;
        }
        foreach (var change in swap.Changes) projection.Lessons[change.Position] = change.After with { Period = change.Position.Period };
        ValidateTeacherConflicts(projection, swap.Changes.Select(c => c.Position));
    }

    static void ValidateTeacherConflicts(SwapProjection projection, IEnumerable<TimetablePosition> positions)
    {

        string Teacher(TimetableSlot lesson) => lesson.TeacherId.Length > 0 ? lesson.TeacherId :
            projection.TeacherIds.GetValueOrDefault(lesson.TeacherName, lesson.TeacherName);
        foreach (var position in positions)
        {
            var lesson = projection.Lessons[position];
            var teacher = Teacher(lesson);
            var clock = Clock(position, projection.Grades);
            var shared = projection.Bundles.FirstOrDefault(b => b.Kind == "simultaneous" && b.Slots.Contains(position));
            var overlaps = projection.Lessons.Any(p => p.Key.Date == position.Date && p.Key != position &&
                p.Value.Subject.Length > 0 && Teacher(p.Value) == teacher && shared?.Slots.Contains(p.Key) != true &&
                (Clock(p.Key, projection.Grades) is { } other && clock is { } time
                    ? string.CompareOrdinal(time.Start, other.End) < 0 && string.CompareOrdinal(other.Start, time.End) < 0
                    : p.Key.Period == position.Period));
            if (overlaps) throw new FilePolicyException($"{lesson.TeacherName} 선생님의 수업 시간이 겹칩니다.", 409);
        }
    }

    (TimetableSwap[] Approvals, TimetableBundle[] Bundles) VerifySwapPublication(
        string[] ids, TimetableBatch[] batches, TimetableBatch[] official, string reviewer)
    {
        if (ids.Length == 0) return ([], []);
        if (batches.Any(b => b.Kind != "daily")) throw new FilePolicyException("교체 승인에는 나이스 일자별 수정 시간표를 사용하세요.");
        var selected = OrderSwaps(ids);
        var dates = selected.SelectMany(s => s.Changes.Select(c => c.Position.Date)).Distinct().ToArray();
        if (dates.Any(date => string.CompareOrdinal(date, TimetableData.Today) < 0))
            throw new FilePolicyException("지난 날짜의 교체 신청은 승인할 수 없습니다.", 409);
        var projection = Project(official, dates, selected);
        var uploaded = Project(official.Concat(batches).ToArray(), dates, []);
        foreach (var position in selected.SelectMany(s => s.Changes.Select(c => c.Position)).Distinct())
        {
            if (!batches.Any(b => b.Date == position.Date && b.Cells.Any(c => c.ClassId == position.ClassId)) ||
                !uploaded.Lessons.TryGetValue(position, out var lesson) || !SameLesson(lesson, projection.Lessons[position]))
                throw new FilePolicyException($"나이스 파일에 {position.Date} {projection.Lessons[position].ClassName} {position.Period}교시 교체가 반영되어야 합니다.", 409);
        }
        ValidateTeacherConflicts(uploaded with { Bundles = projection.Bundles }, selected.SelectMany(s => s.Changes.Select(c => c.Position)).Distinct());
        return (selected.Select(s => Reviewed(s, reviewer, "approved")).ToArray(), projection.Bundles);
    }
}

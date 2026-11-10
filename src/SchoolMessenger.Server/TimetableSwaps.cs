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
        CREATE TABLE IF NOT EXISTS TimetableBundleRules(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL);
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
        api.MapPost("/swap-options", (TimetableSwapOptionsRequest request, HttpContext c) =>
            WithSchedule(c, () => SwapOptions(request, User(c))));
        api.MapPost("/swaps/approve", async Task<IResult> (TimetableCommit request, HttpContext c) =>
        {
            await gate.WaitAsync(c.RequestAborted);
            try { RequireEnabled(); return await ApproveSwaps(request, User(c)); }
            catch (FilePolicyException e) { return Error(e); }
            finally { gate.Release(); }
        });
        api.MapGet("/bundle-rules", async Task<IResult> (HttpContext c) => await WithSchedule(c, () => Results.Ok(Rules())));
        api.MapPost("/bundle-rules", (TimetableBundleRule request, HttpContext c) =>
            WithSchedule(c, () => SaveRule(request, User(c))));
        api.MapDelete("/bundle-rules/{id}", (string id, HttpContext c) => WithSchedule(c, () =>
        {
            RequireManager(User(c));
            Data.Execute("DELETE FROM TimetableBundleRules WHERE Id=$id", ("$id", id));
            return Results.Ok();
        }));
        api.MapGet("/base-lessons", async Task<IResult> (HttpContext c) => await WithSchedule(c, () => BaseLessons(User(c))));
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
        try { return Safe(() => { RequireEnabled(); return action(); }); }
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
    TimetableBundleRule[] Rules() =>
        Data.Query("SELECT Payload FROM TimetableBundleRules", r => Read<TimetableBundleRule>(r.GetString(0))).ToArray();

    IResult SaveRule(TimetableBundleRule request, string user)
    {
        RequireManager(user);
        var rooms = Directory().Classes.Select(c => c.Id).ToHashSet();
        if (!ValidId(request.Id) || request.Name?.Trim() is not { Length: >= 1 and <= 60 } || request.Name.Any(char.IsControl) ||
            request.Members is not { Length: >= 2 and <= 60 } ||
            request.Members.Any(m => m is null || !rooms.Contains(m.ClassId) || m.Subject is not { Length: >= 1 and <= 200 }) ||
            request.Members.Distinct().Count() != request.Members.Length)
            throw new FilePolicyException("묶음 이름과 함께 움직일 수업을 둘 이상 선택하세요.");
        if (Rules().Any(r => r.Id != request.Id && r.Members.Intersect(request.Members).Any()))
            throw new FilePolicyException("이미 다른 묶음에 들어 있는 수업이 있습니다.", 409);
        var rule = request with { Name = request.Name.Trim() };
        Data.Execute("INSERT INTO TimetableBundleRules VALUES($id,$payload) ON CONFLICT(Id) DO UPDATE SET Payload=excluded.Payload",
            ("$id", rule.Id), ("$payload", Payload(rule)));
        return Results.Ok(rule);
    }

    // What each class is taught in its current base timetable: the list bundles are chosen from.
    IResult BaseLessons(string user)
    {
        RequireManager(user);
        var rows = Data.Batches().Where(b => b.Kind == "base").ToArray();
        var lessons = Directory().Classes.SelectMany(room =>
        {
            var latest = rows.Where(b => b.Cells.Any(c => c.ClassId == room.Id)).MaxBy(b => b.Revision);
            if (latest is null || latest.Withdrawn) return [];
            return latest.Cells.Where(c => c.ClassId == room.Id && c.Subject.Length > 0).GroupBy(c => c.Subject)
                .Select(g => new TimetableBaseLesson(room.Id, room.Name, room.Grade, g.Key,
                    string.Join(", ", g.Select(c => c.TeacherName).Distinct()), g.Count()));
        });
        return Results.Ok(lessons.ToArray());
    }

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
        if (request.Dates is not { Length: >= 1 and <= 7 } || request.Dates.Any(string.IsNullOrEmpty))
            throw new FilePolicyException("조회할 날짜를 선택하세요.");
        var preceding = OrderSwaps(SwapIds(request.Dependencies, 20));
        // The request being drafted may reach into another week than the one shown.
        var projection = Project(Data.Batches(), request.Dates.Concat(DatesOf(request.Exchanges)), preceding);
        if (request.Exchanges is { Length: > 0 } draft) ApplySwap(projection, Trial(projection, ValidExchanges(draft)));
        var dates = request.Dates.ToHashSet();
        var uniqueName = UniqueName(user);
        var bundles = projection.Bundles.SelectMany(b => b.Slots.Select(p => (Position: p, b.Name)))
            .ToDictionary(b => b.Position, b => b.Name);
        var slots = projection.Lessons.Where(p => dates.Contains(p.Key.Date) && p.Value.Subject.Length > 0)
            .OrderBy(p => p.Key.Date).ThenBy(p => p.Value.ClassName).ThenBy(p => p.Key.Period)
            .Select(p => new TimetablePlanSlot(p.Key, p.Value, Owns(p.Value, user, uniqueName),
                bundles.GetValueOrDefault(p.Key) ?? RuleOf(projection, p.Key.ClassId, p.Value)?.Name));
        return Results.Ok(new TimetableSwapPlan(Data.Revision, slots.ToArray()));
    }

    static IEnumerable<string> DatesOf(TimetableExchange[]? exchanges) =>
        (exchanges ?? []).SelectMany(e => new[] { e?.First?.Date, e?.Second?.Date }).Where(d => !string.IsNullOrEmpty(d))!;

    static TimetableExchange[] ValidExchanges(TimetableExchange[]? exchanges)
    {
        if (exchanges is not { Length: >= 1 and <= 12 } || exchanges.Any(e => e?.First is null || e.Second is null))
            throw new FilePolicyException("교체할 수업과 신청 내용을 확인하세요.");
        var positions = exchanges.SelectMany(e => new[] { e.First, e.Second }).ToArray();
        if (positions.Any(p => p.Period is < 1 or > 12 || !ValidId(p.ClassId) ||
                string.IsNullOrEmpty(p.Date) || string.CompareOrdinal(p.Date, TimetableData.Today) < 0) ||
            positions.Distinct().Count() != positions.Length ||
            exchanges.Any(e => e.First.ClassId != e.Second.ClassId))
            throw new FilePolicyException("같은 학급의 서로 다른 수업을 한 번씩 선택하세요. 지난 수업은 바꿀 수 없습니다.");
        return exchanges;
    }

    // Both ends of an exchange must hold a lesson, so a class never ends up with an empty period.
    static TimetableSwapChange[] ChangesOf(SwapProjection projection, TimetableExchange[] exchanges) => exchanges.SelectMany(e =>
    {
        if (!projection.Lessons.TryGetValue(e.First, out var first) || first.Subject.Length == 0 ||
            !projection.Lessons.TryGetValue(e.Second, out var second) || second.Subject.Length == 0)
            throw new FilePolicyException("시간표에 있는 두 수업을 선택하세요.", 409);
        return new[] { new TimetableSwapChange(e.First, first, second), new TimetableSwapChange(e.Second, second, first) };
    }).ToArray();

    static TimetableSwap Trial(SwapProjection projection, TimetableExchange[] exchanges) =>
        new("", "", "", "pending", "", 0, [], exchanges, ChangesOf(projection, exchanges));

    static TimetableBundleRule? RuleOf(SwapProjection projection, string room, TimetableSlot lesson) => lesson.Subject.Length == 0 ? null :
        projection.Rules.FirstOrDefault(r => r.Members.Any(m => m.ClassId == room && m.Subject == lesson.Subject));

    // The lessons of one bundle that run in the same period as this one, itself included.
    static TimetablePosition[] Group(SwapProjection projection, TimetablePosition at)
    {
        if (!projection.Lessons.TryGetValue(at, out var lesson) || RuleOf(projection, at.ClassId, lesson) is not { } rule) return [at];
        return projection.Lessons.Where(p => p.Key.Date == at.Date && p.Key.Period == at.Period &&
            rule.Members.Any(m => m.ClassId == p.Key.ClassId && m.Subject == p.Value.Subject)).Select(p => p.Key).ToArray();
    }

    // Trading two periods of one class drags along every class bundled with either lesson, and whatever those are bundled with.
    static TimetableExchange[] Closure(SwapProjection projection, TimetablePosition first, TimetablePosition second)
    {
        var rooms = new List<string> { first.ClassId };
        for (var i = 0; i < rooms.Count; i++)
            foreach (var at in new[] { first with { ClassId = rooms[i] }, second with { ClassId = rooms[i] } })
                foreach (var member in Group(projection, at))
                    if (!rooms.Contains(member.ClassId)) rooms.Add(member.ClassId);
        return rooms.Select(room => new TimetableExchange(first with { ClassId = room }, second with { ClassId = room })).ToArray();
    }

    // Every lesson of the same class the picked one can trade places with, without a teacher being in two rooms,
    // a class losing a lesson or a bundle being split.
    IResult SwapOptions(TimetableSwapOptionsRequest request, string user)
    {
        if (request.Dates is not { Length: >= 1 and <= 7 } || request.Dates.Any(string.IsNullOrEmpty) || request.Pick is not { } pick)
            throw new FilePolicyException("옮길 수업을 선택하세요.");
        var preceding = OrderSwaps(SwapIds(request.Dependencies, 20));
        var projection = Project(Data.Batches(), request.Dates.Append(pick.Date).Concat(DatesOf(request.Exchanges)), preceding);
        var draft = request.Exchanges is { Length: > 0 } ? ValidExchanges(request.Exchanges) : [];
        if (draft.Length > 0) ApplySwap(projection, Trial(projection, draft));
        if (!projection.Lessons.TryGetValue(pick, out var lesson) || lesson.Subject.Length == 0)
            throw new FilePolicyException("옮길 수업을 선택하세요.");
        var uniqueName = UniqueName(user);
        if (!Manager(user) && !Group(projection, pick).Any(p => Owns(projection.Lessons[p], user, uniqueName)))
            throw new FilePolicyException("내 수업 또는 내 수업과 함께 움직이는 묶음만 옮길 수 있습니다.", 403);
        var taken = draft.SelectMany(e => new[] { e.First, e.Second }).ToHashSet();
        var dates = request.Dates.ToHashSet();
        var options = new List<TimetableSwapOption>();
        if (string.CompareOrdinal(pick.Date, TimetableData.Today) < 0) return Results.Ok(options);
        foreach (var target in projection.Lessons.Where(p => p.Key.ClassId == pick.ClassId && p.Key != pick && dates.Contains(p.Key.Date) &&
            p.Value.Subject.Length > 0 && string.CompareOrdinal(p.Key.Date, TimetableData.Today) >= 0 && !SameLesson(p.Value, lesson))
            .Select(p => p.Key).OrderBy(p => p.Date).ThenBy(p => p.Period).ToArray())
        {
            var exchanges = Closure(projection, pick, target);
            if (exchanges.Length > 12 || exchanges.SelectMany(e => new[] { e.First, e.Second }).Any(taken.Contains)) continue;
            try
            {
                ApplySwap(projection with { Lessons = new(projection.Lessons), Bundles = [.. projection.Bundles] }, Trial(projection, exchanges));
                options.Add(new(target, exchanges));
            }
            catch (FilePolicyException) { /* This trade breaks a rule, so it is not offered. */ }
        }
        return Results.Ok(options);
    }

    // Approves requests without a NEIS file: the changed classes get that day's timetable as the requests leave it.
    async Task<IResult> ApproveSwaps(TimetableCommit request, string owner)
    {
        RequireManager(owner);
        var ids = SwapIds(request.SwapIds).Order(StringComparer.Ordinal).ToArray();
        if (!ValidId(request.ClientId) || ids.Length == 0) throw new FilePolicyException("승인할 교체 신청을 선택하세요.");
        if (Data.ByClient(owner, request.ClientId) is { } prior)
            return (prior.ApprovedSwapIds ?? []).SequenceEqual(ids) ? Results.Ok(prior)
                : throw new FilePolicyException("같은 요청 번호로 다른 신청을 승인할 수 없습니다.", 409);
        var official = Data.Batches();
        var selected = OrderSwaps(ids);
        var dates = selected.SelectMany(s => s.Changes.Select(c => c.Position.Date)).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (dates.Any(date => string.CompareOrdinal(date, TimetableData.Today) < 0))
            throw new FilePolicyException("지난 날짜의 교체 신청은 승인할 수 없습니다.", 409);
        var projection = Project(official, dates, selected);
        var batches = dates.Select((date, i) =>
        {
            var rooms = selected.SelectMany(s => s.Changes).Where(c => c.Position.Date == date).Select(c => c.Position.ClassId).ToHashSet();
            var term = official.Where(b => b.Kind == "base" && string.CompareOrdinal(b.Start, date) <= 0 && string.CompareOrdinal(b.End, date) >= 0 &&
                b.Cells.Any(c => rooms.Contains(c.ClassId))).MaxBy(b => b.Revision) ?? throw new FilePolicyException("교체할 날짜의 기초시간표가 없습니다.", 409);
            var weekday = ((int)TimetableData.Date(date).DayOfWeek + 6) % 7 + 1;
            var cells = projection.Lessons.Where(p => p.Key.Date == date && rooms.Contains(p.Key.ClassId) && p.Value.Subject.Length > 0)
                .Select(p => new TimetableCell(p.Key.ClassId, p.Value.ClassName, weekday, p.Key.Period, p.Value.Subject, p.Value.TeacherId, p.Value.TeacherName)).ToArray();
            var client = i == 0 ? request.ClientId : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(request.ClientId + "|" + i)))[..32].ToLowerInvariant();
            var batch = new TimetableBatch(Guid.NewGuid().ToString("N"), 0, owner, client, "daily", term.Year, term.Semester, date, date, date,
                "수업 교체 승인", Now, false, cells, null, ids);
            TimetableData.Validate(batch); ValidateLinks(batch);
            return batch;
        }).ToArray();
        var approvals = selected.Select(s => Reviewed(s, owner, "approved")).ToArray();
        var timeline = official.ToList();
        var entries = batches.Select(batch =>
        {
            var notices = Notifications(batch, Changes(batch, timeline), approvals);
            timeline.Add(batch);
            return (Batch: batch, Notices: notices);
        }).ToArray();
        var saved = Data.Publish(entries, approvals, projection.Bundles);
        for (var i = 0; i < saved.Length; i++) await SendNotices(saved[i], entries[i].Notices);
        return Results.Ok(saved[0]);
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

        var positions = ValidExchanges(request.Exchanges).SelectMany(e => new[] { e.First, e.Second }).ToArray();

        var preceding = OrderSwaps(dependencies);
        var projection = Project(Data.Batches(), positions.Select(p => p.Date), preceding);
        var uniqueName = UniqueName(user);
        var ownPositions = positions.Where(p => projection.Lessons.TryGetValue(p, out var lesson) &&
            lesson.Subject.Length > 0 && Owns(lesson, user, uniqueName)).ToHashSet();
        // The timetable manager arranges trades for anyone; a teacher asks for their own lessons and what is bundled with them.
        var connected = projection.Bundles.Where(b => b.Slots.Any(ownPositions.Contains)).SelectMany(b => b.Slots)
            .Concat(request.Exchanges.Where(e => ownPositions.Contains(e.First) || ownPositions.Contains(e.Second))
                .SelectMany(e => Closure(projection, e.First, e.Second)).SelectMany(e => new[] { e.First, e.Second }))
            .Concat(ownPositions).ToHashSet();
        if (!Manager(user) && (ownPositions.Count == 0 || request.Exchanges.Any(e => !connected.Contains(e.First) && !connected.Contains(e.Second))))
            throw new FilePolicyException("내 수업 또는 내 수업과 함께 움직이는 묶음을 신청하세요.", 403);

        var changes = ChangesOf(projection, request.Exchanges);
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
        TimetableBundle[] Bundles, Dictionary<string, int> Grades, Dictionary<string, string> TeacherIds, TimetableBundleRule[] Rules);

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
        var projection = new SwapProjection(lessons, Bundles(), grades, teacherIds, Rules());
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

        // Bundled lessons that share a period leave it together and arrive in one period together.
        foreach (var (position, target) in moves)
        {
            var group = Group(projection, position);
            if (group.Length > 1 && group.Any(p => !moves.TryGetValue(p, out var to) || to.Date != target.Date || to.Period != target.Period))
                throw new FilePolicyException($"‘{RuleOf(projection, position.ClassId, projection.Lessons[position])!.Name}’ 묶음수업은 같은 시간으로 함께 옮겨야 합니다.", 409);
        }
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
            var together = Group(projection, position);
            var overlaps = projection.Lessons.Any(p => p.Key.Date == position.Date && p.Key != position &&
                p.Value.Subject.Length > 0 && Teacher(p.Value) == teacher && shared?.Slots.Contains(p.Key) != true && !together.Contains(p.Key) &&
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

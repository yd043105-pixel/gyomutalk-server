using System;
using System.Collections.Generic;
using System.Linq;

namespace SchoolMessenger.Contracts;

public record RosterFolder(string Id, string? ParentId, string Name, string[] MemberIds);
public record RosterFolders(int Revision, RosterFolder[] Folders);

public record RosterGroup(string Key, string Name, string[] DirectIds, RosterGroup[] Children)
{
    public string[] AllIds() => DirectIds.Concat(Children.SelectMany(child => child.DirectIds)).Distinct().ToArray();

    public static RosterGroup[] Build(RosterFolder[]? folders, (string Id, string Label, string Department)[] people,
        string query, bool hideEmpty = false)
    {
        bool Matches(string text) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (folders is null)
            return people.GroupBy(person => person.Department).OrderBy(group => group.Key)
                .Select(group => new RosterGroup("department:" + group.Key, group.Key,
                    group.Where(person => Matches(person.Label) || Matches(group.Key)).Select(person => person.Id).ToArray(), []))
                .Where(group => group.DirectIds.Length > 0).ToArray();

        var labels = people.ToDictionary(person => person.Id, person => person.Label);
        var assigned = folders.SelectMany(folder => folder.MemberIds).ToHashSet();
        var children = folders.ToLookup(folder => folder.ParentId);
        RosterGroup Project(RosterFolder folder, bool ancestorMatches, bool root)
        {
            var matches = ancestorMatches || Matches(folder.Name);
            return new("folder:" + folder.Id, folder.Name,
                folder.MemberIds.Where(id => labels.TryGetValue(id, out var label) && (matches || Matches(label))).ToArray(),
                root ? children[folder.Id].OrderBy(child => child.Name).Select(child => Project(child, matches, false))
                    .Where(Visible).ToArray() : []);
        }
        bool Visible(RosterGroup group) => group.DirectIds.Length > 0 || group.Children.Length > 0 ||
            !hideEmpty && query.Length == 0;
        var groups = children[null].OrderBy(folder => folder.Name).Select(folder => Project(folder, false, true))
            .Where(Visible).ToList();
        var unassigned = people.Where(person => !assigned.Contains(person.Id) &&
            (Matches(person.Label) || Matches("미분류"))).Select(person => person.Id).ToArray();
        if (unassigned.Length > 0) groups.Add(new("unclassified", "미분류", unassigned, []));
        return groups.ToArray();
    }
}

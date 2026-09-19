using Styx;
using Styx.Logic.Profiles;
using Styx.Logic.Profiles.Quest;
using Styx.Logic.Questing;

namespace Styx.Logic.Questing.Recovery;

public sealed class QuestDependencyEvidence
{
    public QuestDependencyEvidence(
        uint questId,
        uint prerequisiteQuestId,
        bool isActive,
        bool isAuthoritative)
    {
        QuestId = questId;
        PrerequisiteQuestId = prerequisiteQuestId;
        IsActive = isActive;
        IsAuthoritative = isAuthoritative;
    }

    public uint QuestId { get; }
    public uint PrerequisiteQuestId { get; }
    public bool IsActive { get; }
    public bool IsAuthoritative { get; }
}

public static class QuestPrerequisiteAuthority
{
    private const int MaximumDependencyTraversal = 4096;
    private static readonly object DependencySync = new();
    private static QuestDependencyEvidence[] _publishedDependencies = Array.Empty<QuestDependencyEvidence>();
    private static uint[] _publishedQuestIds = Array.Empty<uint>();
    private static bool _publishedDependencyAuthority;

    public static void PublishAuthoritativeDependencies(
        IEnumerable<QuestDependencyEvidence> dependencies,
        IEnumerable<uint>? knownQuestIds = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        QuestDependencyEvidence[] snapshot = dependencies
            .Where(item => item is not null && item.QuestId != 0 && item.PrerequisiteQuestId != 0)
            .Select(item => new QuestDependencyEvidence(
                item.QuestId, item.PrerequisiteQuestId, false, true))
            .DistinctBy(item => (item.QuestId, item.PrerequisiteQuestId))
            .OrderBy(item => item.PrerequisiteQuestId)
            .ThenBy(item => item.QuestId)
            .ToArray();
        uint[] known = (knownQuestIds ?? Array.Empty<uint>())
            .Concat(snapshot.SelectMany(item => new[] { item.QuestId, item.PrerequisiteQuestId }))
            .Where(id => id != 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        lock (DependencySync)
        {
            _publishedDependencies = snapshot;
            _publishedQuestIds = known;
            _publishedDependencyAuthority = snapshot.Length > 0;
        }
    }

    public static void ClearPublishedDependencyAuthority()
    {
        lock (DependencySync)
        {
            _publishedDependencies = Array.Empty<QuestDependencyEvidence>();
            _publishedQuestIds = Array.Empty<uint>();
            _publishedDependencyAuthority = false;
        }
    }

    public static QuestPrerequisiteStatus DetermineFromDependencies(
        uint questId,
        IEnumerable<QuestDependencyEvidence> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        QuestDependencyEvidence[] snapshot = dependencies.Where(item => item is not null).ToArray();
        if (snapshot.Any(item => item.IsActive && !item.IsAuthoritative))
            return QuestPrerequisiteStatus.Unknown;
        QuestDependencyEvidence[] authoritative = snapshot
            .Where(item => item.IsAuthoritative && item.QuestId != 0 && item.PrerequisiteQuestId != 0)
            .ToArray();
        if (authoritative.Length == 0)
            return QuestPrerequisiteStatus.Unknown;
        return Traverse(
            questId,
            authoritative,
            authoritative.Where(item => item.IsActive).Select(item => item.QuestId),
            authoritative.SelectMany(item => new[] { item.QuestId, item.PrerequisiteQuestId }));
    }

    public static QuestPrerequisiteStatus DetermineFromPublishedDependencies(
        uint questId,
        IEnumerable<uint> activeQuestIds)
    {
        ArgumentNullException.ThrowIfNull(activeQuestIds);
        QuestDependencyEvidence[] published;
        uint[] knownQuestIds;
        bool publishedAuthority;
        lock (DependencySync)
        {
            published = _publishedDependencies;
            knownQuestIds = _publishedQuestIds;
            publishedAuthority = _publishedDependencyAuthority;
        }
        if (!publishedAuthority || questId == 0 || Array.BinarySearch(knownQuestIds, questId) < 0)
            return QuestPrerequisiteStatus.Unknown;
        return Traverse(questId, published, activeQuestIds, knownQuestIds);
    }

    private static QuestPrerequisiteStatus Traverse(
        uint questId,
        IReadOnlyList<QuestDependencyEvidence> dependencies,
        IEnumerable<uint> activeQuestIds,
        IEnumerable<uint> knownQuestIds)
    {
        var active = new HashSet<uint>(activeQuestIds.Where(id => id != 0 && id != questId));
        var known = new HashSet<uint>(knownQuestIds.Where(id => id != 0));
        if (active.Any(id => !known.Contains(id)))
            return QuestPrerequisiteStatus.Unknown;

        var reverse = dependencies
            .GroupBy(item => item.PrerequisiteQuestId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.QuestId).Distinct().OrderBy(id => id).ToArray());
        var pending = new Queue<uint>();
        var visited = new HashSet<uint> { questId };
        pending.Enqueue(questId);
        int traversed = 0;
        while (pending.Count > 0)
        {
            uint prerequisite = pending.Dequeue();
            if (!reverse.TryGetValue(prerequisite, out uint[]? dependents))
                continue;
            foreach (uint dependent in dependents)
            {
                if (++traversed > MaximumDependencyTraversal)
                    return QuestPrerequisiteStatus.Unknown;
                if (!visited.Add(dependent))
                    continue;
                if (active.Contains(dependent))
                    return QuestPrerequisiteStatus.Active;
                pending.Enqueue(dependent);
            }
        }
        return QuestPrerequisiteStatus.NotActive;
    }

    public static QuestPrerequisiteStatus Determine(
        bool relationDataAvailable,
        uint nextQuestId,
        bool nextQuestIsAccepted,
        bool activeGuideContainsNextQuest)
    {
        if (!relationDataAvailable)
            return QuestPrerequisiteStatus.Unknown;
        if (nextQuestId == 0)
            return QuestPrerequisiteStatus.NotActive;
        return nextQuestIsAccepted || activeGuideContainsNextQuest
            ? QuestPrerequisiteStatus.Active
            : QuestPrerequisiteStatus.NotActive;
    }

    public static QuestPrerequisiteStatus Capture(PlayerQuest? quest)
    {
        if (quest is null)
            return QuestPrerequisiteStatus.Unknown;

        try
        {
            if (StyxWoW.Me is null || string.IsNullOrEmpty(ProfileManager.XmlLocation))
                return QuestPrerequisiteStatus.Unknown;
            Profile? profile = ProfileManager.CurrentProfile;
            if (profile is null)
                return QuestPrerequisiteStatus.Unknown;

            var activeIds = profile.Quests.Select(item => item.ID)
                .Concat(GetQuestIds(profile.QuestOrder))
                .Concat(StyxWoW.Me.QuestLog.GetAllQuests().Select(item => item.Id))
                .Where(id => id != 0 && id != quest.Id)
                .Distinct()
                .ToArray();
            if (quest.NextQuestId != 0 && activeIds.Contains(quest.NextQuestId))
                return QuestPrerequisiteStatus.Active;

            return DetermineFromPublishedDependencies(quest.Id, activeIds);
        }
        catch (Exception)
        {
            return QuestPrerequisiteStatus.Unknown;
        }
    }

    private static IEnumerable<uint> GetQuestIds(IEnumerable<OrderNode> nodes)
    {
        foreach (OrderNode node in nodes ?? Enumerable.Empty<OrderNode>())
        {
            uint id = GetQuestId(node);
            if (id != 0)
                yield return id;
            if (node is INodeContainer container)
            {
                foreach (uint child in GetQuestIds(container.GetNodes()))
                    yield return child;
            }
        }
    }

    private static uint GetQuestId(OrderNode node)
    {
        return node switch
        {
            PickUpNode value => value.QuestId,
            TurnInNode value => value.QuestId,
            ObjectiveNode value => value.QuestId,
            MoveToNode value => value.QuestId,
            UseItemNode value => value.QuestId,
            AbandonQuestNode value => value.QuestId,
            _ => 0
        };
    }
}

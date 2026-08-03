using CodexDecision.Core.Routing;
using CodexDecision.Core.SpecialSkills;

namespace CodexDecision.Core.Conversations;

public enum FollowUpBehavior
{
    Queue,
    Steer,
}

public sealed record QueuedFollowUp(
    Guid Id,
    string Prompt,
    RoutingOverride? RoutingOverride,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SpecialSkillId> SpecialSkills,
    AutoRoutingMode AutoRoutingMode,
    IReadOnlyList<CodexDecision.Core.AppServer.AppServerAttachment>? Attachments = null);

public sealed class FollowUpQueue
{
    private readonly Lock gate = new();
    private readonly List<QueuedFollowUp> items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<QueuedFollowUp> Snapshot()
    {
        lock (gate)
        {
            return items.ToArray();
        }
    }

    public QueuedFollowUp Enqueue(
        string prompt,
        RoutingOverride? routingOverride = null,
        IEnumerable<SpecialSkillId>? specialSkills = null,
        AutoRoutingMode autoRoutingMode = AutoRoutingMode.Adaptive,
        IEnumerable<CodexDecision.Core.AppServer.AppServerAttachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var item = new QueuedFollowUp(
            Guid.NewGuid(),
            prompt.Trim(),
            routingOverride,
            DateTimeOffset.UtcNow,
            SpecialSkillCatalog.Normalize(specialSkills),
            autoRoutingMode,
            attachments?.ToArray() ?? []);
        lock (gate)
        {
            items.Add(item);
        }

        OnChanged();
        return item;
    }

    public QueuedFollowUp? Peek()
    {
        lock (gate)
        {
            return items.Count > 0 ? items[0] : null;
        }
    }

    public QueuedFollowUp? Get(Guid id)
    {
        lock (gate)
        {
            return items.FirstOrDefault(item => item.Id == id);
        }
    }

    public void Replace(IEnumerable<QueuedFollowUp> restoredItems)
    {
        ArgumentNullException.ThrowIfNull(restoredItems);
        var normalized = restoredItems
            .Select(item =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Prompt);
                return item with
                {
                    Prompt = item.Prompt.Trim(),
                    SpecialSkills = SpecialSkillCatalog.Normalize(item.SpecialSkills),
                };
            })
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        lock (gate)
        {
            items.Clear();
            items.AddRange(normalized);
        }

        OnChanged();
    }

    public bool Edit(Guid id, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var changed = false;
        lock (gate)
        {
            var index = items.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                items[index] = items[index] with { Prompt = prompt.Trim() };
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return changed;
    }

    public bool MoveUp(Guid id) => Move(id, -1);

    public bool MoveDown(Guid id) => Move(id, 1);

    public bool Remove(Guid id)
    {
        var changed = false;
        lock (gate)
        {
            var index = items.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                items.RemoveAt(index);
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return changed;
    }

    private bool Move(Guid id, int offset)
    {
        var changed = false;
        lock (gate)
        {
            var index = items.FindIndex(item => item.Id == id);
            var target = index + offset;
            if (index >= 0 && target >= 0 && target < items.Count)
            {
                (items[index], items[target]) = (items[target], items[index]);
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged();
        }

        return changed;
    }

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

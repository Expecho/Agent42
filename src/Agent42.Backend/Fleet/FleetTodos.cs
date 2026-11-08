using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>One item of a team's todo list.</summary>
public sealed record FleetTodoItem(int Id, string Title, bool Done);

/// <summary>
/// The todo list of ONE team attempt (ADR 0117), shared by every agent of that team: agents plan their steps on it and
/// tick them off, and the user watches the checklist live on <c>/fleet/</c>. It is plain <see cref="AIFunction"/>s in
/// the shape of <see cref="FleetCanvas"/>, not Agent Framework's <c>TodoProvider</c>, because fleet agents are built from
/// YAML by name and have no session to hang a provider on.
///
/// <para><b>Whole list per event.</b> Every change publishes <c>todos.updated</c> carrying the complete list, so a replay
/// or a page reload rebuilds the latest state by replacing. Nothing else is persisted: the list ends with its attempt.</para>
///
/// <para><b>Atomic, ordered.</b> Agents of a team run concurrently, so each call mutates and publishes under one lock:
/// ids are unique (monotonic, never reused) and the event order matches the state order. Over the caps a call is
/// refused with the reason, never truncated (the ADR 0102 convention).</para>
/// </summary>
public sealed class FleetTodos
{
    public const string UpdatedEvent = "todos.updated";
    public const int MaxItems = 50;
    public const int MaxTitleLength = 200;

    /// <summary>The four tool names, reserved in the fleet tool catalog so no MCP tool can take one.</summary>
    public static readonly IReadOnlyList<string> ToolNames = ["todo_add", "todo_complete", "todo_remove", "todo_list"];

    private readonly IFleetEventSink _events;
    private readonly string _teamId;
    private readonly int _attempt;
    private readonly Lock _gate = new();
    private readonly List<FleetTodoItem> _items = [];
    private int _lastId;

    public FleetTodos(IFleetEventSink events, string teamId, int attempt)
    {
        _events = events;
        _teamId = teamId;
        _attempt = attempt;

        // The model obeys these descriptions, not the prompt (a user's prompt override replaces the prompt text), so
        // each one carries its own rules.
        Tools =
        [
            AIFunctionFactory.Create(Add, "todo_add",
                "Add one or more items to your team's shared todo list, one per step of the work you are about to do. The "
                + "user sees the list live and every agent of your team sees it too. Use it for work with several steps, "
                + $"not for a one-shot answer. At most {MaxItems} items, each title at most {MaxTitleLength} characters, "
                + "never truncated: a longer title or a surplus item is refused. Returns the new ids."),
            AIFunctionFactory.Create(Complete, "todo_complete",
                "Mark one or more todo items done by id, as soon as you finish them, so the user and your team see the "
                + "progress. Completing an item that is already done is fine."),
            AIFunctionFactory.Create(Remove, "todo_remove",
                "Remove one or more todo items by id when they are no longer needed. Do not remove an item to hide work "
                + "you did not do: complete it, or remove it only when it no longer applies."),
            AIFunctionFactory.Create(ListItems, "todo_list",
                "Show your team's shared todo list: each item with its id and whether it is done. Check it before you "
                + "add items, so you do not duplicate what a teammate planned."),
        ];
    }

    public IReadOnlyList<AIFunction> Tools { get; }

    /// <summary>The current items, in the order they were added.</summary>
    public IReadOnlyList<FleetTodoItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>The list for the judge, open items first, or an empty string when nobody planned anything.</summary>
    public string Describe()
    {
        var items = Items;
        if (items.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder($"The team's todo list ({items.Count(i => !i.Done)} open of {items.Count}):\n");
        foreach (var item in items.OrderBy(i => i.Done))
        {
            text.Append(item.Done ? "- [x] " : "- [ ] ").AppendLine(item.Title);
        }

        return text.ToString().TrimEnd();
    }

    private string Add([Description("The titles of the items to add, one per step, each a short sentence.")] string[] titles)
    {
        var clean = (titles ?? []).Select(t => string.Join(' ', (t ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToList();
        if (clean.Count == 0 || clean.Any(t => t.Length == 0))
        {
            return "Refused: pass at least one title, and no empty ones. Nothing was added.";
        }

        if (clean.FirstOrDefault(t => t.Length > MaxTitleLength) is { } tooLong)
        {
            return $"Refused: a title is {tooLong.Length} characters, over the {MaxTitleLength} limit (\"{tooLong[..40]}…\"). Shorten it. Nothing was added.";
        }

        lock (_gate)
        {
            if (_items.Count + clean.Count > MaxItems)
            {
                return $"Refused: the list has {_items.Count} items and holds at most {MaxItems}, so {clean.Count} more do not fit. "
                    + "Complete or remove items first. Nothing was added.";
            }

            var ids = new List<int>();
            foreach (var title in clean)
            {
                _items.Add(new FleetTodoItem(++_lastId, title, false));
                ids.Add(_lastId);
            }

            Publish();
            return $"Added {ids.Count} item(s) with id {string.Join(", ", ids)}.";
        }
    }

    private string Complete([Description("The ids of the items to mark done.")] int[] ids)
        => Change(ids, "Marked done", remove: false);

    private string Remove([Description("The ids of the items to remove.")] int[] ids)
        => Change(ids, "Removed", remove: true);

    // Every id must exist, or nothing changes. Nothing is published when nothing changed (completing items that are
    // already done), so a looping agent cannot grow the event log with whole-list events.
    private string Change(int[]? ids, string verb, bool remove)
    {
        var wanted = (ids ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return "Refused: pass at least one id.";
        }

        lock (_gate)
        {
            var unknown = wanted.Where(id => _items.All(i => i.Id != id)).ToList();
            if (unknown.Count > 0)
            {
                return $"Refused: there is no item with id {string.Join(", ", unknown)}. Nothing was changed. "
                    + $"Ids on the list: {(_items.Count == 0 ? "none" : string.Join(", ", _items.Select(i => i.Id)))}.";
            }

            var changed = false;
            foreach (var id in wanted)
            {
                var index = _items.FindIndex(i => i.Id == id);
                if (remove)
                {
                    _items.RemoveAt(index);
                    changed = true;
                }
                else if (!_items[index].Done)
                {
                    _items[index] = _items[index] with { Done = true };
                    changed = true;
                }
            }

            if (changed)
            {
                Publish();
            }

            return $"{verb}: {string.Join(", ", wanted)}.";
        }
    }

    private string ListItems()
    {
        var items = Items;
        return items.Count == 0
            ? "The todo list is empty. Add the steps of your work with todo_add."
            : string.Join("\n", items.Select(i => $"{i.Id}. [{(i.Done ? "x" : " ")}] {i.Title}"));
    }

    // Under the gate, so the event order is the state order. The literal, not UpdatedEvent: the page's handler guard
    // finds event types by Publish("…").
    private void Publish() => _events.Publish("todos.updated", new
    {
        teamId = _teamId,
        attempt = _attempt,
        todos = _items.Select(i => new { id = i.Id, title = i.Title, done = i.Done }).ToList(),
    });
}

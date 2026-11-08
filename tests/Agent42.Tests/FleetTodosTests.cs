using Agent42.Backend;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The fleet todo list (ADR 0117): unique never-reused ids, idempotent completion, refusals that name the reason,
/// caps that refuse instead of truncating, and one <c>todos.updated</c> per change carrying the whole list.
/// </summary>
public sealed class FleetTodosTests
{
    private readonly RecordingFleetSink _events = new();
    private readonly FleetTodos _todos;

    public FleetTodosTests() => _todos = new FleetTodos(_events, "team-1", 2);

    private static async Task<string> InvokeAsync(FleetTodos todos, string name, AIFunctionArguments arguments)
        => (await todos.Tools.Single(t => t.Name == name).InvokeAsync(arguments))!.ToString()!;

    private Task<string> AddAsync(params string[] titles) => InvokeAsync(_todos, "todo_add", new() { ["titles"] = titles });

    private Task<string> CompleteAsync(params int[] ids) => InvokeAsync(_todos, "todo_complete", new() { ["ids"] = ids });

    private Task<string> RemoveAsync(params int[] ids) => InvokeAsync(_todos, "todo_remove", new() { ["ids"] = ids });

    [Fact]
    public void ToolNames_MatchTheTools()
        => Assert.Equal(FleetTodos.ToolNames.Order(), _todos.Tools.Select(t => t.Name).Order());

    [Fact]
    public async Task Add_AssignsUniqueIds_NeverReused()
    {
        Assert.Contains("id 1, 2", await AddAsync("one", "two"));
        await RemoveAsync(2);
        Assert.Contains("id 3", await AddAsync("three"));

        Assert.Equal([1, 3], _todos.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Complete_IsIdempotent_UnknownIdRefused()
    {
        await AddAsync("one", "two");
        await CompleteAsync(1);
        var again = await CompleteAsync(1);

        Assert.Contains("Marked done", again);
        Assert.Equal([true, false], _todos.Items.Select(i => i.Done));

        var refused = await CompleteAsync(2, 9);
        Assert.Contains("no item with id 9", refused);
        Assert.False(_todos.Items.Single(i => i.Id == 2).Done); // all or nothing
    }

    [Fact]
    public async Task Remove_UnknownIdRefused_NothingChanges()
    {
        await AddAsync("one");

        Assert.Contains("no item with id 5", await RemoveAsync(1, 5));
        Assert.Single(_todos.Items);
    }

    [Fact]
    public async Task Caps_RefuseNeverTruncate()
    {
        Assert.Contains("over the 200 limit", await AddAsync(new string('x', 201)));
        Assert.Contains("at least one title", await AddAsync(" ", "ok"));
        Assert.Empty(_todos.Items);

        await AddAsync(Enumerable.Range(0, FleetTodos.MaxItems).Select(i => $"item {i}").ToArray());
        var surplus = await AddAsync("one too many");

        Assert.Contains("Refused", surplus);
        Assert.Equal(FleetTodos.MaxItems, _todos.Items.Count);

        var exact = new string('y', FleetTodos.MaxTitleLength);
        await RemoveAsync(1);
        await AddAsync(exact);
        Assert.Equal(exact, _todos.Items[^1].Title);
    }

    [Fact]
    public async Task EveryChange_PublishesOneEventWithTheWholeList()
    {
        await AddAsync("one", "two");
        await CompleteAsync(1);
        await CompleteAsync(1); // already done: publishes nothing
        await RemoveAsync(2);
        await AddAsync(); // refused: publishes nothing
        await CompleteAsync(7); // refused: publishes nothing

        var events = _events.Of(FleetTodos.UpdatedEvent).ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal("team-1", events[0].GetProperty("teamId").GetString());
        Assert.Equal(2, events[0].GetProperty("attempt").GetInt32());
        Assert.Equal(2, events[0].GetProperty("todos").GetArrayLength());
        var last = events[2].GetProperty("todos");
        Assert.Equal(1, last.GetArrayLength());
        Assert.True(last[0].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentCalls_PublishOrderedWholeLists()
    {
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => AddAsync($"item {i}"))));

        var counts = _events.Of(FleetTodos.UpdatedEvent).Select(e => e.GetProperty("todos").GetArrayLength()).ToList();
        Assert.Equal(Enumerable.Range(1, 40), counts); // each event is one step further: order matches state
        Assert.Equal(40, _todos.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task List_ShowsIdsAndDoneFlags()
    {
        Assert.Contains("empty", await InvokeAsync(_todos, "todo_list", []));

        await AddAsync("one", "two");
        await CompleteAsync(2);

        Assert.Equal("1. [ ] one\n2. [x] two", await InvokeAsync(_todos, "todo_list", []));
    }

    [Fact]
    public async Task Describe_ListsOpenItemsFirst_EmptyWhenNoneWerePlanned()
    {
        Assert.Equal("", _todos.Describe());

        await AddAsync("first", "second");
        await CompleteAsync(1);

        var text = _todos.Describe();
        Assert.Contains("1 open of 2", text);
        Assert.True(text.IndexOf("- [ ] second", StringComparison.Ordinal) < text.IndexOf("- [x] first", StringComparison.Ordinal));
    }
}

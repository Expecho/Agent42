using System.Text.RegularExpressions;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Guards the frontend half of the chat gateway (ADR 0111). The repo has no JS test runner, so these are source-text
/// checks on the served assets; the behaviour is checked by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class ChatGatewayPanelAssetsTests(StaticAssetsServerFixture fixture)
{
    // SHELL is all-or-nothing: a module missing from it breaks the offline load.
    [Fact]
    public async Task Shell_ListsTheModule()
        => Assert.Contains("\"/js/chatgateway.js\"", await fixture.Client.GetStringAsync("/sw.js"), StringComparison.Ordinal);

    // AC23: every status the server reports has a label, so a new one cannot render as a raw string unnoticed.
    [Fact]
    public async Task EveryServerStatus_HasALabel()
    {
        var module = await fixture.Client.GetStringAsync("/js/chatgateway.js");
        var statuses = typeof(ChatConnectionStatus).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(9, statuses.Count);
        Assert.All(statuses, status => Assert.Contains($"\"{status}\":", module, StringComparison.Ordinal));
    }

}

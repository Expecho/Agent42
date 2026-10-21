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
    // A module unreachable from main.js is invisible to every other guard (ADR 0041).
    [Fact]
    public async Task Module_IsWiredFromMainAndLoadedWithPreferences()
    {
        var main = await fixture.Client.GetStringAsync("/js/main.js");
        var profile = await fixture.Client.GetStringAsync("/js/profile.js");

        Assert.Contains("from \"./chatgateway.js\"", main, StringComparison.Ordinal);
        Assert.Contains("wireChatGateway();", main, StringComparison.Ordinal);
        Assert.Contains("loadChatConnections();", profile, StringComparison.Ordinal);
    }

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

        Assert.Equal(7, statuses.Count);
        Assert.All(statuses, status => Assert.Contains($"\"{status}\":", module, StringComparison.Ordinal));
    }

    // Names, fault reasons and pairing codes are server strings: textContent only. innerHTML is used only to clear.
    [Fact]
    public async Task Module_NeverRendersServerStringsAsHtml()
    {
        var module = await fixture.Client.GetStringAsync("/js/chatgateway.js");

        Assert.DoesNotContain("insertAdjacentHTML", module, StringComparison.Ordinal);
        Assert.All(Regex.Matches(module, @"innerHTML\s*=\s*([^;]+);"), m => Assert.Equal("\"\"", m.Groups[1].Value.Trim()));
    }

    [Fact]
    public async Task Module_CallsTheManagementEndpoints()
    {
        var module = await fixture.Client.GetStringAsync("/js/chatgateway.js");

        Assert.Contains("\"/api/chat-connections\"", module, StringComparison.Ordinal);
        Assert.Contains("/repair`", module, StringComparison.Ordinal);
        Assert.Contains("method: \"DELETE\"", module, StringComparison.Ordinal);
    }

    // The token field is write-only and never pre-filled; the panel says plainly what leaves the app (rule 13).
    [Fact]
    public async Task Panel_KeepsTheTokenWriteOnly_AndSaysWhatIsSent()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");

        Assert.Matches("""<input[^>]*type="password"[^>]*id="chatBotToken"|<input[^>]*id="chatBotToken"[^>]*type="password" """.Trim(), html);
        Assert.Contains("id=\"panelChat\"", html, StringComparison.Ordinal);
        Assert.Contains("are stored by the chat app", html, StringComparison.Ordinal);
        Assert.Contains("messages from anyone else are ignored", html, StringComparison.Ordinal);
    }

    // Phase 1c (Step 16, C13): the three per-connection toggles are real inputs that the save sends, and the groups toggle
    // says in plain words that everyone in the group sees the replies.
    [Fact]
    public async Task Panel_HasTheGroupVoiceAndNotificationToggles_AndTheGroupWarning()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");
        var module = await fixture.Client.GetStringAsync("/js/chatgateway.js");

        foreach (var (id, field, input) in new[]
        {
            ("chatGroups", "groupsEnabled", "chatGroupsInput"),
            ("chatVoiceNotes", "voiceNotesEnabled", "chatVoiceNotesInput"),
            ("chatNotifications", "notificationsEnabled", "chatNotificationsInput"),
        })
        {
            Assert.Matches($"""<input[^>]*type="checkbox"[^>]*id="{id}" """.Trim(), html);
            Assert.Contains($"{field}: {input}.checked", module, StringComparison.Ordinal);
            Assert.Contains($"{input}.checked = connection?.{field} ?? false;", module, StringComparison.Ordinal);
        }

        Assert.Contains("Everyone in the group or channel sees the replies", html, StringComparison.Ordinal);
        Assert.Contains("/setprivacy", html, StringComparison.Ordinal);
    }

    // Phase 1b: Slack's second credential (the xapp- app-level token) is write-only too, shown only for Slack, and sent
    // with the bot token; a blank field keeps its stored token (Update_WithOneToken_KeepsTheOtherStoredToken).
    [Fact]
    public async Task Panel_HasSlacksAppTokenField_WriteOnly()
    {
        var html = await fixture.Client.GetStringAsync("/index.html");
        var module = await fixture.Client.GetStringAsync("/js/chatgateway.js");

        Assert.Matches("""<input[^>]*type="password"[^>]*id="chatAppToken"|<input[^>]*id="chatAppToken"[^>]*type="password" """.Trim(), html);
        Assert.Matches("""id="chatAppTokenField"[^>]*hidden""", html);
        Assert.Contains("const needsAppToken = new Set([\"Slack\"]);", module, StringComparison.Ordinal);
        Assert.Contains("appToken: appToken || null", module, StringComparison.Ordinal);
        Assert.Contains("Slack:", module, StringComparison.Ordinal);
        Assert.Contains("Discord:", module, StringComparison.Ordinal);
    }

    // The tab is appended last, so the index pins on MCP (3), Configuration (4) and Skills (5) still hold.
    [Fact]
    public async Task ChatTab_IsTheLastPreferencesTab()
    {
        var dom = await fixture.Client.GetStringAsync("/js/dom.js");
        var start = dom.IndexOf("export const profileTabs = wireTabs([", StringComparison.Ordinal);
        var tabs = dom[start..dom.IndexOf("]);", start, StringComparison.Ordinal)];

        Assert.EndsWith("getElementById(\"panelChat\")],\n", tabs.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }
}

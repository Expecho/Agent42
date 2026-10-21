using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// Chat command recognition (ADR 0111). A command is only ever the first token, in this platform's prefix, addressed
/// to no bot or to this one; everything else is text that reaches the model.
/// </summary>
public class ChatCommandsTests
{
    [Theory]
    [InlineData("/new", "new", "")]
    [InlineData("  /STOP  ", "stop", "")]
    [InlineData("/model list", "model", "list")]
    [InlineData("/model  foundry:gpt-5.4 ", "model", "foundry:gpt-5.4")]
    [InlineData("/agent Weather bot", "agent", "Weather bot")]
    [InlineData("/new@agent42_test_bot", "new", "")]
    [InlineData("/start", "help", "")]
    [InlineData("/whatever", "help", "")]
    public void Recognizes(string text, string name, string arguments)
        => Assert.Equal(new ChatCommand(name, arguments), ChatCommands.Parse(text, '/', "agent42_test_bot"));

    [Theory]
    [InlineData("hello /new")]
    [InlineData("/etc/hosts is a file")]
    [InlineData("/new@some_other_bot")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("!new")]
    public void IsText(string? text)
        => Assert.Null(ChatCommands.Parse(text, '/', "agent42_test_bot"));

    [Fact]
    public void SlackUsesItsOwnPrefix()
    {
        Assert.Equal(new ChatCommand("new", ""), ChatCommands.Parse("!new", '!', null));
        Assert.Null(ChatCommands.Parse("/new", '!', null));
    }

    [Fact]
    public void Help_ListsEveryCommandWithThePrefix()
    {
        var help = ChatCommands.HelpText('!');

        Assert.All(ChatCommands.All, c => Assert.Contains("!" + c.Name, help));
    }
}

namespace Agent42.Backend;

/// <summary>The one message every non-chat entry point answers when a client sends the reserved <c>auto</c> model (ADR 0137).</summary>
public static class AutoModelRefusal
{
    public const string Message = "Auto is a chat-picker choice. Pick a specific model here.";
}

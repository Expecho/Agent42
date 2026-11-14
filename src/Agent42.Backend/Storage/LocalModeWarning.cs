namespace Agent42.Backend;

/// <summary>
/// The one startup warning local storage owes the operator: with no Easy Auth in front there is no authentication, and
/// every caller is the single user <c>local-dev</c> (<c>CurrentUser</c>). Production never sees it. Warning, not
/// Information: <c>Logging:LogLevel:Default</c> is Warning in production, so an Information record would never reach a log
/// (ADR 0054).
/// </summary>
public static class LocalModeWarning
{
    /// <summary>The message, kept apart so a test and the docs can name the same words.</summary>
    public const string Text =
        "Storage:Provider is 'local' and the app has no authentication of its own: every caller is the user 'local-dev'. " +
        "Keep it on localhost, or put an authenticating reverse proxy in front of it.";

    /// <summary>Logs <see cref="Text"/> when the provider is <see cref="StorageProvider.Local"/>. Returns whether it did.</summary>
    public static bool LogIfLocal(IConfiguration configuration, ILogger logger)
    {
        if (StorageProviderSetting.Read(configuration) != StorageProvider.Local)
        {
            return false;
        }

        logger.LogWarning(Text);
        return true;
    }
}

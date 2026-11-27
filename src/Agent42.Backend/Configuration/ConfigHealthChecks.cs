namespace Agent42.Backend;

public static partial class ConfigHealth
{
    // A check for a value that parses (or should) as an absolute URI. Missing → missingStatus (Error for a
    // fatal Core value, Warning for optional infra); present-but-not-absolute → Error (malformed).
    private static ConfigCheck UriCheck(
        string group, string key, string? value, bool requireHttps,
        string missingStatus = Error, string? missingMessage = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ConfigCheck(group, key, missingStatus, missingMessage ?? "missing", Billable: false);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (requireHttps && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ConfigCheck(group, key, Error,
                requireHttps ? "malformed — not an absolute https URL" : "malformed — not an absolute URL",
                Billable: false);
        }

        return new ConfigCheck(group, key, Ok, "configured", Billable: false);
    }

    private static ConfigCheck PresenceCheck(
        string group, string key, string? value, string present, string missing, string missingStatus = Error) =>
        string.IsNullOrWhiteSpace(value)
            ? new ConfigCheck(group, key, missingStatus, missing, Billable: false)
            : new ConfigCheck(group, key, Ok, present, Billable: false);

    // A probe check's static (unprobed) shape: skipped, with a hint to run live checks.
    private static ConfigCheck NotProbed(string group, string key, string label, bool billable) =>
        new(group, key, Skipped, $"{label}: not probed (run live checks).", Billable: billable);
}

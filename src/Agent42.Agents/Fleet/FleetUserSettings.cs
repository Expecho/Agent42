namespace Agent42.Agents;

/// <summary>
/// A user's overrides of the operator's fleet limits (ADR 0135): the "Advanced settings" on <c>/fleet/</c>. Our own
/// JSON in Blob (<c>{userId}/settings.json</c>), flat namespace like the rest of the fleet (ADR 0039). Every field is
/// nullable: null means "use the operator's value". Unknown JSON members are ignored, so an older or newer document
/// still reads. Operator-only fields (token caps, concurrency, size caps, kill switches) are deliberately absent.
/// </summary>
public sealed record FleetUserSettings
{
    // Behaviour
    public int? MaxTeamDepth { get; init; }
    public int? MaxAgentsPerTask { get; init; }
    public int? MaxConcurrentSubTeamsPerLevel { get; init; }
    public int? DefaultMaxGroupChatRounds { get; init; }
    public double? BudgetSoftLimit { get; init; }
    public int? JudgeDepth { get; init; }
    public int? PassingScore { get; init; }
    public int? MaxAttempts { get; init; }
    public int? MaxFixRounds { get; init; }
    public int? ApprovalDepth { get; init; }
    public int? MaxClarifyingQuestions { get; init; }
    public int? SelfReviewMaxIterations { get; init; }
    public string? DefaultCopilotModel { get; init; }

    // Dream
    public string? DreamAtUtc { get; init; }
    public int? DreamWindowHours { get; init; }
    public int? DreamMaxTasks { get; init; }
    public int? DreamMinTasks { get; init; }
    public int? DreamMinNewTasks { get; init; }
    public int? DreamMaxDigestChars { get; init; }
    public int? DreamMinSupport { get; init; }
    public int? DreamMaxNewPerRun { get; init; }
    public int? LessonMaxChars { get; init; }
    public int? LessonsInPrompt { get; init; }
    public int? LessonTtlDays { get; init; }
    public int? LessonsMaxStored { get; init; }
}

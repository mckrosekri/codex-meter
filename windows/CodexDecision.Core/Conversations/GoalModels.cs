namespace CodexDecision.Core.Conversations;

public enum GoalRunState
{
    Active,
    Paused,
    Completed,
}

public sealed record GoalDefinition(
    string Outcome,
    string Constraints,
    string Verification,
    GoalRunState State = GoalRunState.Active,
    DateTimeOffset? UpdatedAt = null)
{
    public GoalDefinition Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Outcome);
        return this with
        {
            Outcome = Outcome.Trim(),
            Constraints = Constraints?.Trim() ?? string.Empty,
            Verification = Verification?.Trim() ?? string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    public string ToPrompt() =>
        $"Goal outcome:\n{Outcome.Trim()}\n\nConstraints:\n{Constraints.Trim()}\n\nVerification:\n{Verification.Trim()}";
}

namespace Bbs.Client.Core.Domain;

public sealed record AgentRuntimeState(
    string BotId,
    AgentLifecycleState LifecycleState,
    bool IsAttached,
    string? LastErrorCode,
    DateTimeOffset UpdatedAtUtc)
{
    public AgentRuntimeState(
        string BotId,
        AgentLifecycleState LifecycleState,
        bool IsArmed,
        string? LastErrorCode,
        DateTimeOffset UpdatedAtUtc,
        int compatibilityMarker = 0)
        : this(BotId, LifecycleState, IsArmed, LastErrorCode, UpdatedAtUtc)
    {
        _ = compatibilityMarker;
    }

    public bool IsArmed => IsAttached;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BotId))
        {
            errors.Add("bot_id_required");
        }

        if (!Enum.IsDefined(typeof(AgentLifecycleState), LifecycleState))
        {
            errors.Add("lifecycle_state_invalid");
        }

        if (UpdatedAtUtc == default)
        {
            errors.Add("updated_at_utc_required");
        }

        return errors;
    }
}

public enum AgentLifecycleState
{
    Unknown = 0,
    Starting = 1,
    Idle = 2,
    ActiveSession = 3,
    Stopping = 4,
    Stopped = 5,
    Error = 6
}

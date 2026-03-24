namespace Bbs.Client.Core.Domain;

public enum BotCardVisualState
{
    Registered = 0,
    Armed = 1,
    ActiveSession = 2,
    Error = 3
}

public static class BotCardVisualStateRules
{
    public static BotCardVisualState Resolve(AgentRuntimeState? runtimeState)
    {
        if (runtimeState is null)
        {
            return BotCardVisualState.Registered;
        }

        if (runtimeState.LifecycleState == AgentLifecycleState.Error || !string.IsNullOrWhiteSpace(runtimeState.LastErrorCode))
        {
            return BotCardVisualState.Error;
        }

        if (runtimeState.LifecycleState == AgentLifecycleState.ActiveSession)
        {
            return BotCardVisualState.ActiveSession;
        }

        if (runtimeState.IsArmed)
        {
            return BotCardVisualState.Armed;
        }

        return BotCardVisualState.Registered;
    }
}

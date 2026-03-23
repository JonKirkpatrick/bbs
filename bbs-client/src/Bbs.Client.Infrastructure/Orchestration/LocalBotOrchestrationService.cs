using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.Infrastructure.Orchestration;

public sealed class LocalBotOrchestrationService : IBotOrchestrationService
{
    private readonly IClientStorage _storage;
    private readonly IClientLogger? _logger;

    public LocalBotOrchestrationService(IClientStorage storage, IClientLogger? logger = null)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task<BotOrchestrationResult> ArmBotAsync(BotProfile profile, CancellationToken cancellationToken = default)
    {
        AgentRuntimeState nextState;
        string message;
        bool success;

        if (string.IsNullOrWhiteSpace(profile.LaunchPath))
        {
            nextState = BuildState(profile.BotId, AgentLifecycleState.Error, isArmed: false, "launch_path_required");
            message = "Cannot arm bot: launch path is required.";
            success = false;
        }
        else if (!File.Exists(profile.LaunchPath))
        {
            nextState = BuildState(profile.BotId, AgentLifecycleState.Error, isArmed: false, "launch_path_missing");
            message = $"Cannot arm bot: launch path not found ({profile.LaunchPath}).";
            success = false;
        }
        else
        {
            nextState = BuildState(profile.BotId, AgentLifecycleState.Idle, isArmed: true, null);
            message = "Bot armed successfully.";
            success = true;
        }

        await _storage.UpsertAgentRuntimeStateAsync(nextState, cancellationToken);
        _logger?.Log(success ? LogLevel.Information : LogLevel.Warning,
            "bot_arm_result",
            message,
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["state"] = nextState.LifecycleState.ToString(),
                ["armed"] = nextState.IsArmed.ToString()
            });

        return new BotOrchestrationResult(success, nextState, message);
    }

    public async Task<BotOrchestrationResult> DisarmBotAsync(BotProfile profile, CancellationToken cancellationToken = default)
    {
        var nextState = BuildState(profile.BotId, AgentLifecycleState.Stopped, isArmed: false, null);
        await _storage.UpsertAgentRuntimeStateAsync(nextState, cancellationToken);

        const string message = "Bot disarmed successfully.";
        _logger?.Log(LogLevel.Information,
            "bot_disarm_result",
            message,
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["state"] = nextState.LifecycleState.ToString(),
                ["armed"] = nextState.IsArmed.ToString()
            });

        return new BotOrchestrationResult(true, nextState, message);
    }

    private static AgentRuntimeState BuildState(string botId, AgentLifecycleState lifecycle, bool isArmed, string? lastErrorCode)
    {
        return new AgentRuntimeState(
            BotId: botId,
            LifecycleState: lifecycle,
            IsArmed: isArmed,
            LastErrorCode: lastErrorCode,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }
}

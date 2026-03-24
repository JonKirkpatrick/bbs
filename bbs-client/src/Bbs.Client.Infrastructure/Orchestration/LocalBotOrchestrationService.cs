using System;
using System.IO;
using System.Net.Sockets;
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

        try
        {
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
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            var failureState = BuildState(profile.BotId, AgentLifecycleState.Error, isArmed: false, errorCode);
            var failureMessage = $"Bot arm failed due to runtime error ({errorCode}). You can retry without restarting the app.";

            _logger?.Log(LogLevel.Warning,
                "bot_arm_runtime_error",
                failureMessage,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["bot_id"] = profile.BotId,
                    ["error_code"] = errorCode,
                    ["exception_type"] = ex.GetType().Name
                });

            return new BotOrchestrationResult(false, failureState, failureMessage);
        }
    }

    public async Task<BotOrchestrationResult> DisarmBotAsync(BotProfile profile, CancellationToken cancellationToken = default)
    {
        var nextState = BuildState(profile.BotId, AgentLifecycleState.Stopped, isArmed: false, null);
        try
        {
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
        catch (Exception ex)
        {
            var errorCode = MapExceptionToErrorCode(ex);
            var failureState = BuildState(profile.BotId, AgentLifecycleState.Error, isArmed: false, errorCode);
            var failureMessage = $"Bot disarm failed due to runtime error ({errorCode}). You can retry without restarting the app.";

            _logger?.Log(LogLevel.Warning,
                "bot_disarm_runtime_error",
                failureMessage,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["bot_id"] = profile.BotId,
                    ["error_code"] = errorCode,
                    ["exception_type"] = ex.GetType().Name
                });

            return new BotOrchestrationResult(false, failureState, failureMessage);
        }
    }

    private static string MapExceptionToErrorCode(Exception ex)
    {
        return ex switch
        {
            InvalidOperationException => "stale_process_handle",
            SocketException socketException => $"socket_{socketException.SocketErrorCode}".ToLowerInvariant(),
            OperationCanceledException => "operation_canceled",
            _ => "runtime_state_persist_failed"
        };
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

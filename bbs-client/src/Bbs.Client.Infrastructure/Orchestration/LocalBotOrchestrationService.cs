using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Orchestration;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.Infrastructure.Orchestration;

public sealed class LocalBotOrchestrationService : IBotOrchestrationService, IDisposable
{
    private const string AgentLaunchPathMetadataKey = "agent.launch_path";
    private const int AgentSocketReadyTimeoutMs = 3000;

    private readonly IClientStorage _storage;
    private readonly IClientLogger? _logger;
    private readonly object _processGate = new();
    private readonly Dictionary<string, ManagedPair> _managedPairs = new(StringComparer.OrdinalIgnoreCase);

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
        ManagedPair? startedPair = null;

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
        else if (!TryStartManagedPair(profile, out startedPair, out var launchErrorCode, out var launchErrorMessage))
        {
            nextState = BuildState(profile.BotId, AgentLifecycleState.Error, isArmed: false, launchErrorCode);
            message = launchErrorMessage;
            success = false;
        }
        else
        {
            nextState = BuildState(profile.BotId, AgentLifecycleState.Idle, isArmed: true, null);
            message = "Bot armed successfully; agent+bot pair started.";
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
            if (startedPair is not null)
            {
                StopManagedPair(profile.BotId);
            }

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
            StopManagedPair(profile.BotId);
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

    public void Dispose()
    {
        List<string> botIds;
        lock (_processGate)
        {
            botIds = new List<string>(_managedPairs.Keys);
        }

        foreach (var botId in botIds)
        {
            StopManagedPair(botId);
        }
    }

    private bool TryStartManagedPair(BotProfile profile, out ManagedPair? pair, out string errorCode, out string errorMessage)
    {
        pair = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        var socketPath = BuildSocketPath(profile.BotId);
        var agentControlSocketPath = socketPath + ".control";
        RemoveStaleSocket(socketPath);
        RemoveStaleSocket(agentControlSocketPath);

        if (!TryStartProcess(
                profile.BotId,
                BuildAgentStartInfo(profile, socketPath),
                "agent",
                out var agentProcess,
                out var agentError))
        {
            errorCode = "agent_launch_failed";
            errorMessage = $"Cannot arm bot: failed to launch bbs-agent ({agentError}).";
            return false;
        }

        if (!WaitForAgentSocket(socketPath, AgentSocketReadyTimeoutMs))
        {
            if (agentProcess.HasExited)
            {
                var exitCode = agentProcess.ExitCode;
                StopProcess(agentProcess, "agent", profile.BotId);
                errorCode = $"agent_process_exited_{exitCode}";
                errorMessage = $"Cannot arm bot: bbs-agent exited before socket became ready (exit {exitCode}).";
                return false;
            }

            StopProcess(agentProcess, "agent", profile.BotId);
            errorCode = "agent_socket_not_ready";
            errorMessage = "Cannot arm bot: bbs-agent did not publish its socket in time.";
            return false;
        }

        if (!TryStartProcess(
                profile.BotId,
                BuildBotStartInfo(profile, socketPath),
                "bot",
                out var botProcess,
                out var botError))
        {
            StopProcess(agentProcess, "agent", profile.BotId);
            errorCode = "bot_launch_failed";
            errorMessage = $"Cannot arm bot: failed to launch bot process ({botError}).";
            return false;
        }

        var createdPair = new ManagedPair(agentProcess, botProcess, socketPath, agentControlSocketPath);

        agentProcess.Exited += (_, _) => _ = HandleManagedProcessExitAsync(profile.BotId, createdPair, "agent", agentProcess);
        botProcess.Exited += (_, _) => _ = HandleManagedProcessExitAsync(profile.BotId, createdPair, "bot", botProcess);

        lock (_processGate)
        {
            if (_managedPairs.TryGetValue(profile.BotId, out var existing))
            {
                _managedPairs.Remove(profile.BotId);
                StopPair(existing, profile.BotId);
            }

                _managedPairs[profile.BotId] = createdPair;
        }

        _logger?.Log(LogLevel.Information,
            "bot_pair_started",
            "Local bot+agent pair started.",
            new Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["socket_path"] = socketPath,
                ["agent_pid"] = agentProcess.Id.ToString(),
                ["bot_pid"] = botProcess.Id.ToString()
            });

        pair = createdPair;
        return true;
    }

    private bool TryStartProcess(string botId, ProcessStartInfo startInfo, string role, out Process process, out string error)
    {
        process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        error = string.Empty;

        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            _logger?.Log(LogLevel.Debug, $"{role}_process_stdout", args.Data,
                new Dictionary<string, string>
                {
                    ["bot_id"] = botId
                });
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            _logger?.Log(LogLevel.Warning, $"{role}_process_stderr", args.Data,
                new Dictionary<string, string>
                {
                    ["bot_id"] = botId
                });
        };

        try
        {
            if (!process.Start())
            {
                process.Dispose();
                error = "process_start_returned_false";
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger?.Log(LogLevel.Information,
                $"{role}_process_started",
                $"Local {role} process started.",
                new Dictionary<string, string>
                {
                    ["bot_id"] = botId,
                    ["role"] = role,
                    ["file_name"] = startInfo.FileName,
                    ["arguments"] = string.Join(' ', startInfo.ArgumentList)
                });

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }

            error = ex.Message;
            return false;
        }
    }

    private ProcessStartInfo BuildAgentStartInfo(BotProfile profile, string socketPath)
    {
        var listenEndpoint = socketPath;
        if (!OperatingSystem.IsWindows())
        {
            listenEndpoint = "unix://" + socketPath;
        }

        if (TryBuildAgentStartInfoFromMetadata(profile.Metadata, listenEndpoint, profile.Name, out var metadataStartInfo))
        {
            return metadataStartInfo;
        }

        if (TryBuildRepoAgentStartInfo(listenEndpoint, profile.Name, out var repoStartInfo))
        {
            return repoStartInfo;
        }

        return BuildProcessStartInfo("bbs-agent", BuildAgentArgs(listenEndpoint, profile.Name));
    }

    private ProcessStartInfo BuildBotStartInfo(BotProfile profile, string socketPath)
    {
        var botArgs = BuildBotArgsWithSocket(profile.LaunchArgs, socketPath);
        return BuildProcessStartInfo(profile.LaunchPath.Trim(), botArgs);
    }

    private ProcessStartInfo BuildProcessStartInfo(string launchPathOrCommand, IReadOnlyList<string> args, string? workingDirectoryOverride = null)
    {
        var candidate = launchPathOrCommand.Trim();
        var extension = Path.GetExtension(candidate).ToLowerInvariant();

        string executable;
        var arguments = new List<string>();

        if (extension == ".py")
        {
            executable = OperatingSystem.IsWindows() ? "python" : "python3";
            arguments.Add(candidate);
        }
        else if (extension == ".sh")
        {
            executable = OperatingSystem.IsWindows() ? "sh" : "/bin/sh";
            arguments.Add(candidate);
        }
        else
        {
            executable = candidate;
        }

        foreach (var arg in args)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                arguments.Add(arg);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(candidate, workingDirectoryOverride)
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static string ResolveAgentLaunchPath(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue(AgentLaunchPathMetadataKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return "bbs-agent";
    }

    private bool TryBuildAgentStartInfoFromMetadata(IReadOnlyDictionary<string, string> metadata, string listenEndpoint, string botName, out ProcessStartInfo startInfo)
    {
        startInfo = null!;
        var candidate = ResolveAgentLaunchPath(metadata);
        if (string.Equals(candidate, "bbs-agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        startInfo = BuildProcessStartInfo(candidate, BuildAgentArgs(listenEndpoint, botName));
        return true;
    }

    private static bool TryBuildRepoAgentStartInfo(string listenEndpoint, string botName, out ProcessStartInfo startInfo)
    {
        startInfo = null!;
        var repoRoot = FindRepoRootWithAgentSource(AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return false;
        }

        var args = new List<string> { "run", "./cmd/bbs-agent" };
        args.AddRange(BuildAgentArgs(listenEndpoint, botName));
        startInfo = BuildProcessStartInfoStatic("go", args, repoRoot);
        return true;
    }

    private static string? FindRepoRootWithAgentSource(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var goMod = Path.Combine(current.FullName, "go.mod");
            var agentMain = Path.Combine(current.FullName, "cmd", "bbs-agent", "main.go");
            if (File.Exists(goMod) && File.Exists(agentMain))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildAgentArgs(string listenEndpoint, string botName)
    {
        var registerName = SanitizeAgentRegisterName(botName);
        var args = new List<string>
        {
            "--listen",
            listenEndpoint
        };

        if (!string.IsNullOrWhiteSpace(registerName))
        {
            args.Add("--name");
            args.Add(registerName);
        }

        return args;
    }

    private static string SanitizeAgentRegisterName(string botName)
    {
        if (string.IsNullOrWhiteSpace(botName))
        {
            return "bot";
        }

        var parts = botName
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return "bot";
        }

        return string.Join("_", parts);
    }

    private static IReadOnlyList<string> BuildBotArgsWithSocket(IReadOnlyList<string> sourceArgs, string socketPath)
    {
        var args = new List<string>();

        for (var i = 0; i < sourceArgs.Count; i++)
        {
            var current = sourceArgs[i];
            if (string.IsNullOrWhiteSpace(current))
            {
                continue;
            }

            var trimmed = current.Trim();
            if (string.Equals(trimmed, "--socket", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < sourceArgs.Count)
                {
                    i++;
                }
                continue;
            }

            if (trimmed.StartsWith("--socket=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            args.Add(trimmed);
        }

        args.Add("--socket");
        args.Add(socketPath);
        return args;
    }

    private static string BuildSocketPath(string botId)
    {
        var safe = new StringBuilder(botId.Length);
        foreach (var ch in botId)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                safe.Append(ch);
            }
            else
            {
                safe.Append('_');
            }
        }

        if (safe.Length == 0)
        {
            safe.Append("bot");
        }

        return Path.Combine(Path.GetTempPath(), $"bbs-agent-{safe}.sock");
    }

    private static bool WaitForAgentSocket(string socketPath, int timeoutMs)
    {
        if (OperatingSystem.IsWindows())
        {
            Thread.Sleep(200);
            return true;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow <= deadline)
        {
            if (File.Exists(socketPath))
            {
                return true;
            }
            Thread.Sleep(25);
        }

        return false;
    }

    private static void RemoveStaleSocket(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string ResolveWorkingDirectory(string launchPath, string? workingDirectoryOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectoryOverride) && Directory.Exists(workingDirectoryOverride))
        {
            return workingDirectoryOverride;
        }

        var directory = Path.GetDirectoryName(launchPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            return directory;
        }

        return Environment.CurrentDirectory;
    }

    private static ProcessStartInfo BuildProcessStartInfoStatic(string launchPathOrCommand, IReadOnlyList<string> args, string? workingDirectoryOverride)
    {
        var candidate = launchPathOrCommand.Trim();
        var extension = Path.GetExtension(candidate).ToLowerInvariant();

        string executable;
        var arguments = new List<string>();

        if (extension == ".py")
        {
            executable = OperatingSystem.IsWindows() ? "python" : "python3";
            arguments.Add(candidate);
        }
        else if (extension == ".sh")
        {
            executable = OperatingSystem.IsWindows() ? "sh" : "/bin/sh";
            arguments.Add(candidate);
        }
        else
        {
            executable = candidate;
        }

        foreach (var arg in args)
        {
            if (!string.IsNullOrWhiteSpace(arg))
            {
                arguments.Add(arg);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ResolveWorkingDirectory(candidate, workingDirectoryOverride)
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private async Task HandleManagedProcessExitAsync(string botId, ManagedPair expectedPair, string role, Process process)
    {
        ManagedPair? removedPair = null;
        lock (_processGate)
        {
            if (!_managedPairs.TryGetValue(botId, out var current) || !ReferenceEquals(current, expectedPair))
            {
                return;
            }

            _managedPairs.Remove(botId);
            removedPair = current;
        }

        var exitCode = process.ExitCode;
        if (removedPair is not null)
        {
            StopPair(removedPair, botId);
        }

        _logger?.Log(LogLevel.Warning,
            $"{role}_process_exited",
            $"Local {role} process exited while armed pair was active.",
            new Dictionary<string, string>
            {
                ["bot_id"] = botId,
                ["role"] = role,
                ["exit_code"] = exitCode.ToString()
            });

        var currentState = await _storage.GetAgentRuntimeStateAsync(botId);
        if (currentState is null || !currentState.IsArmed)
        {
            return;
        }

        var exitedState = BuildState(botId, AgentLifecycleState.Error, isArmed: false, $"{role}_process_exited_{exitCode}");
        try
        {
            await _storage.UpsertAgentRuntimeStateAsync(exitedState);
        }
        catch (Exception ex)
        {
            _logger?.Log(LogLevel.Warning,
                "paired_process_exit_persist_failed",
                "Failed to persist runtime state after paired process exit.",
                new Dictionary<string, string>
                {
                    ["bot_id"] = botId,
                    ["error"] = ex.Message
                });
        }
    }

    private void StopManagedPair(string botId)
    {
        ManagedPair? pair;
        lock (_processGate)
        {
            if (!_managedPairs.TryGetValue(botId, out pair))
            {
                return;
            }

            _managedPairs.Remove(botId);
        }

        StopPair(pair, botId);
    }

    private void StopPair(ManagedPair pair, string botId)
    {
        StopProcess(pair.BotProcess, "bot", botId);
        StopProcess(pair.AgentProcess, "agent", botId);
        RemoveStaleSocket(pair.SocketPath);
        RemoveStaleSocket(pair.ControlSocketPath);

        _logger?.Log(LogLevel.Information,
            "bot_pair_stopped",
            "Local bot+agent pair stopped.",
            new Dictionary<string, string>
            {
                ["bot_id"] = botId,
                ["socket_path"] = pair.SocketPath
            });
    }

    private static void StopProcess(Process process, string role, string botId)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed record ManagedPair(Process AgentProcess, Process BotProcess, string SocketPath, string ControlSocketPath);

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

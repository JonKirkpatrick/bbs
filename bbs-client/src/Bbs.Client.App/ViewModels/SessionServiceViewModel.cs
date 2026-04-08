using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel managing active bot sessions collection and session lifecycle state.
/// Extracted from MainWindowViewModel to reduce complexity and improve testability.
/// </summary>
public sealed class SessionServiceViewModel : ViewModelBase
{
    private const int DeployHandshakeTimeoutMs = 3000;

    private readonly BotServiceViewModel _botService;
    private readonly IClientLogger _logger;

    public ObservableCollection<ActiveBotSessionItem> ActiveBotSessions { get; } = new();

    private readonly object _deployConnectionLock = new();
    private readonly object _activeAccessCacheLock = new();
    private readonly HashSet<(string BotId, string SessionId)> _activeDeployConnections = new();
    private readonly Dictionary<(string BotId, string SessionId), (string RuntimeBotId, string RuntimeBotName, string ServerId, ServerAccessMetadata Access)> _activeSessionsByBotAndSession = new();

    public SessionServiceViewModel(BotServiceViewModel botService, IClientLogger logger)
    {
        _botService = botService;
        _logger = logger;
    }

    public bool HasActiveBotSessions => ActiveBotSessions.Count > 0;

    public bool ShowActiveBotSessionsEmpty => !HasActiveBotSessions;

    /// <summary>
    /// Clears the active sessions collection and notifies UI of changes.
    /// </summary>
    public void ClearSessions()
    {
        ActiveBotSessions.Clear();
        OnPropertyChanged(nameof(HasActiveBotSessions));
        OnPropertyChanged(nameof(ShowActiveBotSessionsEmpty));
    }

    /// <summary>
    /// Notifies that the session collection has changed and computed properties should be refreshed.
    /// </summary>
    public void NotifySessionsChanged()
    {
        OnPropertyChanged(nameof(HasActiveBotSessions));
        OnPropertyChanged(nameof(ShowActiveBotSessionsEmpty));
    }

    public void AddActiveDeployConnection(string botId, string sessionId)
    {
        lock (_deployConnectionLock)
        {
            _activeDeployConnections.Add((botId, sessionId));
        }
    }

    public void RemoveActiveDeployConnection(string botId, string sessionId)
    {
        lock (_deployConnectionLock)
        {
            _activeDeployConnections.Remove((botId, sessionId));
        }
    }

    public IReadOnlyList<(string BotId, string SessionId)> GetActiveDeployConnectionsForBot(string botId)
    {
        lock (_deployConnectionLock)
        {
            return _activeDeployConnections
                .Where(s => string.Equals(s.BotId, botId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<(string BotId, string SessionId)> GetAllActiveDeployConnections()
    {
        lock (_deployConnectionLock)
        {
            return _activeDeployConnections
                .OrderBy(s => s.BotId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void SetActiveServerAccess(string botId, string sessionId, string runtimeBotId, string runtimeBotName, string serverId, string ownerToken, string dashboardEndpoint)
    {
        var access = string.IsNullOrWhiteSpace(ownerToken)
            ? ServerAccessMetadata.Invalid("Owner token is not available for this runtime session yet.")
            : new ServerAccessMetadata(
                IsValid: true,
                OwnerToken: ownerToken,
                DashboardEndpoint: dashboardEndpoint,
                StatusMessage: "Server access metadata loaded from active runtime session.",
                Source: "active-session-cache");

        lock (_activeAccessCacheLock)
        {
            _activeSessionsByBotAndSession[(botId, sessionId)] = (runtimeBotId, runtimeBotName, serverId, access);
        }
    }

    public void ClearActiveServerAccess(string botId, string sessionId)
    {
        lock (_activeAccessCacheLock)
        {
            _activeSessionsByBotAndSession.Remove((botId, sessionId));
        }
    }

    public bool TryGetActiveServerAccess(string botId, string? targetServerId, out string serverId, out ServerAccessMetadata access)
    {
        lock (_activeAccessCacheLock)
        {
            if (!string.IsNullOrWhiteSpace(targetServerId))
            {
                foreach (var kvp in _activeSessionsByBotAndSession)
                {
                    if (kvp.Key.BotId == botId && kvp.Value.ServerId == targetServerId)
                    {
                        serverId = kvp.Value.ServerId;
                        access = kvp.Value.Access;
                        return true;
                    }
                }
            }
            else
            {
                foreach (var kvp in _activeSessionsByBotAndSession)
                {
                    if (kvp.Key.BotId == botId)
                    {
                        serverId = kvp.Value.ServerId;
                        access = kvp.Value.Access;
                        return true;
                    }
                }
            }
        }

        serverId = string.Empty;
        access = ServerAccessMetadata.Invalid("No active session metadata cached.");
        return false;
    }

    public bool TryGetRuntimeSession(string sourceBotId, string sessionId, out string runtimeBotId, out string runtimeBotName, out string serverId)
    {
        lock (_activeAccessCacheLock)
        {
            if (_activeSessionsByBotAndSession.TryGetValue((sourceBotId, sessionId), out var session))
            {
                runtimeBotId = session.RuntimeBotId;
                runtimeBotName = session.RuntimeBotName;
                serverId = session.ServerId;
                return true;
            }
        }

        runtimeBotId = string.Empty;
        runtimeBotName = string.Empty;
        serverId = string.Empty;
        return false;
    }

    public void PruneStaleActiveSessionCaches(Func<string, bool> controlSocketExists)
    {
        List<(string BotId, string SessionId)> stale;

        lock (_activeAccessCacheLock)
        {
            stale = _activeSessionsByBotAndSession
                .Where(s => !controlSocketExists(s.Value.RuntimeBotId))
                .Select(s => s.Key)
                .ToList();
        }

        foreach (var session in stale)
        {
            RemoveActiveDeployConnection(session.BotId, session.SessionId);
            ClearActiveServerAccess(session.BotId, session.SessionId);
        }
    }

    public void DisconnectActiveDeploymentConnection(string botId, string sessionId, bool sendQuit)
    {
        if (sendQuit)
        {
            TrySendQuitSession(botId, sessionId);
        }

        RemoveActiveDeployConnection(botId, sessionId);
        ClearActiveServerAccess(botId, sessionId);
        NotifySessionsChanged();
    }

    public void DisconnectAllActiveDeploymentConnectionsForBot(string botId, bool sendQuit)
    {
        var sessionsToRemove = GetActiveDeployConnectionsForBot(botId)
            .Select(session => session.SessionId)
            .ToList();

        foreach (var sessionId in sessionsToRemove)
        {
            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit);
        }
    }

    public void DisconnectAllActiveDeploymentConnections(bool sendQuit)
    {
        var sessionsToRemove = GetAllActiveDeployConnections();
        foreach (var session in sessionsToRemove)
        {
            DisconnectActiveDeploymentConnection(session.BotId, session.SessionId, sendQuit);
        }
    }

    public IReadOnlyList<(BotProfile Profile, AgentRuntimeState? RuntimeState)> BuildDisplayBotEntries(
        IClientStorage storage,
        Func<string, bool> hasActiveDeployConnection)
    {
        var profiles = storage.ListBotProfilesAsync().GetAwaiter().GetResult();

        PruneStaleActiveSessionCaches(runtimeBotId => File.Exists(DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId)));

        var entries = new List<(BotProfile Profile, AgentRuntimeState? RuntimeState)>();
        foreach (var profile in profiles)
        {
            if (IsRuntimeInstanceProfile(profile))
            {
                continue;
            }

            var runtimeState = storage.GetAgentRuntimeStateAsync(profile.BotId).GetAwaiter().GetResult();
            if (runtimeState is not null &&
                runtimeState.LifecycleState == AgentLifecycleState.ActiveSession &&
                !hasActiveDeployConnection(profile.BotId))
            {
                runtimeState = new AgentRuntimeState(
                    BotId: runtimeState.BotId,
                    LifecycleState: AgentLifecycleState.Idle,
                    IsAttached: false,
                    LastErrorCode: null,
                    UpdatedAtUtc: DateTimeOffset.UtcNow);
                storage.UpsertAgentRuntimeStateAsync(runtimeState).GetAwaiter().GetResult();
            }

            entries.Add((profile, runtimeState));
        }

        return entries;
    }

    public void RefreshActiveBotSessionsProjection(
        BotSummaryItem? selectedBot,
        ServerSummaryItem? selectedServer,
        IEnumerable<ServerSummaryItem> servers,
        IEnumerable<ServerArenaItem> serverArenaEntries,
        int botTcpDefaultPort)
    {
        PruneStaleActiveSessionCaches(runtimeBotId => File.Exists(DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId)));

        ClearSessions();

        if (selectedBot is null || string.IsNullOrWhiteSpace(selectedBot.BotId))
        {
            return;
        }

        ReconcileActiveSessionsFromRuntimeSockets(selectedBot, servers, botTcpDefaultPort);

        var sessions = GetActiveDeployConnectionsForBot(selectedBot.BotId);
        var serverList = servers as IReadOnlyList<ServerSummaryItem> ?? servers.ToList();
        var arenaList = serverArenaEntries as IReadOnlyList<ServerArenaItem> ?? serverArenaEntries.ToList();

        foreach (var session in sessions)
        {
            var serverId = string.Empty;
            var runtimeBotId = session.BotId;
            var runtimeBotName = session.BotId;
            var access = ServerAccessMetadata.Invalid("Owner token is not available for this server yet.");

            if (TryGetRuntimeSession(session.BotId, session.SessionId, out var cachedRuntimeBotId, out var cachedRuntimeBotName, out var cachedServerId))
            {
                runtimeBotId = cachedRuntimeBotId;
                runtimeBotName = cachedRuntimeBotName;
                serverId = cachedServerId;
                if (TryGetActiveServerAccess(session.BotId, cachedServerId, out var cachedAccessServerId, out var cachedAccess))
                {
                    serverId = cachedAccessServerId;
                    access = cachedAccess;
                }
            }

            var serverName = ResolveServerName(serverId, serverList);
            var arenaOptions = BuildArenaOptionsForServer(serverId, selectedServer, arenaList);

            ActiveBotSessions.Add(new ActiveBotSessionItem
            {
                RuntimeBotId = runtimeBotId,
                SessionId = session.SessionId,
                ServerId = serverId,
                ServerName = string.IsNullOrWhiteSpace(runtimeBotName) ? serverName : $"{serverName} ({runtimeBotName})",
                OwnerTokenMasked = access.IsValid ? MainWindowViewModelHelpers.MaskToken(access.OwnerToken) : "-",
                ArenaOptions = arenaOptions,
                JoinCommand = new RelayCommand(() => ExecuteSessionJoin(session.BotId, session.SessionId)),
                LeaveCommand = new RelayCommand(() => ExecuteSessionLeave(session.BotId, session.SessionId)),
                QuitCommand = new RelayCommand(() => ExecuteSessionQuit(session.BotId, session.SessionId))
            });
        }

        NotifySessionsChanged();
    }

    public void RefreshActiveSessionArenaOptions(ServerSummaryItem? selectedServer, IEnumerable<ServerArenaItem> serverArenaEntries)
    {
        if (ActiveBotSessions.Count == 0)
        {
            return;
        }

        var arenaList = serverArenaEntries as IReadOnlyList<ServerArenaItem> ?? serverArenaEntries.ToList();

        foreach (var session in ActiveBotSessions)
        {
            var selectedArenaId = session.SelectedArena?.ArenaId;
            var options = BuildArenaOptionsForServer(session.ServerId, selectedServer, arenaList);

            session.ArenaOptions.Clear();
            foreach (var option in options)
            {
                session.ArenaOptions.Add(option);
            }

            if (selectedArenaId is > 0)
            {
                session.SelectedArena = session.ArenaOptions.FirstOrDefault(x => x.ArenaId == selectedArenaId);
            }
            else if (session.ArenaOptions.Count == 1)
            {
                session.SelectedArena = session.ArenaOptions[0];
            }
            else if (session.SelectedArena is not null && session.ArenaOptions.Count == 0)
            {
                session.SelectedArena = null;
            }
        }
    }

    public string ResolveServerIdFromAgentTarget(string serverTarget, IEnumerable<ServerSummaryItem> servers, int botTcpDefaultPort)
    {
        if (!TryParseServerTarget(serverTarget, out var targetHost, out var targetPort))
        {
            return string.Empty;
        }

        var normalizedTarget = targetPort > 0
            ? BuildServerHostPort(targetHost, targetPort)
            : targetHost;

        var serverList = servers as IReadOnlyList<ServerSummaryItem> ?? servers.ToList();
        var hostOnlyMatches = new List<ServerSummaryItem>();
        var botPortMatches = new List<ServerSummaryItem>();

        foreach (var server in serverList)
        {
            if (string.Equals(server.ServerId, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return server.ServerId;
            }

            if (string.Equals(BuildServerHostPort(server.Host, server.Port), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return server.ServerId;
            }

            var serverHostCandidates = DeploymentTransportHelpers.BuildHostCandidates(server.Host);
            var hostMatch = serverHostCandidates.Any(candidate =>
                string.Equals(candidate, targetHost, StringComparison.OrdinalIgnoreCase));

            if (!hostMatch)
            {
                continue;
            }

            hostOnlyMatches.Add(server);

            if (targetPort > 0)
            {
                var expectedBotPort = botTcpDefaultPort;
                if (server.Metadata.TryGetValue("bot_port", out var rawBotPort) &&
                    int.TryParse(rawBotPort, out var parsedBotPort) &&
                    parsedBotPort is > 0 and <= 65535)
                {
                    expectedBotPort = parsedBotPort;
                }

                if (expectedBotPort == targetPort)
                {
                    botPortMatches.Add(server);
                }
            }
        }

        if (botPortMatches.Count == 1)
        {
            return botPortMatches[0].ServerId;
        }

        if (hostOnlyMatches.Count == 1)
        {
            return hostOnlyMatches[0].ServerId;
        }

        return string.Empty;
    }

    public ObservableCollection<ServerArenaOptionItem> BuildArenaOptionsForServer(string serverId, ServerSummaryItem? selectedServer, IEnumerable<ServerArenaItem> serverArenaEntries)
    {
        var options = new ObservableCollection<ServerArenaOptionItem>();
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return options;
        }

        if (selectedServer is null || !string.Equals(selectedServer.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        foreach (var arena in serverArenaEntries)
        {
            options.Add(new ServerArenaOptionItem($"#{arena.ArenaId} - {arena.Game}", arena.ArenaId));
        }

        return options;
    }

    private void ExecuteSessionJoin(string botId, string sessionId)
    {
        var item = ActiveBotSessions.FirstOrDefault(x => x.SessionId == sessionId);
        if (item is null)
        {
            return;
        }

        if (!TryResolveRuntimeSessionForAction(botId, sessionId, "JOIN", out var runtimeBotId))
        {
            return;
        }

        if (item.SelectedArena is null || item.SelectedArena.ArenaId <= 0)
        {
            _botService.BotEditorMessage = "Select an arena before JOIN.";
            return;
        }

        if (!int.TryParse(item.JoinHandicapPercent.Trim(), out var handicapPercent))
        {
            _botService.BotEditorMessage = "JOIN handicap must be an integer.";
            return;
        }

        if (!TrySendSessionControlRequest(
                runtimeBotId,
                "join_session",
                new Dictionary<string, object>
                {
                    ["arena_id"] = item.SelectedArena.ArenaId,
                    ["handicap_percent"] = handicapPercent
                },
                "JOIN",
                out var failureMessage))
        {
            _botService.BotEditorMessage = failureMessage;
            return;
        }

        _botService.BotEditorMessage = $"JOIN requested for session {sessionId}.";
    }

    private void ExecuteSessionLeave(string botId, string sessionId)
    {
        if (!TryResolveRuntimeSessionForAction(botId, sessionId, "LEAVE", out var runtimeBotId))
        {
            return;
        }

        if (!TrySendSessionControlRequest(
                runtimeBotId,
                "leave_session",
                new Dictionary<string, object>(),
                "LEAVE",
                out var failureMessage))
        {
            _botService.BotEditorMessage = failureMessage;
            return;
        }

        _botService.BotEditorMessage = $"LEAVE requested for session {sessionId}.";
    }

    private void ExecuteSessionQuit(string botId, string sessionId)
    {
        if (!TryGetRuntimeSession(botId, sessionId, out var runtimeBotId, out _, out _))
        {
            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit: false);
            _botService.BotEditorMessage = $"Removed stale session {sessionId}.";
            return;
        }

        if (!TrySendSessionControlRequest(
                runtimeBotId,
                "quit_session",
                new Dictionary<string, object>(),
                "QUIT",
                out var failureMessage))
        {
            _botService.BotEditorMessage = failureMessage;
            return;
        }

        DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit: false);
        _botService.BotEditorMessage = $"QUIT requested for session {sessionId}.";
    }

    private bool TryResolveRuntimeSessionForAction(string botId, string sessionId, string actionLabel, out string runtimeBotId)
    {
        if (TryGetRuntimeSession(botId, sessionId, out runtimeBotId, out _, out _))
        {
            return true;
        }

        runtimeBotId = string.Empty;
        _botService.BotEditorMessage = $"{actionLabel} failed: runtime session mapping not found.";
        return false;
    }

    private bool TrySendSessionControlRequest(
        string runtimeBotId,
        string messageType,
        IReadOnlyDictionary<string, object> payload,
        string actionLabel,
        out string failureMessage)
    {
        try
        {
            var reply = DeploymentTransportHelpers.SendAgentControlRequest(
                DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId),
                messageType,
                payload,
                DeployHandshakeTimeoutMs);

            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                failureMessage = $"{actionLabel} failed: {reply.Message}";
                return false;
            }

            failureMessage = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            failureMessage = $"{actionLabel} failed: {ex.Message}";
            return false;
        }
    }

    private void TrySendQuitSession(string sourceBotId, string sessionId)
    {
        try
        {
            if (!TryGetRuntimeSession(sourceBotId, sessionId, out var runtimeBotId, out _, out _))
            {
                return;
            }

            var controlSocketPath = DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId);
            if (!File.Exists(controlSocketPath))
            {
                return;
            }

            var reply = DeploymentTransportHelpers.SendAgentControlRequest(controlSocketPath, "quit_session", new Dictionary<string, object>(), DeployHandshakeTimeoutMs);
            if (string.Equals(reply.Type, "control_error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log(LogLevel.Warning, "session_quit_control_error", "Failed to quit active session via control socket.",
                    new Dictionary<string, string>
                    {
                        ["bot_id"] = sourceBotId,
                        ["runtime_bot_id"] = runtimeBotId,
                        ["session_id"] = sessionId,
                        ["message"] = reply.Message
                    });
            }
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "session_quit_failed", "Failed to quit active session via control socket.",
                new Dictionary<string, string>
                {
                    ["bot_id"] = sourceBotId,
                    ["session_id"] = sessionId,
                    ["error"] = ex.GetType().Name
                });
        }
    }

    private void ReconcileActiveSessionsFromRuntimeSockets(BotSummaryItem sourceBot, IEnumerable<ServerSummaryItem> servers, int botTcpDefaultPort)
    {
        var sourceBotId = sourceBot.BotId;
        var sourceBotName = sourceBot.Name;
        var runtimeIdPrefix = sourceBotId + "-";
        var socketPrefix = "bbs-agent-" + runtimeIdPrefix;
        var socketSuffix = ".sock.control";

        string[] controlSockets;
        try
        {
            controlSockets = Directory.GetFiles(Path.GetTempPath(), socketPrefix + "*" + socketSuffix);
        }
        catch
        {
            return;
        }

        var serverList = servers as IReadOnlyList<ServerSummaryItem> ?? servers.ToList();

        foreach (var controlSocketPath in controlSockets)
        {
            var fileName = Path.GetFileName(controlSocketPath);
            if (!fileName.StartsWith("bbs-agent-", StringComparison.Ordinal) ||
                !fileName.EndsWith(socketSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var runtimeBotId = fileName.Substring("bbs-agent-".Length, fileName.Length - "bbs-agent-".Length - socketSuffix.Length);
            if (!runtimeBotId.StartsWith(runtimeIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var accessReply = ReadRuntimeAccess(runtimeBotId);
            if (accessReply is null)
            {
                continue;
            }

            var activeSessionId = NormalizeActiveSessionId(accessReply.SessionId);
            if (string.IsNullOrWhiteSpace(activeSessionId))
            {
                continue;
            }

            var runtimeSuffix = runtimeBotId[runtimeIdPrefix.Length..];
            if (string.IsNullOrWhiteSpace(runtimeSuffix))
            {
                continue;
            }

            var sessionId = activeSessionId;
            var runtimeBotName = sourceBotName + "-" + runtimeSuffix;

            AddActiveDeployConnection(sourceBotId, sessionId);

            var serverId = ResolveServerIdFromAgentTarget(accessReply.Server, serverList, botTcpDefaultPort);
            SetActiveServerAccess(
                sourceBotId,
                sessionId,
                runtimeBotId,
                runtimeBotName,
                serverId,
                accessReply.OwnerToken,
                accessReply.DashboardEndpoint);
        }
    }

    private AgentControlResponse? ReadRuntimeAccess(string runtimeBotId)
    {
        try
        {
            var controlSocketPath = DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId);
            if (!File.Exists(controlSocketPath))
            {
                return null;
            }

            var reply = DeploymentTransportHelpers.SendAgentControlRequest(controlSocketPath, "server_access", new Dictionary<string, object>(), DeployHandshakeTimeoutMs);
            if (!string.Equals(reply.Type, "server_access", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return reply;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRuntimeInstanceProfile(BotProfile profile)
    {
        if (profile.Metadata.TryGetValue("runtime_instance", out var runtimeFlag) &&
            string.Equals(runtimeFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeActiveSessionId(string rawSessionId)
    {
        var trimmed = rawSessionId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (int.TryParse(trimmed, out var numericSessionId) && numericSessionId <= 0)
        {
            return string.Empty;
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static bool TryParseServerTarget(string serverTarget, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var trimmed = serverTarget.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            host = absolute.Host;
            port = absolute.Port;
            return !string.IsNullOrWhiteSpace(host);
        }

        if (Uri.TryCreate("tcp://" + trimmed, UriKind.Absolute, out var implicitUri))
        {
            host = implicitUri.Host;
            port = implicitUri.Port;
            return !string.IsNullOrWhiteSpace(host);
        }

        host = trimmed;
        return true;
    }

    private static string BuildServerHostPort(string host, int port)
    {
        return $"{host}:{port}";
    }

    private static string ResolveServerName(string serverId, IReadOnlyList<ServerSummaryItem> servers)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return "Unknown server";
        }

        var server = servers.FirstOrDefault(item => string.Equals(item.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        return server is null ? serverId : server.Name;
    }
}

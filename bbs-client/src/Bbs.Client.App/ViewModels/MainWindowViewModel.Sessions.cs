using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string ResolveServerName(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return "Unknown server";
        }

        var server = FindServerById(serverId);
        return server is null ? serverId : server.Name;
    }

    private void PruneStaleActiveSessionCaches()
    {
        _sessionService.PruneStaleActiveSessionCaches(runtimeBotId => File.Exists(DeploymentTransportHelpers.BuildAgentControlSocketPath(runtimeBotId)));
    }

    private void SetActiveServerAccess(string botId, string sessionId, string runtimeBotId, string runtimeBotName, string serverId, string ownerToken, string dashboardEndpoint)
    {
        _sessionService.SetActiveServerAccess(botId, sessionId, runtimeBotId, runtimeBotName, serverId, ownerToken, dashboardEndpoint);
    }

    private void ClearActiveServerAccess(string botId, string sessionId)
    {
        _sessionService.ClearActiveServerAccess(botId, sessionId);
    }

    private bool TryGetActiveServerAccess(string botId, string? targetServerId, out string serverId, out ServerAccessMetadata access)
    {
        return _sessionService.TryGetActiveServerAccess(botId, targetServerId, out serverId, out access);
    }

    private bool TryGetRuntimeSession(string sourceBotId, string sessionId, out string runtimeBotId, out string runtimeBotName, out string serverId)
    {
        return _sessionService.TryGetRuntimeSession(sourceBotId, sessionId, out runtimeBotId, out runtimeBotName, out serverId);
    }

    private bool HasActiveDeployConnection(string botId)
    {
        return _sessionService.GetActiveDeployConnectionsForBot(botId).Count > 0;
    }

    private void DisconnectActiveDeploymentConnection(string botId, string sessionId, bool sendQuit)
    {
        if (sendQuit)
        {
            TrySendQuitSession(botId, sessionId);
        }

        _sessionService.RemoveActiveDeployConnection(botId, sessionId);

        ClearActiveServerAccess(botId, sessionId);
        RefreshActiveBotSessionsProjection();
        TriggerServerAccessRefresh();
    }

    private void DisconnectAllActiveDeploymentConnectionsForBot(string botId, bool sendQuit)
    {
        var sessionsToRemove = _sessionService.GetActiveDeployConnectionsForBot(botId)
            .Select(session => session.SessionId)
            .ToList();

        foreach (var sessionId in sessionsToRemove)
        {
            DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit);
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

    private void RefreshActiveBotSessionsProjection()
    {
        PruneStaleActiveSessionCaches();

        var selectedBot = SelectedBot;
        var selectedBotId = selectedBot?.BotId;
        _sessionService.ClearSessions();

        if (selectedBot is null || string.IsNullOrWhiteSpace(selectedBotId))
        {
            return;
        }

        ReconcileActiveSessionsFromRuntimeSockets(selectedBot);

        var sessions = _sessionService.GetActiveDeployConnectionsForBot(selectedBotId);

        foreach (var session in sessions)
        {
            var serverId = string.Empty;
            var runtimeBotId = session.BotId;
            var runtimeBotName = session.BotId;
            var access = ServerAccessMetadata.Invalid("Owner token is not available for this server yet.");
            if (_sessionService.TryGetRuntimeSession(session.BotId, session.SessionId, out var cachedRuntimeBotId, out var cachedRuntimeBotName, out var cachedServerId))
            {
                runtimeBotId = cachedRuntimeBotId;
                runtimeBotName = cachedRuntimeBotName;
                serverId = cachedServerId;
                if (_sessionService.TryGetActiveServerAccess(session.BotId, cachedServerId, out var cachedAccessServerId, out var cachedAccess))
                {
                    serverId = cachedAccessServerId;
                    access = cachedAccess;
                }
            }

            var serverName = ResolveServerName(serverId);
            var arenaOptions = BuildArenaOptionsForServer(serverId);
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

        _sessionService.NotifySessionsChanged();
    }

    private void ReconcileActiveSessionsFromRuntimeSockets(BotSummaryItem sourceBot)
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

            _sessionService.AddActiveDeployConnection(sourceBotId, sessionId);

            var serverId = ResolveServerIdFromAgentTarget(accessReply.Server);
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

    private string ResolveServerIdFromAgentTarget(string serverTarget)
    {
        if (!TryParseServerTarget(serverTarget, out var targetHost, out var targetPort))
        {
            return string.Empty;
        }

        var normalizedTarget = targetPort > 0
            ? BuildServerHostPort(targetHost, targetPort)
            : targetHost;

        var hostOnlyMatches = new List<ServerSummaryItem>();
        var botPortMatches = new List<ServerSummaryItem>();

        foreach (var server in Servers)
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
                var expectedBotPort = BotTcpDefaultPort;
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
        LoadBotsFromStorage();
        SelectedBot = FindBotById(botId);
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

    private ObservableCollection<ServerArenaOptionItem> BuildArenaOptionsForServer(string serverId)
    {
        var options = new ObservableCollection<ServerArenaOptionItem>();
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return options;
        }

        if (!string.Equals(SelectedServer?.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
        {
            return options;
        }

        foreach (var arena in ServerArenaEntries)
        {
            options.Add(new ServerArenaOptionItem($"#{arena.ArenaId} - {arena.Game}", arena.ArenaId));
        }

        return options;
    }

    private void RefreshActiveSessionArenaOptions()
    {
        if (ActiveBotSessions.Count == 0)
        {
            return;
        }

        foreach (var session in ActiveBotSessions)
        {
            var selectedArenaId = session.SelectedArena?.ArenaId;
            var options = BuildArenaOptionsForServer(session.ServerId);

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
}

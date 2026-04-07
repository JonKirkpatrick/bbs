using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel managing active bot sessions collection and session lifecycle state.
/// Extracted from MainWindowViewModel to reduce complexity and improve testability.
/// </summary>
public sealed class SessionServiceViewModel : ViewModelBase
{
    public ObservableCollection<ActiveBotSessionItem> ActiveBotSessions { get; } = new();

    private readonly object _deployConnectionLock = new();
    private readonly object _activeAccessCacheLock = new();
    private readonly HashSet<(string BotId, string SessionId)> _activeDeployConnections = new();
    private readonly Dictionary<(string BotId, string SessionId), (string RuntimeBotId, string RuntimeBotName, string ServerId, ServerAccessMetadata Access)> _activeSessionsByBotAndSession = new();

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
}

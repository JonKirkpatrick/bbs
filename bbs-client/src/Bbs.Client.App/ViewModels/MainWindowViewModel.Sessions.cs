using System.Collections.ObjectModel;
using System.IO;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
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
        _sessionService.DisconnectActiveDeploymentConnection(botId, sessionId, sendQuit);
        RefreshActiveBotSessionsProjection();
        TriggerServerAccessRefresh();
    }

    private void DisconnectAllActiveDeploymentConnectionsForBot(string botId, bool sendQuit)
    {
        _sessionService.DisconnectAllActiveDeploymentConnectionsForBot(botId, sendQuit);
        RefreshActiveBotSessionsProjection();
        TriggerServerAccessRefresh();
    }

    private void RefreshActiveBotSessionsProjection()
    {
        _sessionService.RefreshActiveBotSessionsProjection(SelectedBot, SelectedServer, Servers, ServerArenaEntries, BotTcpDefaultPort);
    }

    private string ResolveServerIdFromAgentTarget(string serverTarget)
    {
        return _sessionService.ResolveServerIdFromAgentTarget(serverTarget, Servers, BotTcpDefaultPort);
    }

    private ObservableCollection<ServerArenaOptionItem> BuildArenaOptionsForServer(string serverId)
    {
        return _sessionService.BuildArenaOptionsForServer(serverId, SelectedServer, ServerArenaEntries);
    }

    private void RefreshActiveSessionArenaOptions()
    {
        _sessionService.RefreshActiveSessionArenaOptions(SelectedServer, ServerArenaEntries);
    }
}
using System.Net.Sockets;

namespace Bbs.Client.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void DeploySelectedBotToSelectedServer()
    {
        if (!CanDeploySelectedBot())
        {
            _botService.BotEditorMessage = "Deploy is only available when a live server is selected.";
            return;
        }

        var bot = SelectedBot;
        var server = SelectedServer;
        if (bot is null)
        {
            _botService.BotEditorMessage = "Select a bot before deploy.";
            return;
        }

        if (server is null)
        {
            _botService.BotEditorMessage = "Deploy requires a selected server.";
            return;
        }

        try
        {
            _deploymentService.DeploySelectedBotToSelectedServer(bot, server);
            LoadBotsFromStorage();
            SelectedBot = FindBotById(bot.BotId);
            RefreshActiveBotSessionsProjection();
            TriggerServerAccessRefresh();
            _botService.BotEditorMessage = _deploymentService.LastDeploymentMessage;
        }
        catch (SocketException socketException)
        {
            HandleOrchestrationException("deploy", bot.BotId, $"socket_{socketException.SocketErrorCode}".ToLowerInvariant(), socketException);
        }
        catch (InvalidOperationException invalidOperationException)
        {
            HandleOrchestrationException("deploy", bot.BotId, "deploy_runtime_unavailable", invalidOperationException);
        }
        catch (Exception ex)
        {
            if (DeploymentTransportHelpers.TryResolveSocketErrorCode(ex, out var socketErrorCode))
            {
                HandleOrchestrationException("deploy", bot.BotId, socketErrorCode, ex);
                return;
            }

            HandleOrchestrationException("deploy", bot.BotId, "deploy_attach_failed", ex);
        }
    }
}

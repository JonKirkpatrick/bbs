using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed record ServerMetadataEntryItem(string Key, string Value);

public sealed record ServerPluginCatalogItem(
    string Name,
    string DisplayName,
    string Version,
    IReadOnlyDictionary<string, string>? PluginMetadata = null)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        PluginMetadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RegisterHandshakeResult(
    string SessionId,
    string OwnerToken,
    string DashboardEndpoint);

public sealed record AgentControlResponse(
    string Type,
    string Id,
    string Message,
    string Server,
    string SessionId,
    string BotId,
    string ControlToken,
    string OwnerToken,
    string DashboardEndpoint,
    string DashboardHost,
    string DashboardPort)
{
    public string BotSecret => ControlToken;
}

public sealed record ServerArenaOptionItem(string Label, int ArenaId);

public sealed class ActiveBotSessionItem : ViewModelBase
{
    private ServerArenaOptionItem? _selectedArena;
    private string _joinHandicapPercent = "0";

    public required string RuntimeBotId { get; init; }
    public required string SessionId { get; init; }
    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string OwnerTokenMasked { get; init; }
    public required ObservableCollection<ServerArenaOptionItem> ArenaOptions { get; init; }
    public required ICommand JoinCommand { get; init; }
    public required ICommand LeaveCommand { get; init; }
    public required ICommand QuitCommand { get; init; }

    public ServerArenaOptionItem? SelectedArena
    {
        get => _selectedArena;
        set
        {
            if (_selectedArena == value)
            {
                return;
            }

            _selectedArena = value;
            OnPropertyChanged();
        }
    }

    public string JoinHandicapPercent
    {
        get => _joinHandicapPercent;
        set
        {
            if (string.Equals(_joinHandicapPercent, value, StringComparison.Ordinal))
            {
                return;
            }

            _joinHandicapPercent = value;
            OnPropertyChanged();
        }
    }
}

public enum WorkspaceContext
{
    Home = 0,
    BotDetails = 1,
    ServerDetails = 2,
    ArenaViewer = 3
}

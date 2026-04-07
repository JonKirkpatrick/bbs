using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class ServerAccessServiceViewModel : ViewModelBase
{
    private bool _isServerAccessLoading;
    private string _serverAccessStatus = "Select a server to load server access metadata.";
    private string _serverAccessOwnerToken = "-";
    private string _serverAccessDashboardEndpoint = "-";
    private string _ownerTokenActionStatus = "Owner-token actions are unavailable until valid server access metadata is loaded.";

    public ServerAccessMetadata ServerAccessMetadata { get; set; } = ServerAccessMetadata.Invalid("No metadata loaded.");

    public bool IsServerAccessLoading
    {
        get => _isServerAccessLoading;
        set
        {
            if (_isServerAccessLoading == value)
            {
                return;
            }

            _isServerAccessLoading = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessStatus
    {
        get => _serverAccessStatus;
        set
        {
            if (_serverAccessStatus == value)
            {
                return;
            }

            _serverAccessStatus = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessOwnerToken
    {
        get => _serverAccessOwnerToken;
        set
        {
            if (_serverAccessOwnerToken == value)
            {
                return;
            }

            _serverAccessOwnerToken = value;
            OnPropertyChanged();
        }
    }

    public string ServerAccessDashboardEndpoint
    {
        get => _serverAccessDashboardEndpoint;
        set
        {
            if (_serverAccessDashboardEndpoint == value)
            {
                return;
            }

            _serverAccessDashboardEndpoint = value;
            OnPropertyChanged();
        }
    }

    public string OwnerTokenActionStatus
    {
        get => _ownerTokenActionStatus;
        set
        {
            if (_ownerTokenActionStatus == value)
            {
                return;
            }

            _ownerTokenActionStatus = value;
            OnPropertyChanged();
        }
    }

    public bool HasValidServerAccess => ServerAccessMetadata.IsValid;
    public bool ShowOwnerTokenActions => HasValidServerAccess;
    public bool ShowOwnerTokenActionsUnavailable => !ShowOwnerTokenActions;
}
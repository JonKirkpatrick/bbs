using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class ServerServiceViewModel : ViewModelBase
{
    private string _serverEditorName = string.Empty;
    private string _serverEditorHost = string.Empty;
    private string _serverEditorPort = "3000";
    private bool _serverEditorUseTls;
    private string _serverEditorMetadata = string.Empty;
    private string _serverEditorMessage = "Fill out the server form and save.";

    public string ServerEditorName
    {
        get => _serverEditorName;
        set
        {
            if (_serverEditorName == value)
            {
                return;
            }

            _serverEditorName = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorHost
    {
        get => _serverEditorHost;
        set
        {
            if (_serverEditorHost == value)
            {
                return;
            }

            _serverEditorHost = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorPort
    {
        get => _serverEditorPort;
        set
        {
            if (_serverEditorPort == value)
            {
                return;
            }

            _serverEditorPort = value;
            OnPropertyChanged();
        }
    }

    public bool ServerEditorUseTls
    {
        get => _serverEditorUseTls;
        set
        {
            if (_serverEditorUseTls == value)
            {
                return;
            }

            _serverEditorUseTls = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMetadata
    {
        get => _serverEditorMetadata;
        set
        {
            if (_serverEditorMetadata == value)
            {
                return;
            }

            _serverEditorMetadata = value;
            OnPropertyChanged();
        }
    }

    public string ServerEditorMessage
    {
        get => _serverEditorMessage;
        set
        {
            if (_serverEditorMessage == value)
            {
                return;
            }

            _serverEditorMessage = value;
            OnPropertyChanged();
        }
    }

    public void PrepareForNewServer()
    {
        ServerEditorName = string.Empty;
        ServerEditorHost = string.Empty;
        ServerEditorPort = "3000";
        ServerEditorUseTls = false;
        ServerEditorMetadata = string.Empty;
        ServerEditorMessage = "Creating a new known server.";
    }

    public void PopulateEditor(ServerSummaryItem server)
    {
        ServerEditorName = server.Name;
        ServerEditorHost = server.Host;
        ServerEditorPort = server.Port.ToString();
        ServerEditorUseTls = server.UseTls;
        ServerEditorMetadata = MainWindowViewModelHelpers.FormatMetadata(server.Metadata);
        ServerEditorMessage = $"Editing known server: {server.Name}";
    }
}

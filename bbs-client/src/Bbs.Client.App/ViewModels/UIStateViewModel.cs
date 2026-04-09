using System.Windows.Input;
using Avalonia.Controls;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Manages pure UI state: workspace context, panel expansion, calculated visibility properties.
/// Independent of business logic; safe to test in isolation.
/// </summary>
public class UIStateViewModel : ViewModelBase
{
    private WorkspaceContext _currentContext = WorkspaceContext.Home;
    private bool _isLeftPanelExpanded = true;
    private bool _isRightPanelExpanded = true;

    public UIStateViewModel()
    {
        ToggleLeftPanelCommand = new RelayCommand(ToggleLeftPanel);
        ToggleRightPanelCommand = new RelayCommand(ToggleRightPanel);
        SetHomeContextCommand = new RelayCommand(SetHomeContext);
    }

    /// <summary>
    /// Current workspace context (Home, BotDetails, ServerDetails, ServerEditor, ArenaViewer).
    /// </summary>
    public WorkspaceContext CurrentContext
    {
        get => _currentContext;
        private set
        {
            if (_currentContext == value)
            {
                return;
            }

            _currentContext = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentContextLabel));
            OnPropertyChanged(nameof(ShowBotEditor));
            OnPropertyChanged(nameof(ShowServerEditor));
            OnPropertyChanged(nameof(ShowServerDetails));
            OnPropertyChanged(nameof(ShowArenaViewer));
        }
    }

    /// <summary>
    /// Whether the left panel (Bots) is visually expanded.
    /// </summary>
    public bool IsLeftPanelExpanded
    {
        get => _isLeftPanelExpanded;
        private set
        {
            if (_isLeftPanelExpanded == value)
            {
                return;
            }

            _isLeftPanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLeftPanelCollapsed));
            OnPropertyChanged(nameof(LeftPanelWidth));
        }
    }

    /// <summary>
    /// Whether the right panel (Servers/Details) is visually expanded.
    /// </summary>
    public bool IsRightPanelExpanded
    {
        get => _isRightPanelExpanded;
        private set
        {
            if (_isRightPanelExpanded == value)
            {
                return;
            }

            _isRightPanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRightPanelCollapsed));
            OnPropertyChanged(nameof(RightPanelWidth));
        }
    }

    /// <summary>
    /// Computed: Left panel is collapsed (opposite of IsLeftPanelExpanded).
    /// </summary>
    public bool IsLeftPanelCollapsed => !IsLeftPanelExpanded;

    /// <summary>
    /// Computed: Right panel is collapsed (opposite of IsRightPanelExpanded).
    /// </summary>
    public bool IsRightPanelCollapsed => !IsRightPanelExpanded;

    /// <summary>
    /// Computed: Left panel width based on expansion state.
    /// Expanded: 280px | Collapsed: 56px (icon-only)
    /// </summary>
    public GridLength LeftPanelWidth => IsLeftPanelExpanded ? new GridLength(280) : new GridLength(56);

    /// <summary>
    /// Computed: Right panel width based on expansion state.
    /// Expanded: 280px | Collapsed: 56px (icon-only)
    /// </summary>
    public GridLength RightPanelWidth => IsRightPanelExpanded ? new GridLength(280) : new GridLength(56);

    /// <summary>
    /// Computed: Whether to show bot editor based on context.
    /// </summary>
    public bool ShowBotEditor => _currentContext == WorkspaceContext.BotDetails;

    /// <summary>
    /// Computed: Whether to show server editor based on context.
    /// </summary>
    public bool ShowServerEditor => _currentContext == WorkspaceContext.ServerEditor;

    /// <summary>
    /// Computed: Whether to show server details (owner token, arenas) based on context.
    /// </summary>
    public bool ShowServerDetails => _currentContext == WorkspaceContext.ServerDetails;

    /// <summary>
    /// Computed: Whether to show arena viewer based on context.
    /// </summary>
    public bool ShowArenaViewer => _currentContext == WorkspaceContext.ArenaViewer;

    /// <summary>
    /// Computed: Human-readable context label for debugging/diagnostics.
    /// </summary>
    public string CurrentContextLabel => $"Context: {_currentContext}";

    /// <summary>
    /// Command: Toggle left panel expansion state.
    /// </summary>
    public ICommand ToggleLeftPanelCommand { get; }

    /// <summary>
    /// Command: Toggle right panel expansion state.
    /// </summary>
    public ICommand ToggleRightPanelCommand { get; }

    /// <summary>
    /// Command: Switch to Home context (no editor visible).
    /// </summary>
    public ICommand SetHomeContextCommand { get; }

    /// <summary>
    /// Switch to a specific workspace context.
    /// Called by MainWindowViewModel when user selects bot/server or navigates.
    /// </summary>
    public void SwitchContext(WorkspaceContext context)
    {
        CurrentContext = context;
    }

    private void ToggleLeftPanel()
    {
        IsLeftPanelExpanded = !IsLeftPanelExpanded;
    }

    private void ToggleRightPanel()
    {
        IsRightPanelExpanded = !IsRightPanelExpanded;
    }

    private void SetHomeContext()
    {
        SwitchContext(WorkspaceContext.Home);
    }
}

using System.Collections.ObjectModel;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel managing active bot sessions collection and session lifecycle state.
/// Extracted from MainWindowViewModel to reduce complexity and improve testability.
/// </summary>
public sealed class SessionServiceViewModel : ViewModelBase
{
    public ObservableCollection<ActiveBotSessionItem> ActiveBotSessions { get; } = new();

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
}

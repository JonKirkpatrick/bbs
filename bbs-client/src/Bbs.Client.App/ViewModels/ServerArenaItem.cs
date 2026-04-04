using System.Windows.Input;

namespace Bbs.Client.App.ViewModels;

public sealed record ServerArenaItem(
    int ArenaId,
    string Game,
    string Status,
    string Players,
    int MoveCount,
    string ViewerUrl,
    string PluginEntryUrl,
    int ViewerWidth,
    int ViewerHeight,
    ICommand WatchCommand);

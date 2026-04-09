using System.Windows.Input;
using Avalonia.Media;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class BotSummaryItem : ViewModelBase
{
    private static readonly IBrush DefaultAccentBrush = new SolidColorBrush(Color.Parse("#0e7a6d"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#fffaf3"));
    private static readonly IBrush AttachedAccentBrush = new SolidColorBrush(Color.Parse("#b7791f"));
    private static readonly IBrush AttachedBackgroundBrush = new SolidColorBrush(Color.Parse("#fff4df"));
    private static readonly IBrush ActiveAccentBrush = new SolidColorBrush(Color.Parse("#2b8a3e"));
    private static readonly IBrush ActiveBackgroundBrush = new SolidColorBrush(Color.Parse("#e8f8ec"));
    private static readonly IBrush ErrorAccentBrush = new SolidColorBrush(Color.Parse("#c92a2a"));
    private static readonly IBrush ErrorBackgroundBrush = new SolidColorBrush(Color.Parse("#fdecec"));

    public required string BotId { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string Status { get; init; }
    public required IBrush AccentBrush { get; init; }
    public required IBrush BackgroundBrush { get; init; }
    public required string LaunchPath { get; init; }
    public string? AvatarImagePath { get; init; }
    public required IReadOnlyList<string> LaunchArgs { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public BotCardVisualState VisualState { get; init; }
    public AgentLifecycleState LifecycleState { get; init; }
    public bool IsAttached { get; init; }
    public bool IsArmed => IsAttached;
    public string? LastErrorCode { get; init; }
    public required ICommand DeployCommand { get; init; }

    public static BotSummaryItem FromProfile(
        BotProfile profile,
        AgentRuntimeState? runtimeState,
        ICommand deployCommand)
    {
        var visualState = BotCardVisualStateRules.Resolve(runtimeState);
        var status = BuildStatusText(runtimeState, visualState);
        var (accentBrush, backgroundBrush) = ResolveBrushes(visualState);

        return new BotSummaryItem
        {
            BotId = profile.BotId,
            Name = profile.Name,
            Summary = "Local profile",
            Status = status,
            AccentBrush = accentBrush,
            BackgroundBrush = backgroundBrush,
            LaunchPath = profile.LaunchPath,
            AvatarImagePath = profile.AvatarImagePath,
            LaunchArgs = profile.LaunchArgs,
            Metadata = profile.Metadata,
            CreatedAtUtc = profile.CreatedAtUtc,
            VisualState = visualState,
            LifecycleState = runtimeState?.LifecycleState ?? AgentLifecycleState.Unknown,
            IsAttached = runtimeState?.IsAttached ?? false,
            LastErrorCode = runtimeState?.LastErrorCode,
            DeployCommand = deployCommand
        };
    }

    public static BotSummaryItem FromProfile(
        BotProfile profile,
        AgentRuntimeState? runtimeState,
        ICommand armCommand,
        ICommand disarmCommand,
        ICommand deployCommand)
    {
        _ = armCommand;
        _ = disarmCommand;
        return FromProfile(profile, runtimeState, deployCommand);
    }

    private static string BuildStatusText(AgentRuntimeState? runtimeState, BotCardVisualState visualState)
    {
        if (runtimeState is null)
        {
            return "Registered";
        }

        return visualState switch
        {
            BotCardVisualState.Attached => "Attached",
            BotCardVisualState.ActiveSession => "Active Session",
            BotCardVisualState.Error => string.IsNullOrWhiteSpace(runtimeState.LastErrorCode)
                ? "Error"
                : $"Error: {runtimeState.LastErrorCode}",
            _ => runtimeState.LifecycleState.ToString()
        };
    }

    private static (IBrush AccentBrush, IBrush BackgroundBrush) ResolveBrushes(BotCardVisualState visualState)
    {
        return visualState switch
        {
            BotCardVisualState.Attached => (AttachedAccentBrush, AttachedBackgroundBrush),
            BotCardVisualState.ActiveSession => (ActiveAccentBrush, ActiveBackgroundBrush),
            BotCardVisualState.Error => (ErrorAccentBrush, ErrorBackgroundBrush),
            _ => (DefaultAccentBrush, DefaultBackgroundBrush)
        };
    }

    public BotProfile ToProfile()
    {
        return BotProfile.Create(
            botId: BotId,
            name: Name,
            launchPath: LaunchPath,
            avatarImagePath: AvatarImagePath,
            launchArgs: LaunchArgs,
            metadata: new Dictionary<string, string>(Metadata),
            createdAtUtc: CreatedAtUtc,
            updatedAtUtc: DateTimeOffset.UtcNow);
    }
}

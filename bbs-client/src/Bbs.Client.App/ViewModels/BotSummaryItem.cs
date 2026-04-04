using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Media;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class BotSummaryItem : ViewModelBase
{
    private static readonly IBrush DefaultAccentBrush = new SolidColorBrush(Color.Parse("#0e7a6d"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#fffaf3"));
    private static readonly IBrush ArmedAccentBrush = new SolidColorBrush(Color.Parse("#b7791f"));
    private static readonly IBrush ArmedBackgroundBrush = new SolidColorBrush(Color.Parse("#fff4df"));
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
    public bool IsArmed { get; init; }
    public string? LastErrorCode { get; init; }
    public required ICommand ArmCommand { get; init; }
    public required ICommand DisarmCommand { get; init; }
    public required ICommand DeployCommand { get; init; }
    public bool CanArm => !IsArmed;
    public bool CanDisarm => IsArmed;

    public bool IsArmedToggle
    {
        // If we CAN disarm, it means we are currently ARMED (Toggle Up)
        get => CanDisarm;
        set
        {
            if (value)
            {
                // User pushed toggle UP -> Run the same command as the "Arm" button
                if (ArmCommand.CanExecute(null)) ArmCommand.Execute(null);
            }
            else
            {
                // User pushed toggle DOWN -> Run the same command as the "Disarm" button
                if (DisarmCommand.CanExecute(null)) DisarmCommand.Execute(null);
            }

            // We use the string "IsArmedToggle" here to tell Avalonia to refresh the knob
            OnPropertyChanged("IsArmedToggle");

            // We also notify these so the "Deploy" button visibility updates
            OnPropertyChanged("CanArm");
            OnPropertyChanged("CanDisarm");
            OnPropertyChanged("IsArmed");
        }
    }

    public static BotSummaryItem FromProfile(
        BotProfile profile,
        AgentRuntimeState? runtimeState,
        ICommand armCommand,
        ICommand disarmCommand,
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
            IsArmed = runtimeState?.IsArmed ?? false,
            LastErrorCode = runtimeState?.LastErrorCode,
            ArmCommand = armCommand,
            DisarmCommand = disarmCommand,
            DeployCommand = deployCommand
        };
    }

    private static string BuildStatusText(AgentRuntimeState? runtimeState, BotCardVisualState visualState)
    {
        if (runtimeState is null)
        {
            return "Registered";
        }

        return visualState switch
        {
            BotCardVisualState.Armed => "Armed",
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
            BotCardVisualState.Armed => (ArmedAccentBrush, ArmedBackgroundBrush),
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

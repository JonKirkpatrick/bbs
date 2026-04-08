using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Bbs.Client.Core.Domain;
using Bbs.Client.Core.Logging;
using Bbs.Client.Core.Storage;

namespace Bbs.Client.App.ViewModels;

public sealed class BotServiceViewModel : ViewModelBase
{
    private IClientStorage _storage;
    private readonly IClientLogger _logger;

    private string _botEditorName = string.Empty;
    private string _botEditorLaunchPath = string.Empty;
    private string _botEditorArgs = string.Empty;
    private string _botEditorMetadata = string.Empty;
    private string _botEditorMessage = "Fill out the bot form and save.";

    public BotServiceViewModel(IClientStorage storage, IClientLogger logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Bots = new ObservableCollection<BotSummaryItem>();
    }

    public ObservableCollection<BotSummaryItem> Bots { get; }

    public void UpdateStorage(IClientStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public string BotEditorName
    {
        get => _botEditorName;
        set
        {
            if (_botEditorName == value)
            {
                return;
            }

            _botEditorName = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorLaunchPath
    {
        get => _botEditorLaunchPath;
        set
        {
            if (_botEditorLaunchPath == value)
            {
                return;
            }

            _botEditorLaunchPath = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorArgs
    {
        get => _botEditorArgs;
        set
        {
            if (_botEditorArgs == value)
            {
                return;
            }

            _botEditorArgs = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorMetadata
    {
        get => _botEditorMetadata;
        set
        {
            if (_botEditorMetadata == value)
            {
                return;
            }

            _botEditorMetadata = value;
            OnPropertyChanged();
        }
    }

    public string BotEditorMessage
    {
        get => _botEditorMessage;
        set
        {
            if (_botEditorMessage == value)
            {
                return;
            }

            _botEditorMessage = value;
            OnPropertyChanged();
        }
    }

    public string SaveBotProfile(BotSummaryItem? selectedBot)
    {
        var botId = selectedBot?.BotId ?? Guid.NewGuid().ToString("N");
        var createdAt = selectedBot?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedAt = DateTimeOffset.UtcNow;

        var profile = BotProfile.Create(
            botId: botId,
            name: BotEditorName.Trim(),
            launchPath: BotEditorLaunchPath.Trim(),
            avatarImagePath: selectedBot?.AvatarImagePath,
            launchArgs: MainWindowViewModelHelpers.ParseArgs(BotEditorArgs),
            metadata: MainWindowViewModelHelpers.ParseMetadata(BotEditorMetadata),
            createdAtUtc: createdAt,
            updatedAtUtc: updatedAt);

        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            BotEditorMessage = $"Cannot save bot: {string.Join(", ", errors)}";
            _logger.Log(LogLevel.Warning, "bot_profile_validation_failed", "Bot profile save validation failed.",
                new Dictionary<string, string>
                {
                    ["errors"] = string.Join(",", errors)
                });
            return string.Empty;
        }

        _storage.UpsertBotProfileAsync(profile).GetAwaiter().GetResult();
        BotEditorMessage = $"Saved bot profile: {profile.Name}";
        _logger.Log(LogLevel.Information, "bot_profile_saved", "Bot profile persisted.",
            new Dictionary<string, string>
            {
                ["bot_id"] = profile.BotId,
                ["name"] = profile.Name
            });

        return botId;
    }

    public void PrepareForNewBot()
    {
        BotEditorName = string.Empty;
        BotEditorLaunchPath = string.Empty;
        BotEditorArgs = string.Empty;
        BotEditorMetadata = string.Empty;
        BotEditorMessage = "Creating a new bot profile.";
    }

    public void PopulateEditor(BotSummaryItem bot)
    {
        BotEditorName = bot.Name;
        BotEditorLaunchPath = bot.LaunchPath;
        BotEditorArgs = string.Join(" ", bot.LaunchArgs);
        BotEditorMetadata = MainWindowViewModelHelpers.FormatMetadata(bot.Metadata);
        BotEditorMessage = $"Editing bot profile: {bot.Name}";
    }

    public BotSummaryItem? FindBotById(string botId)
    {
        foreach (var bot in Bots)
        {
            if (bot.BotId == botId)
            {
                return bot;
            }
        }

        return null;
    }
}

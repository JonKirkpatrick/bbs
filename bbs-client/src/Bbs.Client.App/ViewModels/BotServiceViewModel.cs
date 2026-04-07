using System;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

public sealed class BotServiceViewModel : ViewModelBase
{
    private string _botEditorName = string.Empty;
    private string _botEditorLaunchPath = string.Empty;
    private string _botEditorArgs = string.Empty;
    private string _botEditorMetadata = string.Empty;
    private string _botEditorMessage = "Fill out the bot form and save.";

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
}

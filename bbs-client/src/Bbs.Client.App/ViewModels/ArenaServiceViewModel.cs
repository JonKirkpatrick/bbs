using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Bbs.Client.Core.Domain;

namespace Bbs.Client.App.ViewModels;

/// <summary>
/// Service ViewModel managing arena editor form state and initialization workflows.
/// Extracted from MainWindowViewModel to reduce complexity and improve testability.
/// </summary>
public sealed class ArenaServiceViewModel : ViewModelBase
{
    private ObservableCollection<ServerPluginCatalogItem>? _pluginCatalog;
    private string _ownerArenaSelectedPlugin = string.Empty;
    private string _ownerArenaArgs = string.Empty;
    private string _ownerArenaTimeMs = string.Empty;
    private bool _ownerArenaAllowHandicap = true;
    private string _ownerJoinArenaId = string.Empty;
    private string _ownerJoinHandicapPercent = "0";

    public string OwnerArenaSelectedPlugin
    {
        get => _ownerArenaSelectedPlugin;
        set
        {
            if (string.Equals(_ownerArenaSelectedPlugin, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaSelectedPlugin = value;
            OnPropertyChanged();
            SyncArgsFromSelectedPlugin(_pluginCatalog);
        }
    }

    public string OwnerArenaArgs
    {
        get => _ownerArenaArgs;
        set
        {
            if (string.Equals(_ownerArenaArgs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaArgs = value;
            OnPropertyChanged();
        }
    }

    public string OwnerArenaTimeMs
    {
        get => _ownerArenaTimeMs;
        set
        {
            if (string.Equals(_ownerArenaTimeMs, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerArenaTimeMs = value;
            OnPropertyChanged();
        }
    }

    public bool OwnerArenaAllowHandicap
    {
        get => _ownerArenaAllowHandicap;
        set
        {
            if (_ownerArenaAllowHandicap == value)
            {
                return;
            }

            _ownerArenaAllowHandicap = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinArenaId
    {
        get => _ownerJoinArenaId;
        set
        {
            if (string.Equals(_ownerJoinArenaId, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinArenaId = value;
            OnPropertyChanged();
        }
    }

    public string OwnerJoinHandicapPercent
    {
        get => _ownerJoinHandicapPercent;
        set
        {
            if (string.Equals(_ownerJoinHandicapPercent, value, StringComparison.Ordinal))
            {
                return;
            }

            _ownerJoinHandicapPercent = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Resets all arena form fields to default values.
    /// Called when preparing to create or modify an arena context.
    /// </summary>
    public void PrepareForArenaForm()
    {
        OwnerArenaSelectedPlugin = string.Empty;
        OwnerArenaArgs = string.Empty;
        OwnerArenaTimeMs = string.Empty;
        OwnerArenaAllowHandicap = true;
        OwnerJoinArenaId = string.Empty;
        OwnerJoinHandicapPercent = "0";
    }

    /// <summary>
    /// Ensures a valid plugin is selected from the available plugin catalog.
    /// If the current selection is invalid or no plugins are available, selects the first plugin or clears.
    /// </summary>
    /// <param name="pluginCatalog">Collection of available server plugins.</param>
    public void EnsureValidPluginSelection(ObservableCollection<ServerPluginCatalogItem> pluginCatalog)
    {
        _pluginCatalog = pluginCatalog;

        if (pluginCatalog.Count == 0)
        {
            OwnerArenaSelectedPlugin = string.Empty;
            OwnerArenaArgs = string.Empty;
            return;
        }

        var exists = pluginCatalog.Any(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            OwnerArenaSelectedPlugin = pluginCatalog[0].Name;
            return;
        }

        SyncArgsFromSelectedPlugin(pluginCatalog);
    }

    public void SetPluginCatalog(ObservableCollection<ServerPluginCatalogItem> pluginCatalog)
    {
        _pluginCatalog = pluginCatalog;
    }

    /// <summary>
    /// Synchronizes arena args from the selected plugin's metadata.
    /// </summary>
    /// <param name="pluginCatalog">Optional collection of available plugins. If null, uses only current SelectedPlugin.</param>
    private void SyncArgsFromSelectedPlugin(ObservableCollection<ServerPluginCatalogItem>? pluginCatalog = null)
    {
        if (string.IsNullOrWhiteSpace(OwnerArenaSelectedPlugin))
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        ServerPluginCatalogItem? selectedPlugin;
        if (pluginCatalog is not null)
        {
            selectedPlugin = pluginCatalog
                .FirstOrDefault(p => string.Equals(p.Name, OwnerArenaSelectedPlugin, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // If no catalog provided, we can't sync - this is a limitation when called from property setter
            return;
        }

        if (selectedPlugin is null)
        {
            OwnerArenaArgs = string.Empty;
            return;
        }

        if (!selectedPlugin.Metadata.TryGetValue("args_json", out var argsJson) || string.IsNullOrWhiteSpace(argsJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var parts = new List<string>();
            foreach (var arg in doc.RootElement.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = arg.TryGetProperty("key", out var keyElement)
                    ? keyElement.GetString()
                    : null;
                var defaultValue = arg.TryGetProperty("default_value", out var defaultElement)
                    ? defaultElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(defaultValue))
                {
                    continue;
                }

                parts.Add($"{key.Trim()}={defaultValue.Trim()}");
            }

            if (parts.Count > 0)
            {
                OwnerArenaArgs = string.Join(' ', parts);
            }
        }
        catch (JsonException)
        {
        }
    }
}

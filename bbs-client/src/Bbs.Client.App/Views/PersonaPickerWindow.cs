using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Media;
using Bbs.Client.Infrastructure.Personas;

namespace Bbs.Client.App.Views;

public sealed class PersonaPickerWindow : Window
{
    private readonly ListBox _personaListBox;
    private readonly TaskCompletionSource<string?> _completionSource = new();

    private PersonaPickerWindow(IReadOnlyList<PersonaMetadata> personas)
    {
        Title = "Load Persona";
        Width = 420;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;

        var header = new TextBlock
        {
            Text = "Select a persona to load:",
            Margin = new Thickness(0, 0, 0, 8)
        };

        _personaListBox = new ListBox
        {
            MinHeight = 280,
            ItemsSource = personas,
            DisplayMemberBinding = new Binding(nameof(PersonaMetadata.DisplayName)),
            SelectedIndex = personas.Count > 0 ? 0 : -1
        };
        _personaListBox.DoubleTapped += OnLoadClicked;

        var loadButton = new Button
        {
            Content = "Load",
            MinWidth = 80,
            IsEnabled = personas.Count > 0
        };
        loadButton.Click += OnLoadClicked;

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 80
        };
        cancelButton.Click += OnCancelClicked;

        var emptyState = new TextBlock
        {
            Text = personas.Count == 0 ? "No personas are available in ~/.local/state/bbs-client/personas." : string.Empty,
            IsVisible = personas.Count == 0,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                header,
                _personaListBox,
                emptyState,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        loadButton
                    }
                }
            }
        };

        Closed += (_, _) => _completionSource.TrySetResult(null);
        Opened += (_, _) => _personaListBox.Focus();
        KeyDown += OnWindowKeyDown;
    }

    public static async Task<string?> ShowAsync(Window owner, IReadOnlyList<PersonaMetadata> personas)
    {
        var window = new PersonaPickerWindow(personas);
        _ = window.ShowDialog(owner);
        return await window._completionSource.Task.ConfigureAwait(true);
    }

    private void OnLoadClicked(object? sender, RoutedEventArgs e)
    {
        if (_personaListBox.SelectedItem is not PersonaMetadata selectedPersona)
        {
            return;
        }

        _completionSource.TrySetResult(selectedPersona.FilePath);
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _completionSource.TrySetResult(null);
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnLoadClicked(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            OnCancelClicked(sender, e);
        }
    }
}
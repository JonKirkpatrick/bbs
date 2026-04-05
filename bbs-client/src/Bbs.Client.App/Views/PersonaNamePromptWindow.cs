using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Bbs.Client.App.Views;

public sealed class PersonaNamePromptWindow : Window
{
    private readonly TextBox _nameTextBox;
    private readonly TaskCompletionSource<string?> _completionSource = new();

    private PersonaNamePromptWindow(string title, string prompt, string defaultValue)
    {
        Title = title;
        Width = 380;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;

        var promptText = new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 8)
        };

        _nameTextBox = new TextBox
        {
            Text = defaultValue,
            MinWidth = 280
        };
        _nameTextBox.KeyDown += OnTextBoxKeyDown;

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 80
        };
        okButton.Click += OnOkClicked;

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 80
        };
        cancelButton.Click += OnCancelClicked;

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                promptText,
                _nameTextBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        okButton
                    }
                }
            }
        };

        Closed += (_, _) => _completionSource.TrySetResult(null);
        Opened += (_, _) => _nameTextBox.Focus();
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, string defaultValue = "")
    {
        var window = new PersonaNamePromptWindow(title, prompt, defaultValue);
        _ = window.ShowDialog(owner);
        return await window._completionSource.Task.ConfigureAwait(true);
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var value = _nameTextBox.Text?.Trim();
        _completionSource.TrySetResult(string.IsNullOrWhiteSpace(value) ? null : value);
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _completionSource.TrySetResult(null);
        Close();
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOkClicked(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            OnCancelClicked(sender, e);
        }
    }
}

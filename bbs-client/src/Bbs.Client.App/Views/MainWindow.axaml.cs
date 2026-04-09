using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Bbs.Client.App.ViewModels;

namespace Bbs.Client.App.Views;

public partial class MainWindow : Window
{
    private bool _embeddedViewerInitialized;
    private MainWindowViewModel? _currentVm;
    private ContentControl? _embeddedViewerHost;
    private Border? _embeddedViewerSurface;
    private Border? _embeddedViewerViewport;
    private const double EmbeddedViewerSurfacePadding = 6;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        EnsureEmbeddedViewerLayoutHooks();
        TryInitializeEmbeddedViewer();
        UpdateEmbeddedViewerLayout();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVm is not null)
        {
            _currentVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _currentVm = DataContext as MainWindowViewModel;
        if (_currentVm is not null)
        {
            _currentVm.PropertyChanged += OnViewModelPropertyChanged;
        }

        EnsureEmbeddedViewerLayoutHooks();
        TryInitializeEmbeddedViewer();
        UpdateEmbeddedViewerLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ShowArenaViewer) ||
            e.PropertyName == nameof(MainWindowViewModel.IsEmbeddedViewerSupported) ||
            e.PropertyName == nameof(MainWindowViewModel.ArenaViewerHostWidth) ||
            e.PropertyName == nameof(MainWindowViewModel.ArenaViewerHostHeight))
        {
            TryInitializeEmbeddedViewer();
            UpdateEmbeddedViewerLayout();
        }
    }

    private async void OnNewPersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var name = await PersonaNamePromptWindow.ShowAsync(this, "New Persona", "Enter a name for the new persona:", "New Persona");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await ViewModel.CreatePersonaAsync(name);
    }

    private async void OnLoadPersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var personas = await ViewModel.ListAvailablePersonasAsync();
        if (personas.Count == 0)
        {
            return;
        }

        var selectedPersonaPath = await PersonaPickerWindow.ShowAsync(this, personas);
        if (string.IsNullOrWhiteSpace(selectedPersonaPath))
        {
            return;
        }

        await ViewModel.LoadPersonaAsync(selectedPersonaPath);
    }

    private void OnUnloadPersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        ViewModel.UnloadPersona();
    }

    private async void OnDuplicatePersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsPersonaLoaded)
        {
            return;
        }

        var currentName = Path.GetFileNameWithoutExtension(ViewModel.CurrentPersonaPath ?? string.Empty);
        var name = await PersonaNamePromptWindow.ShowAsync(this, "Duplicate Persona", "Enter a name for the duplicate persona:", $"{currentName} Copy");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await ViewModel.DuplicateCurrentPersonaAsync(name);
    }

    private async void OnRenamePersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsPersonaLoaded)
        {
            return;
        }

        var currentName = Path.GetFileNameWithoutExtension(ViewModel.CurrentPersonaPath ?? string.Empty);
        var name = await PersonaNamePromptWindow.ShowAsync(this, "Rename Persona", "Enter a new name for the current persona:", currentName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await ViewModel.RenameCurrentPersonaAsync(name);
    }

    private async void OnDeletePersonaClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.IsPersonaLoaded)
        {
            return;
        }

        var currentName = Path.GetFileNameWithoutExtension(ViewModel.CurrentPersonaPath ?? string.Empty);
        var confirmation = await PersonaNamePromptWindow.ShowAsync(this, "Delete Persona", $"Type '{currentName}' to confirm delete:", currentName);
        if (!string.Equals(confirmation, currentName, StringComparison.Ordinal))
        {
            return;
        }

        await ViewModel.DeleteCurrentPersonaAsync();
    }

    private void EnsureEmbeddedViewerLayoutHooks()
    {
        if (_embeddedViewerHost is null)
        {
            _embeddedViewerHost = this.FindControl<ContentControl>("ArenaEmbeddedViewerHost");
        }

        if (_embeddedViewerSurface is null)
        {
            _embeddedViewerSurface = this.FindControl<Border>("ArenaEmbeddedViewerSurface");
        }

        if (_embeddedViewerViewport is null)
        {
            _embeddedViewerViewport = this.FindControl<Border>("ArenaViewerViewport");
            if (_embeddedViewerViewport is not null)
            {
                _embeddedViewerViewport.SizeChanged += OnEmbeddedViewerViewportSizeChanged;
            }
        }
    }

    private void OnEmbeddedViewerViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateEmbeddedViewerLayout();
    }

    private void UpdateEmbeddedViewerLayout()
    {
        if (_currentVm is null || _embeddedViewerHost is null || _embeddedViewerSurface is null || _embeddedViewerViewport is null)
        {
            return;
        }

        var nativeWidth = Math.Max(1, _currentVm.ArenaViewerHostWidth);
        var nativeHeight = Math.Max(1, _currentVm.ArenaViewerHostHeight);

        var availableWidth = Math.Max(1, _embeddedViewerViewport.Bounds.Width - (EmbeddedViewerSurfacePadding * 2));
        // The viewer lives inside a vertical stack where height can expand with content.
        // Scale primarily from available width, then cap by a fraction of the window height.
        var widthScale = availableWidth / nativeWidth;
        var windowHeight = Math.Max(1, Bounds.Height);
        var maxViewerHeight = Math.Max(220, windowHeight * 0.62);
        var heightScaleCap = maxViewerHeight / nativeHeight;
        var scale = Math.Max(0.1, Math.Min(widthScale, heightScaleCap));

        var displayWidth = Math.Max(1, Math.Floor(nativeWidth * scale));
        var displayHeight = Math.Max(1, Math.Floor(nativeHeight * scale));

        _embeddedViewerHost.Width = displayWidth;
        _embeddedViewerHost.Height = displayHeight;

        _embeddedViewerSurface.Width = displayWidth + (EmbeddedViewerSurfacePadding * 2);
        _embeddedViewerSurface.Height = displayHeight + (EmbeddedViewerSurfacePadding * 2);
    }

    private void TryInitializeEmbeddedViewer()
    {
        if (_embeddedViewerInitialized)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (!vm.IsEmbeddedViewerSupported)
        {
            _embeddedViewerInitialized = true;
            return;
        }

        if (!vm.ShowArenaViewer)
        {
            return;
        }

        var host = this.FindControl<ContentControl>("ArenaEmbeddedViewerHost");
        if (host is null)
        {
            return;
        }

        try
        {
            var webViewType = Type.GetType("WebViewControl.WebView, WebViewControl.Avalonia", throwOnError: true)!;
            var webView = Activator.CreateInstance(webViewType) as Control
                ?? throw new InvalidOperationException("Embedded WebView type could not be created as an Avalonia control.");

            var addressPropertyField = webViewType.GetField("AddressProperty", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Embedded WebView AddressProperty field is missing.");

            if (addressPropertyField.GetValue(null) is not AvaloniaProperty addressProperty)
            {
                throw new InvalidOperationException("Embedded WebView AddressProperty is invalid.");
            }

            webView.Bind(addressProperty, new Binding("ArenaViewerUrl"));
            host.Content = webView;
            _embeddedViewerInitialized = true;
            vm.ConfigureEmbeddedViewerSupport(true, vm.EmbeddedViewerSupportMessage);
            UpdateEmbeddedViewerLayout();
        }
        catch (Exception ex)
        {
            vm.RegisterEmbeddedViewerRuntimeFailure($"Embedded WebView init failed ({ex.GetType().Name}).");
            _embeddedViewerInitialized = true;
        }
    }
}

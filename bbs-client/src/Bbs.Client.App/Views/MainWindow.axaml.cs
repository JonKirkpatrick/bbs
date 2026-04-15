using System.ComponentModel;
using System.Reflection;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Bbs.Client.App.ViewModels;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Bbs.Client.App.Views;

public partial class MainWindow : Window
{
    private bool _embeddedViewerInitialized;
    private MainWindowViewModel? _currentVm;
    private ContentControl? _embeddedViewerHost;
    private Border? _embeddedViewerSurface;
    private Border? _embeddedViewerViewport;
    private ShapePath? _logoPulsingOverlayPath;
    private bool _logoPulsePlayed;
    private Grid? _leftDrawerPanel;
    private Grid? _rightDrawerPanel;
    private Border? _leftDrawerHitStrip;
    private Border? _rightDrawerHitStrip;
    private TranslateTransform? _leftDrawerTransform;
    private TranslateTransform? _rightDrawerTransform;
    private bool _isLeftDrawerOpen;
    private bool _isRightDrawerOpen;
    private CancellationTokenSource? _leftDrawerCloseCts;
    private CancellationTokenSource? _rightDrawerCloseCts;
    private static readonly IBrush PersonaLoadPulseBrush = new SolidColorBrush(Color.Parse("#2DBE60"));
    private const double EmbeddedViewerSurfacePadding = 6;
    private const double DrawerMinimumWidth = 56;
    private const double DrawerDesiredWidth = 280;
    private const double DrawerMinWindowFraction = 0.25;
    private const double DrawerMaxWindowFraction = 0.50;
    private static readonly TimeSpan DrawerCloseDelay = TimeSpan.FromMilliseconds(120);

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        SizeChanged += OnWindowSizeChanged;
        DataContextChanged += OnDataContextChanged;
        PointerExited += OnWindowPointerExited;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        EnsureDrawerLayoutHooks();
        UpdateDrawerWidths();
        SetDrawersClosedImmediate();
        EnsureEmbeddedViewerLayoutHooks();
        TryInitializeEmbeddedViewer();
        UpdateEmbeddedViewerLayout();
        await PlayStartupLogoPulseAsync();
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

        EnsureDrawerLayoutHooks();
        UpdateDrawerWidths();
        SetDrawersClosedImmediate();
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

        if (e.PropertyName == nameof(MainWindowViewModel.UIState))
        {
            SetDrawersClosedImmediate();
        }
    }

    private void EnsureDrawerLayoutHooks()
    {
        if (_leftDrawerPanel is null)
        {
            _leftDrawerPanel = this.FindControl<Grid>("LeftDrawerPanel");
            if (_leftDrawerPanel?.RenderTransform is TranslateTransform leftTransform)
            {
                _leftDrawerTransform = leftTransform;
            }
        }

        _leftDrawerHitStrip ??= this.FindControl<Border>("LeftDrawerHitStrip");

        if (_rightDrawerPanel is null)
        {
            _rightDrawerPanel = this.FindControl<Grid>("RightDrawerPanel");
            if (_rightDrawerPanel?.RenderTransform is TranslateTransform rightTransform)
            {
                _rightDrawerTransform = rightTransform;
            }
        }

        _rightDrawerHitStrip ??= this.FindControl<Border>("RightDrawerHitStrip");
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateDrawerWidths();
    }

    private double GetClampedDrawerWidth()
    {
        var windowWidth = Math.Max(1, Bounds.Width);
        var minWidth = windowWidth * DrawerMinWindowFraction;
        var maxWidth = windowWidth * DrawerMaxWindowFraction;
        return Math.Clamp(DrawerDesiredWidth, minWidth, maxWidth);
    }

    private void UpdateDrawerWidths()
    {
        EnsureDrawerLayoutHooks();
        var width = GetClampedDrawerWidth();

        if (_leftDrawerPanel is not null)
        {
            _leftDrawerPanel.Width = width;
        }

        if (_rightDrawerPanel is not null)
        {
            _rightDrawerPanel.Width = width;
        }

        if (_leftDrawerTransform is not null && !_isLeftDrawerOpen)
        {
            _leftDrawerTransform.X = -width;
        }

        if (_rightDrawerTransform is not null && !_isRightDrawerOpen)
        {
            _rightDrawerTransform.X = width;
        }
    }

    private double GetDrawerWidth(Control? drawer)
    {
        if (drawer is null)
        {
            return 280;
        }

        if (drawer.Bounds.Width > 0)
        {
            return Math.Max(DrawerMinimumWidth, drawer.Bounds.Width);
        }

        if (drawer.Width > 0)
        {
            return Math.Max(DrawerMinimumWidth, drawer.Width);
        }

        return 280;
    }

    private void SetDrawersClosedImmediate()
    {
        EnsureDrawerLayoutHooks();
        CancelLeftDrawerClose();
        CancelRightDrawerClose();

        if (_leftDrawerTransform is not null)
        {
            _leftDrawerTransform.X = -GetDrawerWidth(_leftDrawerPanel);
        }

        if (_rightDrawerTransform is not null)
        {
            _rightDrawerTransform.X = GetDrawerWidth(_rightDrawerPanel);
        }

        _isLeftDrawerOpen = false;
        _isRightDrawerOpen = false;
    }

    private bool IsPointerOverLeftDrawerRegion()
    {
        return (_leftDrawerPanel?.IsPointerOver ?? false) || (_leftDrawerHitStrip?.IsPointerOver ?? false);
    }

    private bool IsPointerOverRightDrawerRegion()
    {
        return (_rightDrawerPanel?.IsPointerOver ?? false) || (_rightDrawerHitStrip?.IsPointerOver ?? false);
    }

    private void CancelLeftDrawerClose()
    {
        _leftDrawerCloseCts?.Cancel();
        _leftDrawerCloseCts?.Dispose();
        _leftDrawerCloseCts = null;
    }

    private void CancelRightDrawerClose()
    {
        _rightDrawerCloseCts?.Cancel();
        _rightDrawerCloseCts?.Dispose();
        _rightDrawerCloseCts = null;
    }

    private void ScheduleLeftDrawerClose()
    {
        CancelLeftDrawerClose();
        var cts = new CancellationTokenSource();
        _leftDrawerCloseCts = cts;
        _ = CloseLeftDrawerAfterDelayAsync(cts.Token);
    }

    private void ScheduleRightDrawerClose()
    {
        CancelRightDrawerClose();
        var cts = new CancellationTokenSource();
        _rightDrawerCloseCts = cts;
        _ = CloseRightDrawerAfterDelayAsync(cts.Token);
    }

    private async Task CloseLeftDrawerAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DrawerCloseDelay, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsPointerOverLeftDrawerRegion())
                {
                    SetLeftDrawerOpen(false);
                }
            }, DispatcherPriority.Input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Drawer close was canceled by a re-enter event.
        }
    }

    private async Task CloseRightDrawerAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DrawerCloseDelay, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsPointerOverRightDrawerRegion())
                {
                    SetRightDrawerOpen(false);
                }
            }, DispatcherPriority.Input, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Drawer close was canceled by a re-enter event.
        }
    }

    private void SetLeftDrawerOpen(bool isOpen)
    {
        EnsureDrawerLayoutHooks();
        if (_leftDrawerTransform is null || _leftDrawerPanel is null)
        {
            return;
        }

        if (isOpen)
        {
            CancelLeftDrawerClose();
        }

        if (_isLeftDrawerOpen == isOpen)
        {
            return;
        }

        var target = isOpen ? 0 : -GetDrawerWidth(_leftDrawerPanel);
        _isLeftDrawerOpen = isOpen;
        _leftDrawerTransform.X = target;
    }

    private void SetRightDrawerOpen(bool isOpen)
    {
        EnsureDrawerLayoutHooks();
        if (_rightDrawerTransform is null || _rightDrawerPanel is null)
        {
            return;
        }

        if (isOpen)
        {
            CancelRightDrawerClose();
        }

        if (_isRightDrawerOpen == isOpen)
        {
            return;
        }

        var target = isOpen ? 0 : GetDrawerWidth(_rightDrawerPanel);
        _isRightDrawerOpen = isOpen;
        _rightDrawerTransform.X = target;
    }

    private void OnLeftDrawerHitStripPointerEntered(object? sender, PointerEventArgs e)
    {
        SetLeftDrawerOpen(true);
    }

    private void OnLeftDrawerHitStripPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleLeftDrawerClose();
    }

    private void OnRightDrawerHitStripPointerEntered(object? sender, PointerEventArgs e)
    {
        SetRightDrawerOpen(true);
    }

    private void OnRightDrawerHitStripPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleRightDrawerClose();
    }

    private void OnLeftDrawerPanelPointerEntered(object? sender, PointerEventArgs e)
    {
        SetLeftDrawerOpen(true);
    }

    private void OnRightDrawerPanelPointerEntered(object? sender, PointerEventArgs e)
    {
        SetRightDrawerOpen(true);
    }

    private void OnLeftDrawerPanelPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleLeftDrawerClose();
    }

    private void OnRightDrawerPanelPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleRightDrawerClose();
    }

    private void OnWindowPointerExited(object? sender, PointerEventArgs e)
    {
        ScheduleLeftDrawerClose();
        ScheduleRightDrawerClose();
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

        await RunWithPersonaLoadPulseAsync(() => ViewModel.CreatePersonaAsync(name));
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

        await RunWithPersonaLoadPulseAsync(() => ViewModel.LoadPersonaAsync(selectedPersonaPath));
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

        await RunWithPersonaLoadPulseAsync(() => ViewModel.DuplicateCurrentPersonaAsync(name));
    }

    private async Task RunWithPersonaLoadPulseAsync(Func<Task> operation)
    {
        _logoPulsingOverlayPath ??= this.FindControl<ShapePath>("LogoPulsingOverlayPath");
        if (_logoPulsingOverlayPath is null)
        {
            await operation();
            return;
        }

        var originalStroke = _logoPulsingOverlayPath.Stroke;
        using var pulseCancellation = new CancellationTokenSource();
        _logoPulsingOverlayPath.Stroke = PersonaLoadPulseBrush;
        var pulseTask = RunPersonaLoadPulseLoopAsync(pulseCancellation.Token);

        try
        {
            await operation();
        }
        finally
        {
            pulseCancellation.Cancel();
            try
            {
                await pulseTask;
            }
            catch (OperationCanceledException)
            {
                // Pulse cancellation is expected once loading has completed.
            }

            _logoPulsingOverlayPath.Opacity = 0;
            _logoPulsingOverlayPath.Stroke = originalStroke;
        }
    }

    private async Task RunPersonaLoadPulseLoopAsync(CancellationToken cancellationToken)
    {
        if (_logoPulsingOverlayPath is null)
        {
            return;
        }

        var pulseAnimation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(900),
            IterationCount = new IterationCount(1),
            Easing = new SineEaseInOut(),
            FillMode = FillMode.None,
            PlaybackDirection = PlaybackDirection.Normal,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0.1d),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(0.5d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0.85d),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0.1d),
                    }
                }
            }
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            await pulseAnimation.RunAsync(_logoPulsingOverlayPath, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
        }
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

    private async Task PlayStartupLogoPulseAsync()
    {
        if (_logoPulsePlayed)
        {
            return;
        }

        _logoPulsingOverlayPath ??= this.FindControl<ShapePath>("LogoPulsingOverlayPath");
        if (_logoPulsingOverlayPath is null)
        {
            return;
        }

        _logoPulsePlayed = true;

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        var pulseAnimation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1650),
            IterationCount = new IterationCount(1),
            Easing = new SineEaseOut(),
            FillMode = FillMode.None,
            PlaybackDirection = PlaybackDirection.Normal,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0.8d),
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                    }
                }
            }
        };

        await pulseAnimation.RunAsync(_logoPulsingOverlayPath);
        _logoPulsingOverlayPath.Opacity = 0;
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

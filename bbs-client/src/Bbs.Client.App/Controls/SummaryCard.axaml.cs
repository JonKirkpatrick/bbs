using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bbs.Client.App.Controls;

public partial class SummaryCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SummaryCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<SummaryCard, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<string> StatusTextProperty =
        AvaloniaProperty.Register<SummaryCard, string>(nameof(StatusText), string.Empty);

    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<SummaryCard, IBrush>(nameof(AccentBrush), Brushes.SlateGray);

    public static readonly StyledProperty<IBrush> BackgroundBrushProperty =
        AvaloniaProperty.Register<SummaryCard, IBrush>(nameof(BackgroundBrush), Brushes.Transparent);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SummaryCard, bool>(nameof(IsActive), false);

    public SummaryCard()
    {
        InitializeComponent();
        UpdateSelectionVisual();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public IBrush AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsActiveProperty ||
            change.Property == AccentBrushProperty)
        {
            UpdateSelectionVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual()
    {
        if (CardBorder is null)
        {
            return;
        }

        if (IsActive)
        {
            CardBorder.BorderThickness = new Thickness(3);
            CardBorder.Padding = new Thickness(10);
            CardBorder.BorderBrush = ResolveActiveBorderBrush() ?? AccentBrush;
        }
        else
        {
            CardBorder.BorderThickness = new Thickness(1);
            CardBorder.Padding = new Thickness(12);
            CardBorder.BorderBrush = AccentBrush;
        }

    }

    private IBrush? ResolveActiveBorderBrush()
    {
        return TryGetResource("Palette.ListItemSelectedBorderBrush", ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
    }

}

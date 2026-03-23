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

    public SummaryCard()
    {
        InitializeComponent();
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
}

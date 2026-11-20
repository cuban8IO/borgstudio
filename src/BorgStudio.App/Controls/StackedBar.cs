using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BorgStudio.App.Controls;

/// <summary>A part of a <see cref="StackedBar"/>: its share of the whole (0..1) and which chart color it has.</summary>
/// <param name="Kind">Picks the brush: "series" (the repository), "forecast" (other data), or a resource key.</param>
public sealed record BarSegment(double Fraction, string Kind);

/// <summary>
/// A horizontal bar split into parts, left to right, on a recessive track (the rest: free space). Drawn by hand – no chart
/// library – in the chart colors of App.axaml.
/// </summary>
public sealed class StackedBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<StackedBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<StackedBar, double>(nameof(Radius), 5);

    static StackedBar()
    {
        AffectsRender<StackedBar>(SegmentsProperty, RadiusProperty);
        HeightProperty.OverrideDefaultValue<StackedBar>(10);
    }

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var shape = new RoundedRect(bounds, Radius);
        context.DrawRectangle(ChartBrushes.Get(this, "ChartTrackBrush"), null, shape);
        using (context.PushClip(shape))
        {
            var x = 0.0;
            foreach (var segment in Segments ?? [])
            {
                var width = Math.Clamp(segment.Fraction, 0, 1) * bounds.Width;
                if (width <= 0)
                    continue;
                context.DrawRectangle(ChartBrushes.Get(this, ChartBrushes.KeyOf(segment.Kind)), null, new Rect(x, 0, width, bounds.Height));
                // A thin gap between touching parts, in the track's color.
                x += width;
                if (x < bounds.Width - 1)
                    context.DrawRectangle(ChartBrushes.Get(this, "CardBackground"), null, new Rect(x - 1, 0, 2, bounds.Height));
            }
        }
    }
}

/// <summary>The chart colors from the app's resources (with fallbacks for designers and tests).</summary>
internal static class ChartBrushes
{
    public static string KeyOf(string kind) => kind switch
    {
        "series" => "ChartSeriesBrush",
        "forecast" => "ChartForecastBrush",
        "limit" => "ChartLimitBrush",
        "ok" => "ChartOkBrush",
        "warn" => "ChartWarnBrush",
        "fail" => "ChartFailBrush",
        _ => kind,
    };

    public static IBrush Get(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : key switch
            {
                "ChartSeriesBrush" => new SolidColorBrush(Color.Parse("#3987E5")),
                "ChartForecastBrush" => new SolidColorBrush(Color.Parse("#EDA100")),
                "ChartLimitBrush" or "ChartFailBrush" => new SolidColorBrush(Color.Parse("#D03B3B")),
                "ChartOkBrush" => new SolidColorBrush(Color.Parse("#0CA30C")),
                "ChartWarnBrush" => new SolidColorBrush(Color.Parse("#FAB219")),
                "CardBackground" => new SolidColorBrush(Color.Parse("#1F1F1F")),
                _ => new SolidColorBrush(Color.Parse("#2D2D2D")),
            };
}

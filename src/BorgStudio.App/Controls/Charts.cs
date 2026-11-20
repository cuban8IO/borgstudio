using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Controls;

/// <summary>A value at a time (a measurement, or a point of the forecast).</summary>
public sealed record ChartPoint(DateTimeOffset Time, double Value);

/// <summary>A column: its label below, its height, its color ("series", "warn", "fail") and what hovering shows.</summary>
public sealed record ChartColumn(string Label, double Value, string Kind, string Tip);

/// <summary>A day in the activity calendar: its color ("", "ok", "warn", "fail") and what hovering shows.</summary>
public sealed record CalendarDay(DateOnly Date, string Kind, string Tip);

/// <summary>
/// What the charts share: drawn by hand in the chart colors (no chart library), sizes on the value axis, and a small tooltip
/// next to the pointer.
/// </summary>
public abstract class ChartBase : Control
{
    protected const double AxisWidth = 64;
    protected const double AxisHeight = 20;
    protected const double LabelSize = 11;

    protected Point? Pointer { get; private set; }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Pointer = null;
        InvalidateVisual();
    }

    protected IBrush Brush(string key) => ChartBrushes.Get(this, ChartBrushes.KeyOf(key));

    protected static IBrush Muted { get; } = new SolidColorBrush(Color.Parse("#898781"));

    protected static FormattedText Text(string text, IBrush brush, double size = LabelSize) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    /// <summary>Byte sizes ("760 GB").</summary>
    protected static string Size(double bytes) => BorgTexts.Size((long)Math.Max(0, bytes));

    /// <summary>Horizontal grid lines with their values on the left.</summary>
    protected void DrawValueAxis(DrawingContext context, Rect plot, double min, double max, int lines = 4)
    {
        var grid = new Pen(Brush("ChartGridBrush"), 1);
        for (var index = 0; index <= lines; index++)
        {
            var value = min + (max - min) * index / lines;
            var y = plot.Bottom - plot.Height * index / lines;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(Size(value), Muted);
            context.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
    }

    /// <summary>A small box with <paramref name="text"/> beside <paramref name="anchor"/>, kept inside the control.</summary>
    protected void DrawTooltip(DrawingContext context, Point anchor, string text)
    {
        var label = Text(text, ChartBrushes.Get(this, "BpTextBrush"), 12);
        var box = new Rect(anchor.X + 10, anchor.Y - label.Height - 14, label.Width + 16, label.Height + 10);
        if (box.Right > Bounds.Width)
            box = box.WithX(anchor.X - box.Width - 10);
        if (box.Top < 0)
            box = box.WithY(anchor.Y + 10);
        context.DrawRectangle(ChartBrushes.Get(this, "BpSurface2Brush"), new Pen(ChartBrushes.Get(this, "BpLineBrush"), 1),
            new RoundedRect(box, 4));
        context.DrawText(label, new Point(box.X + 8, box.Y + 5));
    }
}

/// <summary>
/// The storage over time: the measurements as a line with a light area below, the forecast as a dashed line on, and the
/// capacity as a dashed limit. Hovering shows the nearest point.
/// </summary>
public sealed class StorageChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<StorageChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> ProjectionProperty =
        AvaloniaProperty.Register<StorageChart, IReadOnlyList<ChartPoint>?>(nameof(Projection));

    public static readonly StyledProperty<double?> LimitProperty = AvaloniaProperty.Register<StorageChart, double?>(nameof(Limit));

    public static readonly StyledProperty<string?> LimitLabelProperty = AvaloniaProperty.Register<StorageChart, string?>(nameof(LimitLabel));

    static StorageChart() => AffectsRender<StorageChart>(PointsProperty, ProjectionProperty, LimitProperty, LimitLabelProperty);

    public IReadOnlyList<ChartPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>The forecast, starting at the last measurement.</summary>
    public IReadOnlyList<ChartPoint>? Projection
    {
        get => GetValue(ProjectionProperty);
        set => SetValue(ProjectionProperty, value);
    }

    /// <summary>The total (disk or capacity); <c>null</c>: unknown.</summary>
    public double? Limit
    {
        get => GetValue(LimitProperty);
        set => SetValue(LimitProperty, value);
    }

    public string? LimitLabel
    {
        get => GetValue(LimitLabelProperty);
        set => SetValue(LimitLabelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var points = Points ?? [];
        if (points.Count < 2 || Bounds.Width < AxisWidth + 40 || Bounds.Height < AxisHeight + 30)
            return;
        var projection = Projection ?? [];
        var all = points.Concat(projection).ToList();

        var start = all.Min(point => point.Time);
        var end = all.Max(point => point.Time);
        var span = Math.Max(1, (end - start).TotalSeconds);
        var high = Math.Max(all.Max(point => point.Value), Limit ?? 0);
        var low = all.Min(point => point.Value);
        // Room below the lowest value, but never below zero: growth stays visible without lying about it.
        var min = Math.Max(0, low - (high - low) * 0.25 - high * 0.02);
        var max = high + (high - min) * 0.05;

        var plot = new Rect(AxisWidth, 8, Bounds.Width - AxisWidth - 8, Bounds.Height - AxisHeight - 8);
        Point At(ChartPoint point) => new(
            plot.Left + plot.Width * (point.Time - start).TotalSeconds / span,
            plot.Bottom - plot.Height * (point.Value - min) / Math.Max(1, max - min));

        DrawValueAxis(context, plot, min, max);

        // Dates along the bottom.
        for (var index = 0; index <= 4; index++)
        {
            var time = start.AddSeconds(span * index / 4).ToLocalTime();
            var label = Text(time.ToString("d. MMM", CultureInfo.CurrentCulture), Muted);
            var x = plot.Left + plot.Width * index / 4 - label.Width / 2;
            context.DrawText(label, new Point(Math.Clamp(x, plot.Left - 20, Bounds.Width - label.Width), plot.Bottom + 4));
        }

        // The capacity.
        if (Limit is { } limit)
        {
            var y = plot.Bottom - plot.Height * (limit - min) / Math.Max(1, max - min);
            context.DrawLine(new Pen(Brush("limit"), 1, new DashStyle([3, 3], 0)), new Point(plot.Left, y), new Point(plot.Right, y));
            if (LimitLabel is { } text)
            {
                var label = Text(text, Brush("limit"));
                context.DrawText(label, new Point(plot.Right - label.Width - 4, y - label.Height - 2));
            }
        }

        // The measurements: area and line.
        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(At(points[0]), false);
            foreach (var point in points.Skip(1))
                geometry.LineTo(At(point));
            geometry.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(At(points[0]).X, plot.Bottom), true);
            foreach (var point in points)
                geometry.LineTo(At(point));
            geometry.LineTo(new Point(At(points[^1]).X, plot.Bottom));
            geometry.EndFigure(true);
        }
        var series = Brush("series");
        context.DrawGeometry(new SolidColorBrush(((ISolidColorBrush)series).Color, 0.12), null, area);
        context.DrawGeometry(null, new Pen(series, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        // The forecast.
        if (projection.Count >= 2)
        {
            var dashed = new Pen(Brush("forecast"), 2, new DashStyle([4, 3], 0), PenLineCap.Round);
            for (var index = 1; index < projection.Count; index++)
                context.DrawLine(dashed, At(projection[index - 1]), At(projection[index]));
        }

        // Hover: the nearest point in time.
        if (Pointer is { } pointer && plot.Contains(pointer))
        {
            var nearest = all.MinBy(point => Math.Abs(At(point).X - pointer.X))!;
            var spot = At(nearest);
            context.DrawLine(new Pen(Muted, 1), new Point(spot.X, plot.Top), new Point(spot.X, plot.Bottom));
            var isForecast = !points.Contains(nearest);
            context.DrawEllipse(isForecast ? Brush("forecast") : series, new Pen(ChartBrushes.Get(this, "CardBackground"), 2), spot, 4.5, 4.5);
            var when = nearest.Time.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
            DrawTooltip(context, spot, $"{when}: {Size(nearest.Value)}" + (isForecast ? $" ({BorgStudio.App.Resources.Strings.ChartForecast})" : ""));
        }
    }
}

/// <summary>Columns per backup (what it newly stored); failed runs as short red stubs. Hovering shows a column's details.</summary>
public sealed class ColumnChart : ChartBase
{
    public static readonly StyledProperty<IReadOnlyList<ChartColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<ColumnChart, IReadOnlyList<ChartColumn>?>(nameof(Columns));

    static ColumnChart() => AffectsRender<ColumnChart>(ColumnsProperty);

    public IReadOnlyList<ChartColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var columns = Columns ?? [];
        if (columns.Count == 0 || Bounds.Width < AxisWidth + 40 || Bounds.Height < AxisHeight + 30)
            return;

        var plot = new Rect(AxisWidth, 8, Bounds.Width - AxisWidth - 8, Bounds.Height - AxisHeight - 8);
        var max = Math.Max(1, columns.Max(column => column.Value)) * 1.08;
        DrawValueAxis(context, plot, 0, max);

        var slot = plot.Width / columns.Count;
        var width = Math.Clamp(slot * 0.6, 3, 24);
        var labelEvery = Math.Max(1, (int)Math.Ceiling(columns.Count / (plot.Width / 56)));
        var hovered = Pointer is { } pointer && plot.Contains(pointer) ? (int)((pointer.X - plot.Left) / slot) : -1;

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var center = plot.Left + slot * (index + 0.5);
            // Failed runs stored nothing: a short stub keeps them visible.
            var height = Math.Max(column.Kind == "fail" ? 4 : 1.5, plot.Height * column.Value / max);
            var bar = new Rect(center - width / 2, plot.Bottom - height, width, height);
            var brush = Brush(column.Kind);
            context.DrawRectangle(index == hovered ? new SolidColorBrush(((ISolidColorBrush)brush).Color, 0.75) : brush, null,
                new RoundedRect(bar, 3, 3, 0, 0));
            if (index % labelEvery == 0)
            {
                var label = Text(column.Label, Muted);
                context.DrawText(label, new Point(center - label.Width / 2, plot.Bottom + 4));
            }
        }

        if (hovered >= 0 && hovered < columns.Count)
            DrawTooltip(context, new Point(plot.Left + slot * (hovered + 0.5), Pointer!.Value.Y), columns[hovered].Tip);
    }
}

/// <summary>
/// The days of the last weeks as small squares, a column per week (Monday on top): green ok, yellow warnings, red failed,
/// empty without backup. Hovering shows the day.
/// </summary>
public sealed class ActivityCalendar : ChartBase
{
    private const double Cell = 14;
    private const double Gap = 3;
    private const double LabelWidth = 26;

    public static readonly StyledProperty<IReadOnlyList<CalendarDay>?> DaysProperty =
        AvaloniaProperty.Register<ActivityCalendar, IReadOnlyList<CalendarDay>?>(nameof(Days));

    static ActivityCalendar() => AffectsRender<ActivityCalendar>(DaysProperty);

    public IReadOnlyList<CalendarDay>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var weeks = Math.Ceiling((Days?.Count ?? 0) / 7.0) + 1;
        return new Size(LabelWidth + weeks * (Cell + Gap), 7 * (Cell + Gap) + 8);
    }

    public override void Render(DrawingContext context)
    {
        var days = Days ?? [];
        if (days.Count == 0)
            return;

        // Weekday names in the first column (Monday, Wednesday, Friday).
        var monday = new DateOnly(2024, 1, 1);
        foreach (var row in new[] { 0, 2, 4 })
        {
            var label = Text(monday.AddDays(row).ToString("ddd", CultureInfo.CurrentCulture), Muted);
            context.DrawText(label, new Point(0, row * (Cell + Gap) + (Cell - label.Height) / 2));
        }

        var first = days[0].Date;
        var firstMonday = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        Rect CellOf(DateOnly date)
        {
            var offset = date.DayNumber - firstMonday.DayNumber;
            return new Rect(LabelWidth + offset / 7 * (Cell + Gap), offset % 7 * (Cell + Gap), Cell, Cell);
        }

        CalendarDay? hovered = null;
        foreach (var day in days)
        {
            var cell = CellOf(day.Date);
            var brush = day.Kind.Length == 0 ? Brush("ChartTrackBrush") : Brush(day.Kind);
            context.DrawRectangle(brush, null, new RoundedRect(cell, 3));
            if (Pointer is { } pointer && cell.Inflate(Gap / 2).Contains(pointer))
            {
                hovered = day;
                context.DrawRectangle(null, new Pen(ChartBrushes.Get(this, "BpTextBrush"), 1), new RoundedRect(cell, 3));
            }
        }
        if (hovered is not null)
            DrawTooltip(context, CellOf(hovered.Date).Center, hovered.Tip);
    }
}

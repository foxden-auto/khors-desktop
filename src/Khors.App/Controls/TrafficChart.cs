using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Khors.App.Services;

namespace Khors.App.Controls;

/// <summary>
/// График скорости: две линии (отправка и загрузка) за последние <see cref="Capacity"/> секунд, новые справа.
/// Наведение показывает значения в этой точке. Цвета — кисти темы, заданные в разметке.
/// </summary>
public sealed class TrafficChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<TrafficSample>?> SamplesProperty =
        AvaloniaProperty.Register<TrafficChart, IReadOnlyList<TrafficSample>?>(nameof(Samples));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<TrafficChart, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<TrafficChart, int>(nameof(Capacity), 60);

    public static readonly StyledProperty<IBrush?> UploadBrushProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(UploadBrush));

    public static readonly StyledProperty<IBrush?> DownloadBrushProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(DownloadBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(GridBrush));

    /// <summary>Фон под точками наведения — кольцо, отделяющее точку от линии.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<TrafficChart, IBrush?>(nameof(SurfaceBrush));

    private int? _hover;

    static TrafficChart()
    {
        AffectsRender<TrafficChart>(SamplesProperty, MaximumProperty, CapacityProperty, UploadBrushProperty, DownloadBrushProperty, GridBrushProperty, SurfaceBrushProperty);
    }

    public IReadOnlyList<TrafficSample>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public IBrush? UploadBrush
    {
        get => GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public IBrush? DownloadBrush
    {
        get => GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? SurfaceBrush
    {
        get => GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2)
        {
            return;
        }

        // Фон для попадания указателя: без него наведение срабатывает только над линиями.
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        var grid = new Pen(GridBrush, 1);
        foreach (var y in new[] { 0.5, Math.Round(size.Height / 2) + 0.5, size.Height - 0.5 })
        {
            context.DrawLine(grid, new Point(0, y), new Point(size.Width, y));
        }

        if (Samples is not { Count: > 0 } samples)
        {
            return;
        }

        DrawSeries(context, samples, s => s.UpPerSecond, UploadBrush);
        DrawSeries(context, samples, s => s.DownPerSecond, DownloadBrush);

        if (_hover is { } index && index < samples.Count)
        {
            var x = X(index, samples.Count);
            context.DrawLine(grid, new Point(x, 0), new Point(x, size.Height));
            DrawDot(context, new Point(x, Y(samples[index].UpPerSecond)), UploadBrush);
            DrawDot(context, new Point(x, Y(samples[index].DownPerSecond)), DownloadBrush);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        if (Samples is not { Count: > 0 } samples)
        {
            SetHover(null, null);
            return;
        }

        // Ближайшая точка по горизонтали; левее первой точки — первая.
        var slot = Bounds.Width / Math.Max(1, Capacity - 1);
        var fromRight = (int)Math.Round((Bounds.Width - e.GetPosition(this).X) / slot);
        var index = Math.Clamp(samples.Count - 1 - fromRight, 0, samples.Count - 1);
        var sample = samples[index];
        SetHover(index, Localizer.Format("ChartTooltipFormat", Localizer.Rate(sample.UpPerSecond), Localizer.Rate(sample.DownPerSecond)));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null, null);
    }

    private void SetHover(int? index, string? text)
    {
        if (_hover == index)
        {
            return;
        }

        _hover = index;
        ToolTip.SetTip(this, text);
        ToolTip.SetIsOpen(this, text is not null);
        InvalidateVisual();
    }

    private double X(int index, int count) => Bounds.Width - ((count - 1 - index) * Bounds.Width / Math.Max(1, Capacity - 1));

    // Линия толщиной 2 не должна обрезаться у краёв: шкала — от 1 до высоты − 1.
    private double Y(double value) => Bounds.Height - 1 - (Math.Clamp(value / Math.Max(1, Maximum), 0, 1) * (Bounds.Height - 2));

    private void DrawSeries(DrawingContext context, IReadOnlyList<TrafficSample> samples, Func<TrafficSample, double> value, IBrush? brush)
    {
        if (samples.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(X(0, samples.Count), Y(value(samples[0]))), isFilled: false);
            for (var i = 1; i < samples.Count; i++)
            {
                figure.LineTo(new Point(X(i, samples.Count), Y(value(samples[i]))));
            }

            figure.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    private void DrawDot(DrawingContext context, Point center, IBrush? brush) =>
        context.DrawEllipse(brush, new Pen(SurfaceBrush, 2), center, 4, 4);
}

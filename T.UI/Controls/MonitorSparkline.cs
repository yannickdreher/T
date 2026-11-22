using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using T.UI.ViewModels;

namespace T.UI.Controls;

/// <summary>
/// Five-minute history of one metric (optionally a second series, e.g. network send). Missing
/// samples are gaps. The primary series gets a translucent area under the line.
/// </summary>
public sealed class MonitorSparkline : Control
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static readonly StyledProperty<IReadOnlyList<MonitorPoint>?> PointsProperty =
        AvaloniaProperty.Register<MonitorSparkline, IReadOnlyList<MonitorPoint>?>(nameof(Points));
    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<MonitorSparkline, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty =
        AvaloniaProperty.Register<MonitorSparkline, IBrush?>(nameof(SecondaryBrush));

    static MonitorSparkline() => AffectsRender<MonitorSparkline>(PointsProperty, LineBrushProperty, SecondaryBrushProperty);

    public IReadOnlyList<MonitorPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    /// <summary>Second series (drawn dashed); its presence also switches the scale from percent to the data maximum.</summary>
    public IBrush? SecondaryBrush { get => GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Points is not { Count: > 0 } points || Bounds.Width <= 2 || Bounds.Height <= 2 || LineBrush is not { } primary)
            return;

        double width = Bounds.Width - 2;
        double height = Bounds.Height - 2;
        double maximum = SecondaryBrush is null
            ? 100d
            : Math.Max(1d, points.Max(point => Math.Max(point.Value ?? 0, point.Other ?? 0)));
        var end = points[^1].Time;

        Point ToPoint(MonitorPoint sample, double value) => new(
            1 + width * Math.Clamp(1 - (end - sample.Time).TotalSeconds / Window.TotalSeconds, 0, 1),
            1 + height * (1 - Math.Clamp(value / maximum, 0, 1)));

        // Contiguous runs of samples; a missing sample starts a new run (gap).
        List<List<Point>> Runs(bool secondary)
        {
            var runs = new List<List<Point>>();
            List<Point>? run = null;
            foreach (var sample in points)
            {
                if ((secondary ? sample.Other : sample.Value) is not { } value)
                {
                    run = null;
                    continue;
                }
                if (run is null) runs.Add(run = []);
                run.Add(ToPoint(sample, value));
            }
            return runs;
        }

        var primaryRuns = Runs(secondary: false);

        // Area under the primary line.
        using (context.PushOpacity(0.18))
        {
            foreach (var run in primaryRuns.Where(r => r.Count > 1))
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(run[0].X, 1 + height), true);
                    foreach (var p in run) g.LineTo(p);
                    g.LineTo(new Point(run[^1].X, 1 + height));
                    g.EndFigure(true);
                }
                context.DrawGeometry(primary, null, area);
            }
        }

        DrawRuns(primaryRuns, new Pen(primary, 1.5, lineJoin: PenLineJoin.Round));
        if (SecondaryBrush is { } other)
            DrawRuns(Runs(secondary: true), new Pen(other, 1.5, DashStyle.Dash, lineJoin: PenLineJoin.Round));

        void DrawRuns(List<List<Point>> runs, Pen pen)
        {
            foreach (var run in runs)
            {
                if (run.Count == 1)
                {
                    context.DrawEllipse(pen.Brush, null, run[0], 1.2, 1.2);
                    continue;
                }
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(run[0], false);
                    foreach (var p in run.Skip(1)) g.LineTo(p);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, line);
            }
        }
    }
}

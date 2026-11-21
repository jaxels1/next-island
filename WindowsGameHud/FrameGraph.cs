// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SapphireGameHud;

public sealed class FrameGraph : FrameworkElement
{
    private static readonly Pen Track = MakePen(0x32, 0x39, 0x47, 1);
    private static readonly Pen Reference60 = MakePen(0x26, 0xAE, 0xB3, 0xBD, 0.75);
    private static readonly Pen Trace = MakePen(0x8B, 0xE8, 0xB7, 1.35);
    private static readonly Pen SpikeTrace = MakePen(0xF5, 0xC7, 0x6B, 1.6);
    private static readonly Brush AreaFill = MakeAreaFill();
    private IReadOnlyList<double> samples = Array.Empty<double>();

    internal void SetSamples(IReadOnlyList<double> values)
    {
        if (ReferenceEquals(samples, values)) return;
        samples = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;
        var baseline = height - 2;
        drawing.DrawLine(Track, new Point(0, baseline), new Point(width, baseline));
        var referenceY = baseline - Math.Clamp(16.6 / 50, 0, 1) * (height - 4);
        drawing.DrawLine(Reference60, new Point(0, referenceY), new Point(width, referenceY));
        if (samples.Count < 2) return;

        var points = new Point[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            var x = 1 + i * (width - 2) / (samples.Count - 1);
            // Keep a fixed scale so spikes remain visually comparable from one update to the next.
            var y = baseline - Math.Clamp(samples[i] / 50, 0, 1) * (height - 4);
            points[i] = new Point(x, y);
        }

        var area = new StreamGeometry();
        using (var path = area.Open())
        {
            path.BeginFigure(new Point(points[0].X, baseline), true, true);
            for (var i = 0; i < points.Length; i++) path.LineTo(points[i], true, false);
            path.LineTo(new Point(points[^1].X, baseline), true, false);
        }
        area.Freeze();
        drawing.DrawGeometry(AreaFill, null, area);

        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Length; i++) path.LineTo(points[i], true, false);
        }
        line.Freeze();
        drawing.DrawGeometry(null, Trace, line);

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1] > 25 || samples[i] > 25)
                drawing.DrawLine(SpikeTrace, points[i - 1], points[i]);
        }
    }

    private static Brush MakeAreaFill()
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(0x38, 0x8B, 0xE8, 0xB7),
            Color.FromArgb(0x00, 0x8B, 0xE8, 0xB7),
            new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(byte red, byte green, byte blue, double thickness) =>
        MakePen(0xFF, red, green, blue, thickness);

    private static Pen MakePen(byte alpha, byte red, byte green, byte blue, double thickness)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}

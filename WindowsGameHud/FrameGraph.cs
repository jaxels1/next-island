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
    private static readonly Pen Trace = MakePen(0xA8, 0xED, 0xCC, 1.25);
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
        drawing.DrawLine(Track, new Point(0, height - 2), new Point(width, height - 2));
        if (samples.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var i = 0; i < samples.Count; i++)
            {
                var x = 1 + i * (width - 2) / (samples.Count - 1);
                // Keep a fixed scale so spikes remain visually comparable from one update to the next.
                var y = height - 2 - Math.Clamp(samples[i] / 50, 0, 1) * (height - 4);
                if (i == 0) path.BeginFigure(new Point(x, y), false, false);
                else path.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, Trace, geometry);
    }

    private static Pen MakePen(byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(red, green, blue)), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}

// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the island's bezel transition.
using System;
using System.Windows;
using System.Windows.Media;

namespace SapphireGameHud;

public sealed class IslandSurface : FrameworkElement
{
    private static readonly Brush BodyFill = CreateBodyFill();
    private static readonly Pen SpecularRim = CreateSpecularRim();
    private static readonly Pen OuterShadow = CreateShadowPen(0x30, 3);
    private static readonly Pen InnerShadow = CreateShadowPen(0x48, 1.5);
    private Rect body;
    private Geometry outline = Geometry.Empty;
    private double attachment;
    private double musicPocket;
    private double musicRowHeight;
    private double musicWidth;
    private Color? accentColor;
    private Pen? pocketRim;

    internal void SetShape(Rect bounds, Geometry shape, double neck, double music, double rowHeight, double pocketWidth, Color? accent = null)
    {
        body = bounds;
        outline = shape;
        attachment = neck;
        musicPocket = music;
        musicRowHeight = rowHeight;
        musicWidth = pocketWidth;
        if (accentColor != accent)
        {
            accentColor = accent;
            pocketRim = accent is { } tint
                ? Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x38, tint.R, tint.G, tint.B))), 0.75))
                : null;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (body.Width <= 1 || body.Height <= 1) return;

        drawing.PushTransform(new TranslateTransform(body.X, body.Y));
        var surface = outline;
        if (attachment > 0.001 && body.Top > 0.5)
        {
            var center = body.Width / 2;
            var stretch = Math.Clamp(body.Top / 12, 0, 1);
            var root = Math.Min(body.Width * 0.30, (20 + 43 * attachment) * attachment);
            var shoulder = Math.Min(body.Width * 0.32, root * 1.15 + 2);
            var waist = root * (0.18 + 0.50 * (1 - stretch));
            var neck = new StreamGeometry();
            using (var path = neck.Open())
            {
                path.BeginFigure(new Point(center - root, -body.Top - 2), true, true);
                path.BezierTo(new Point(center - waist, 1 - body.Top), new Point(center - waist, -body.Top * .24),
                    new Point(center - shoulder, 3), true, true);
                path.LineTo(new Point(center + shoulder, 3), true, false);
                path.BezierTo(new Point(center + waist, -body.Top * .24), new Point(center + waist, 1 - body.Top),
                    new Point(center + root, -body.Top - 2), true, true);
            }
            surface = new CombinedGeometry(GeometryCombineMode.Union, outline, neck);
        }

        if (body.Height >= 6)
        {
            drawing.PushTransform(new TranslateTransform(0, 1.5));
            drawing.DrawGeometry(null, OuterShadow, surface);
            drawing.DrawGeometry(null, InnerShadow, surface);
            drawing.Pop();
        }

        drawing.DrawGeometry(BodyFill, SpecularRim, surface);
        DrawMusicPocket(drawing);
        drawing.Pop();
    }

    internal static Geometry CreateBody(double width, double height, double radius, double game, double rowHeight, double controlsWidth)
    {
        var extension = height - rowHeight;
        if (game <= .001 || extension <= 0)
            return Freeze(new RectangleGeometry(new Rect(0, 0, width, height), radius, radius));

        var topRadius = rowHeight / 2;
        var top = new RectangleGeometry(new Rect(0, 0, width, rowHeight), topRadius, topRadius);
        var panelWidth = width - (width - Math.Min(width, controlsWidth)) * game;
        var panelLeft = (width - panelWidth) / 2;
        var panelRight = panelLeft + panelWidth;
        var join = Math.Min(panelLeft, Math.Min(14, extension / 2));
        var panelRadius = Math.Min(26, extension / 2);
        var panel = new StreamGeometry();
        using (var path = panel.Open())
        {
            var y = topRadius;
            path.BeginFigure(new Point(panelLeft - join, y), true, true);
            path.LineTo(new Point(panelRight + join, y), true, false);
            path.BezierTo(new Point(panelRight + join, y + topRadius), new Point(panelRight, y + topRadius),
                new Point(panelRight, y + topRadius + join), true, false);
            path.LineTo(new Point(panelRight, height - panelRadius), true, false);
            path.QuadraticBezierTo(new Point(panelRight, height), new Point(panelRight - panelRadius, height), true, false);
            path.LineTo(new Point(panelLeft + panelRadius, height), true, false);
            path.QuadraticBezierTo(new Point(panelLeft, height), new Point(panelLeft, height - panelRadius), true, false);
            path.LineTo(new Point(panelLeft, y + topRadius + join), true, false);
            path.BezierTo(new Point(panelLeft, y + topRadius), new Point(panelLeft - join, y + topRadius),
                new Point(panelLeft - join, y), true, false);
        }
        return Freeze(new CombinedGeometry(GeometryCombineMode.Union, top, panel));
    }

    private void DrawMusicPocket(DrawingContext drawing)
    {
        if (musicPocket <= .01 || musicWidth < 12) return;
        var row = Math.Min(body.Height, musicRowHeight);
        var pocketHeight = Math.Max(2, (row - 6) * (.45 + .55 * musicPocket));
        var pocketWidth = Math.Min(musicWidth - 6, body.Width - 12);
        if (pocketWidth <= 0) return;
        var pocket = new Rect(body.Width - pocketWidth - 4, (row - pocketHeight) / 2, pocketWidth, pocketHeight);
        drawing.PushClip(outline);
        drawing.PushOpacity(Math.Min(1, musicPocket * 2));
        drawing.DrawRoundedRectangle((Brush)FindResource("Recessed"), pocketRim, pocket, pocketHeight / 2, pocketHeight / 2);
        drawing.Pop();
        drawing.Pop();
    }

    private static Brush CreateBodyFill()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(0x14, 0x16, 0x1C),
            Color.FromRgb(0x0C, 0x0D, 0x11),
            new Point(0, 0), new Point(0, 1));
        return Freeze(brush);
    }

    private static Pen CreateSpecularRim()
    {
        var rimBrush = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x45, 0x49, 0x56), 0.0),
            new(Color.FromRgb(0x26, 0x28, 0x30), 0.45),
            new(Color.FromRgb(0x1C, 0x1E, 0x24), 1.0)
        }, new Point(0, 0), new Point(0, 1));
        return Freeze(new Pen(Freeze(rimBrush), 1));
    }

    private static Pen CreateShadowPen(byte alpha, double thickness) =>
        Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))), thickness));

    private static T Freeze<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}

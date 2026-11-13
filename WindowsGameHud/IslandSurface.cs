// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the island's bezel transition.
using System;
using System.Windows;
using System.Windows.Media;

namespace SapphireGameHud;

public sealed class IslandSurface : FrameworkElement
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromRgb(13, 14, 17)));
    private static readonly Pen Rim = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(49, 51, 57))), 1));

    private Rect body;
    private double radius;
    private double attachment;
    private double musicPocket;
    private double musicRowHeight;
    private double gameBlend;

    internal void SetShape(Rect bounds, double corner, double neck, double music, double rowHeight, double game)
    {
        if (bounds == body && corner == radius && neck == attachment && music == musicPocket &&
            rowHeight == musicRowHeight && game == gameBlend) return;
        body = bounds;
        radius = corner;
        attachment = neck;
        musicPocket = music;
        musicRowHeight = rowHeight;
        gameBlend = game;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (body.Width <= 1 || body.Height <= 1) return;

        var rounded = BodyGeometry();
        if (attachment <= 0.001 || body.Top <= 0.5)
        {
            drawing.DrawGeometry(Fill, Rim, rounded);
            DrawMusicPocket(drawing, rounded);
            return;
        }

        var center = body.X + body.Width / 2;
        var stretch = Math.Clamp(body.Top / 12, 0, 1);
        var root = Math.Min(body.Width * 0.30, (20 + 43 * attachment) * attachment);
        var shoulder = Math.Min(body.Width * 0.32, root * 1.15 + 2);
        var waist = root * (0.18 + 0.50 * (1 - stretch));
        var neck = new StreamGeometry();
        using (var path = neck.Open())
        {
            path.BeginFigure(new Point(center - root, -2), true, true);
            path.BezierTo(new Point(center - waist, 1), new Point(center - waist, body.Top * 0.76),
                new Point(center - shoulder, body.Top + 3), true, true);
            path.LineTo(new Point(center + shoulder, body.Top + 3), true, false);
            path.BezierTo(new Point(center + waist, body.Top * 0.76), new Point(center + waist, 1),
                new Point(center + root, -2), true, true);
        }
        drawing.DrawGeometry(Fill, Rim, new CombinedGeometry(GeometryCombineMode.Union, rounded, neck));
    }

    private Geometry BodyGeometry()
    {
        var extension = body.Height - musicRowHeight;
        if (gameBlend < .5 || extension <= .5)
            return new RectangleGeometry(body, radius, radius);

        var topRadius = musicRowHeight / 2;
        var top = new RectangleGeometry(new Rect(body.X, body.Y, body.Width, musicRowHeight), topRadius, topRadius);
        var panelWidth = Math.Min(body.Width, 344);
        var panelLeft = body.X + (body.Width - panelWidth) / 2;
        var panelRight = panelLeft + panelWidth;
        var join = Math.Min(14, extension / 2);
        var panelRadius = Math.Min(24, extension / 2);
        var panel = new StreamGeometry();
        using (var path = panel.Open())
        {
            var y = body.Top + musicRowHeight / 2;
            path.BeginFigure(new Point(panelLeft - join, y), true, true);
            path.LineTo(new Point(panelRight + join, y), true, false);
            path.BezierTo(new Point(panelRight + join, y + topRadius), new Point(panelRight, y + topRadius),
                new Point(panelRight, y + topRadius + join), true, false);
            path.LineTo(new Point(panelRight, body.Bottom - panelRadius), true, false);
            path.QuadraticBezierTo(new Point(panelRight, body.Bottom), new Point(panelRight - panelRadius, body.Bottom), true, false);
            path.LineTo(new Point(panelLeft + panelRadius, body.Bottom), true, false);
            path.QuadraticBezierTo(new Point(panelLeft, body.Bottom), new Point(panelLeft, body.Bottom - panelRadius), true, false);
            path.LineTo(new Point(panelLeft, y + topRadius + join), true, false);
            path.BezierTo(new Point(panelLeft, y + topRadius), new Point(panelLeft - join, y + topRadius),
                new Point(panelLeft - join, y), true, false);
        }
        return new CombinedGeometry(GeometryCombineMode.Union, top, panel);
    }

    private void DrawMusicPocket(DrawingContext drawing, Geometry outline)
    {
        if (musicPocket <= .01 || musicPocket >= .995) return;
        var pocketHeight = Math.Min(body.Height, musicRowHeight) * (.35 + .6 * musicPocket);
        var pocketWidth = Math.Min(156 * musicPocket, body.Width - 12);
        var pocket = new Rect(body.Right - pocketWidth - 5, body.Top + (Math.Min(body.Height, musicRowHeight) - pocketHeight) / 2,
            pocketWidth, pocketHeight);
        drawing.PushClip(outline);
        drawing.PushOpacity(Math.Sin(Math.PI * musicPocket) * .8);
        drawing.DrawRoundedRectangle(PocketFill, null, pocket, pocketHeight / 2, pocketHeight / 2);
        drawing.Pop();
        drawing.Pop();
    }

    private static readonly Brush PocketFill = Freeze(new SolidColorBrush(Color.FromRgb(49, 51, 60)));

    private static T Freeze<T>(T item) where T : Freezable
    {
        item.Freeze();
        return item;
    }
}

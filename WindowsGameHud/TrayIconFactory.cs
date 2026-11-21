// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SapphireGameHud;

internal static class TrayIconFactory
{
    internal static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var pill = CreateRoundedRect(new RectangleF(1.5f, 7.5f, 29f, 17f), 8.5f);
            using var fill = new SolidBrush(Color.FromArgb(0x11, 0x13, 0x18));
            using var rim = new Pen(Color.FromArgb(0x3D, 0x42, 0x4F), 1.5f);
            graphics.FillPath(fill, pill);
            graphics.DrawPath(rim, pill);

            using var barBrush = new SolidBrush(Color.FromArgb(0xC7, 0xD6, 0xF6));
            float[] heights = [6f, 10f, 8f, 5f];
            const float barWidth = 2.2f;
            const float gap = 2.3f;
            var startX = 16f - (heights.Length * barWidth + (heights.Length - 1) * gap) / 2f;
            for (var i = 0; i < heights.Length; i++)
            {
                var h = heights[i];
                var x = startX + i * (barWidth + gap);
                var y = 16f - h / 2f;
                using var bar = CreateRoundedRect(new RectangleF(x, y, barWidth, h), barWidth / 2f);
                graphics.FillPath(barBrush, bar);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}

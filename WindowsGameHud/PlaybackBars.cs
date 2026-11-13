// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SapphireGameHud;

public sealed class PlaybackBars : FrameworkElement
{
    private static readonly Brush Ink = CreateInk();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly double[] bars = [.32, .32, .32, .32];
    private long startedAt;
    private bool playing;

    public PlaybackBars()
    {
        timer.Tick += (_, _) => UpdateBars();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    internal void SetPlaying(bool value)
    {
        if (playing == value) return;
        playing = value;
        if (value) startedAt = Environment.TickCount64;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (IsVisible && SystemParameters.ClientAreaAnimation && (playing || Array.Exists(bars, height => height > .33)))
            timer.Start();
        else
        {
            timer.Stop();
            Array.Fill(bars, .32);
            InvalidateVisual();
        }
    }

    private void UpdateBars()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            UpdateTimer();
            return;
        }
        var time = (Environment.TickCount64 - startedAt) / 1000d;
        for (var i = 0; i < bars.Length; i++)
        {
            var wave = .5 + .5 * Math.Sin(time * (8.5 + i * 1.6) + i * 1.7);
            var target = playing ? .32 + .68 * wave : .32;
            bars[i] += (target - bars[i]) * .48;
        }
        InvalidateVisual();
        if (!playing) UpdateTimer();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        for (var i = 0; i < bars.Length; i++)
        {
            var height = Math.Max(2, ActualHeight * bars[i]);
            var x = (ActualWidth - 14) / 2 + i * 4;
            drawing.DrawRoundedRectangle(Ink, null, new Rect(x, (ActualHeight - height) / 2, 2, height), 1, 1);
        }
    }

    private static Brush CreateInk()
    {
        var ink = new SolidColorBrush(Color.FromRgb(214, 219, 233));
        ink.Freeze();
        return ink;
    }
}

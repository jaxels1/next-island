// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SapphireGameHud;

internal enum PlaybackChange { None, Started, Paused, Resumed, Next, Previous, Advanced, Changed, Restarted, Ended, Stopped }
internal enum TrackDirection { None, Forward, Backward }

internal static class PlaybackMotion
{
    internal static PlaybackChange Observe(MediaReading before, MediaReading after, double elapsed,
        TrackDirection direction, bool seeking)
    {
        var sameSource = before.Source == after.Source;
        var nearEnd = before.Playing && before.Duration > 3 &&
            before.Position + Math.Max(0, elapsed) * before.PlaybackRate >= before.Duration - 1.5;
        if (!after.HasTrack)
            return before.HasTrack ? nearEnd ? PlaybackChange.Ended : PlaybackChange.Stopped : PlaybackChange.None;
        if (!before.HasTrack) return after.Playing ? PlaybackChange.Started : PlaybackChange.Changed;
        if (!sameSource) return PlaybackChange.Changed;
        if (before.Title != after.Title || before.Artist != after.Artist)
        {
            return direction switch
            {
                TrackDirection.Forward => PlaybackChange.Next,
                TrackDirection.Backward => PlaybackChange.Previous,
                _ => nearEnd ? PlaybackChange.Advanced : PlaybackChange.Changed
            };
        }

        // Previous can restart the same track. A looping track can also reset its position.
        if (!seeking && after.Position < 2 && before.Position >= 3)
        {
            if (direction == TrackDirection.Backward) return PlaybackChange.Restarted;
            if (direction == TrackDirection.None && after.Playing && nearEnd) return PlaybackChange.Advanced;
        }
        if (before.Playing && !after.Playing)
        {
            if (nearEnd && (after.Stopped || !seeking && after.Position >= before.Duration - .5)) return PlaybackChange.Ended;
            return after.Stopped ? PlaybackChange.Stopped : PlaybackChange.Paused;
        }
        return !before.Playing && after.Playing ? PlaybackChange.Resumed : PlaybackChange.None;
    }

    internal static bool ReplacesTrack(PlaybackChange change) => change is PlaybackChange.Started or PlaybackChange.Next
        or PlaybackChange.Previous or PlaybackChange.Advanced or PlaybackChange.Changed or PlaybackChange.Ended or PlaybackChange.Stopped;

    internal static double Offset(PlaybackChange change, double distance) => change switch
    {
        PlaybackChange.Next => distance,
        PlaybackChange.Previous or PlaybackChange.Restarted => -distance,
        _ => 0
    };

    internal static void Arrive(FrameworkElement element, double offset, int milliseconds, double startOpacity = .5)
    {
        var animate = element.IsVisible && SystemParameters.ClientAreaAnimation;
        // A second track or tab change can arrive mid-transition. Continue from the
        // visible position and opacity instead of jumping back to the entrance.
        if (element.RenderTransform is not TranslateTransform translation || translation.IsFrozen)
        {
            translation = new TranslateTransform();
            element.RenderTransform = translation;
        }
        // WPF keeps completed clocks attached with FillBehavior.Stop. Inspect the
        // visible values so a stopped clock cannot suppress all later arrivals.
        var from = Math.Abs(translation.X) > .001 ? translation.X : offset;
        var opacity = element.Opacity < .999 ? element.Opacity : startOpacity;
        translation.X = 0;
        translation.BeginAnimation(TranslateTransform.XProperty, animate ? To(from, 0, milliseconds) : null);
        element.Opacity = 1;
        element.BeginAnimation(UIElement.OpacityProperty, animate ? To(opacity, 1, milliseconds) : null);
    }

    internal static void EaseTo(Animatable element, DependencyProperty property, double target, int milliseconds, bool animate)
    {
        var current = (double)element.GetValue(property);
        element.SetValue(property, target);
        element.BeginAnimation(property, animate ? To(current, target, milliseconds) : null);
    }

    internal static DoubleAnimation To(double from, double to, int milliseconds) => new(from, to,
        TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };
}

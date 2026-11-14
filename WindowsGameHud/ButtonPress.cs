// SPDX-License-Identifier: AGPL-3.0-only
using System.Windows;
using System.Windows.Media;

namespace SapphireGameHud;

public static class ButtonPress
{
    public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
        "IsPressed", typeof(bool), typeof(ButtonPress), new PropertyMetadata(false, OnPressedChanged));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(ButtonPress), new PropertyMetadata(.96));

    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.RegisterAttached(
        "OffsetX", typeof(double), typeof(ButtonPress), new PropertyMetadata(0d, OnOffsetChanged));

    public static double GetOffsetX(DependencyObject element) => (double)element.GetValue(OffsetXProperty);
    public static void SetOffsetX(DependencyObject element, double value) => element.SetValue(OffsetXProperty, value);

    private static void OnOffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement visual) return;
        if (visual.RenderTransform is not TranslateTransform offset || offset.IsFrozen)
            visual.RenderTransform = offset = new TranslateTransform();
        PlaybackMotion.EaseTo(offset, TranslateTransform.XProperty, (double)args.NewValue, 160,
            visual.IsVisible && SystemParameters.ClientAreaAnimation);
    }

    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);
    public static void SetIsPressed(DependencyObject element, bool value) => element.SetValue(IsPressedProperty, value);
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);

    private static void OnPressedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement visual) return;

        if (visual.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            visual.RenderTransformOrigin = new Point(.5, .5);
            visual.RenderTransform = scale;
        }

        var pressed = (bool)args.NewValue;
        var animate = visual.IsVisible && SystemParameters.ClientAreaAnimation;
        var target = pressed && animate ? GetScale(visual) : 1;
        var duration = pressed ? 100 : 160;
        PlaybackMotion.EaseTo(scale, ScaleTransform.ScaleXProperty, target, duration, animate);
        PlaybackMotion.EaseTo(scale, ScaleTransform.ScaleYProperty, target, duration, animate);
    }
}

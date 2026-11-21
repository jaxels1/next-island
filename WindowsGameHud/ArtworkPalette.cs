// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SapphireGameHud;

internal static class ArtworkPalette
{
    private const int HueBuckets = 12;
    internal static readonly Color DefaultAccent = Color.FromRgb(0xC7, 0xD6, 0xF6);

    internal static Color ExtractAccent(BitmapSource? artwork)
    {
        if (artwork is null || artwork.PixelWidth < 1 || artwork.PixelHeight < 1)
            return DefaultAccent;

        try
        {
            var source = artwork.Format == PixelFormats.Bgra32 || artwork.Format == PixelFormats.Pbgra32
                ? artwork
                : new FormatConvertedBitmap(artwork, PixelFormats.Bgra32, null, 0);
            var premultiplied = source.Format == PixelFormats.Pbgra32;

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);

            var stepX = Math.Max(1, width / 16);
            var stepY = Math.Max(1, height / 16);

            var bucketR = new double[HueBuckets];
            var bucketG = new double[HueBuckets];
            var bucketB = new double[HueBuckets];
            var bucketWeight = new double[HueBuckets];
            double maxSaturation = 0;

            for (var y = stepY / 2; y < height; y += stepY)
            {
                var row = y * stride;
                for (var x = stepX / 2; x < width; x += stepX)
                {
                    var i = row + x * 4;
                    var a = pixels[i + 3] / 255d;
                    if (a < 0.5) continue;

                    var b = pixels[i] / 255d;
                    var g = pixels[i + 1] / 255d;
                    var r = pixels[i + 2] / 255d;
                    if (premultiplied && a < 1d)
                    {
                        b = Math.Min(1d, b / a);
                        g = Math.Min(1d, g / a);
                        r = Math.Min(1d, r / a);
                    }

                    var (hue, sat, lum) = RgbToHsl(r, g, b);
                    if (sat > maxSaturation) maxSaturation = sat;
                    if (sat < 0.14 || lum < 0.10 || lum > 0.92) continue;

                    var midLumWeight = 1.0 - Math.Abs(lum - 0.52) * 1.25;
                    var weight = a * sat * sat * Math.Max(0.2, midLumWeight);
                    var bucket = Math.Clamp((int)(hue * HueBuckets) % HueBuckets, 0, HueBuckets - 1);
                    bucketR[bucket] += r * weight;
                    bucketG[bucket] += g * weight;
                    bucketB[bucket] += b * weight;
                    bucketWeight[bucket] += weight;
                }
            }

            if (maxSaturation < 0.14) return DefaultAccent;

            var best = -1;
            var bestWeight = 0d;
            for (var i = 0; i < HueBuckets; i++)
            {
                if (bucketWeight[i] > bestWeight)
                {
                    bestWeight = bucketWeight[i];
                    best = i;
                }
            }

            if (best < 0 || bestWeight <= 0) return DefaultAccent;

            var (h, s, l) = RgbToHsl(bucketR[best] / bestWeight, bucketG[best] / bestWeight, bucketB[best] / bestWeight);
            if (s < 0.14) return DefaultAccent;

            s = Math.Clamp(s, 0.42, 0.80);
            l = Math.Clamp(l, 0.68, 0.82);
            return HslToRgb(h, s, l);
        }
        catch (Exception)
        {
            return DefaultAccent;
        }
    }

    internal static string? FormatSourceApp(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return null;

        if (Contains(sourceId, "Spotify")) return "Spotify";
        if (Contains(sourceId, "ZuneMusic") || Contains(sourceId, "Microsoft.Media.Player")) return "Media Player";
        if (Contains(sourceId, "AppleMusic") || Contains(sourceId, "AppleInc.AppleMusic")) return "Apple Music";
        if (Contains(sourceId, "chrome")) return "Chrome";
        if (Contains(sourceId, "msedge")) return "Edge";
        if (Contains(sourceId, "firefox")) return "Firefox";
        if (Contains(sourceId, "brave")) return "Brave";
        if (Contains(sourceId, "opera")) return "Opera";
        if (Contains(sourceId, "vlc")) return "VLC";
        if (Contains(sourceId, "tidal")) return "TIDAL";
        if (Contains(sourceId, "foobar")) return "foobar2000";
        if (Contains(sourceId, "discord")) return "Discord";
        if (Contains(sourceId, "musicbee")) return "MusicBee";
        if (Contains(sourceId, "aimp")) return "AIMP";
        return null;
    }

    private static bool Contains(string source, string token) =>
        source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var delta = max - min;
        if (delta <= 1e-6) return (0, 0, l);

        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;
        return (h / 6, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 1e-6)
        {
            var gray = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
            return Color.FromRgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var r = HueToChannel(p, q, h + 1d / 3);
        var g = HueToChannel(p, q, h);
        var b = HueToChannel(p, q, h - 1d / 3);
        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1d / 6) return p + (q - p) * 6 * t;
        if (t < 1d / 2) return q;
        if (t < 2d / 3) return p + (q - p) * (2d / 3 - t) * 6;
        return p;
    }
}

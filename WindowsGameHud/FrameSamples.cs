// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SapphireGameHud;

internal sealed record FrameReading(double FramesPerSecond, double Milliseconds, double[] History);

internal sealed class FrameSamples(int processId)
{
    private const int HistoryLength = 72;
    private const int MaximumSamplesPerChain = 512;
    private const long FreshnessMs = 2_000;
    private readonly Dictionary<string, Queue<(long Time, double Ms)>> chains = new(StringComparer.Ordinal);
    private int pidColumn = -1;
    private int chainColumn = -1;
    private int timeColumn = -1;

    internal void Append(string line, long now)
    {
        if (line.Length is 0 or > 8192) return;
        var cells = ReadCsv(line);
        if (cells.Count == 0) return;
        if (cells[0].TrimStart('\uFEFF').Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            pidColumn = FindColumn(cells, "ProcessID");
            chainColumn = FindColumn(cells, "SwapChainAddress");
            timeColumn = FindColumn(cells, "MsBetweenPresents");
            return;
        }

        if (pidColumn < 0 || chainColumn < 0 || timeColumn < 0
            || cells.Count <= Math.Max(pidColumn, Math.Max(chainColumn, timeColumn))
            || !int.TryParse(cells[pidColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid != processId
            || !double.TryParse(cells[timeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            || !double.IsFinite(ms) || ms <= 0 || ms > FreshnessMs)
            return;

        var chain = cells[chainColumn];
        if (chain.Length == 0) return;
        if (!chains.TryGetValue(chain, out var samples))
        {
            RemoveStaleChains(now);
            if (chains.Count >= 24) return;
            chains.Add(chain, samples = new Queue<(long, double)>(HistoryLength));
        }
        if (samples.Count == MaximumSamplesPerChain) samples.Dequeue();
        samples.Enqueue((now, ms));
    }

    internal FrameReading? Read(long now)
    {
        RemoveStaleChains(now);
        // Count recent presents separately for each swap chain so menus and helper surfaces
        // cannot double-count the main game's frame rate.
        var selected = chains.Values.OrderByDescending(samples => samples.Count).FirstOrDefault();
        if (selected is null || selected.Count < 2) return null;
        var history = selected.TakeLast(HistoryLength).Select(sample => sample.Ms).ToArray();
        var mean = history.Average();
        return new FrameReading(1000 / mean, mean, history);
    }

    private void RemoveStaleChains(long now)
    {
        foreach (var (chain, samples) in chains.ToArray())
        {
            while (samples.TryPeek(out var sample) && now - sample.Time > FreshnessMs)
                samples.Dequeue();
            if (samples.Count == 0) chains.Remove(chain);
        }
    }

    private static int FindColumn(List<string> cells, string name) =>
        cells.FindIndex(cell => cell.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadCsv(string line)
    {
        // PresentMon can quote executable names containing commas.
        var cells = new List<string>();
        var position = 0;
        while (position < line.Length)
        {
            if (line[position] == '"')
            {
                position++;
                var value = new System.Text.StringBuilder();
                var closed = false;
                while (position < line.Length)
                {
                    var character = line[position++];
                    if (character == '"')
                    {
                        if (position >= line.Length || line[position] != '"')
                        {
                            closed = true;
                            break;
                        }
                        position++;
                    }
                    value.Append(character);
                }
                if (!closed || position < line.Length && line[position] != ',') return [];
                cells.Add(value.ToString());
            }
            else
            {
                var end = line.IndexOf(',', position);
                if (end < 0) end = line.Length;
                cells.Add(line[position..end]);
                position = end;
            }
            if (position < line.Length) position++;
        }
        return cells;
    }
}

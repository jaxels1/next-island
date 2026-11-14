// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace SapphireGameHud;

internal readonly record struct UsageReading(double? Cpu, double? Gpu, double? Ram);
internal readonly record struct TemperatureReading(double? Cpu, double? Gpu);

internal sealed class HardwareTelemetry : IDisposable
{
    private (ulong Idle, ulong Total)? previousCpu;
    private nint gpuQuery;
    private nint gpuCounter;
    private long retryGpuAt;
    private const uint MoreData = 0x800007D2;
    private const uint FormatDouble = 0x00000200;

    internal UsageReading Read() => new(ReadCpu(), ReadGpu(), ReadRam());

    private static double? ReadRam()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : null;
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            previousCpu = null;
            return null;
        }
        var total = kernel.Value + user.Value;
        var old = previousCpu;
        previousCpu = (idle.Value, total);
        if (old is not { } last || total <= last.Total || idle.Value < last.Idle) return null;
        var elapsed = total - last.Total;
        var idleDelta = idle.Value - last.Idle;
        return Math.Clamp(100 * (1 - (double)idleDelta / elapsed), 0, 100);
    }

    private double? ReadGpu()
    {
        if (gpuCounter == 0)
        {
            var now = Environment.TickCount64;
            if (now < retryGpuAt) return null;
            retryGpuAt = now + 10_000;
            if (PdhOpenQuery(null, 0, out gpuQuery) != 0) return null;

            const string path = @"\GPU Engine(*)\Utilization Percentage";
            if (PdhAddEnglishCounter(gpuQuery, path, 0, out gpuCounter) != 0)
            {
                CloseGpuQuery();
                return null;
            }
        }

        if (PdhCollectQueryData(gpuQuery) != 0) return null;
        uint bytes = 0;
        uint count = 0;
        if (PdhGetFormattedCounterArray(gpuCounter, FormatDouble, ref bytes, ref count, 0) != MoreData
            || bytes == 0 || bytes > 16 * 1024 * 1024) return null;

        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArray(gpuCounter, FormatDouble, ref bytes, ref count, buffer) != 0)
                return null;
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var size = Marshal.SizeOf<CounterItem>();
            if (count > bytes / size) return null;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                if (item.Status > 1 || !double.IsFinite(item.Value) || item.Value < 0) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                var separator = name?.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase) ?? -1;
                if (name is null || separator < 0) continue;
                // The prefix is the process ID. Sum instances for the same physical engine.
                var engine = name[separator..];
                var duplicateIndex = engine.IndexOf('#');
                if (duplicateIndex >= 0) engine = engine[..duplicateIndex];
                engines[engine] = engines.GetValueOrDefault(engine) + item.Value;
            }
            return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        CloseGpuQuery();
        previousCpu = null;
    }

    private void CloseGpuQuery()
    {
        if (gpuQuery != 0) PdhCloseQuery(gpuQuery);
        gpuQuery = gpuCounter = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low, High;
        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical, AvailablePhysical;
        internal ulong TotalPageFile, AvailablePageFile;
        internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        internal nint Name;
        internal uint Status;
        internal double Value;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, nint data, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(nint query, string path, nint data, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}

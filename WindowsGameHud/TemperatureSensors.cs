// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 to read local CPU and GPU temperatures.
using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SapphireGameHud;

internal sealed class TemperatureSensors : IDisposable
{
    private Computer? computer;

    internal TemperatureReading Read()
    {
        try
        {
            if (computer is null)
            {
                computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                computer.Open();
            }

            double? cpu = null;
            double? gpu = null;
            foreach (var hardware in computer.Hardware)
            {
                try
                {
                    var readings = ReadTemperatures(hardware);
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        var value = Best(readings, name => name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                            ?? Best(readings, _ => true);
                        cpu = Max(cpu, value);
                    }
                    else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        var value = Best(readings, name => name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("GPU Edge", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase));
                        gpu = Max(gpu, value);
                    }
                }
                catch (Exception error) when (IsSensorError(error))
                {
                    // A missing driver for one device does not invalidate the other devices.
                }
            }
            return new TemperatureReading(cpu, gpu);
        }
        catch (Exception error) when (IsSensorError(error))
        {
            Dispose();
            return default;
        }
    }

    private static List<(string Name, double Value)> ReadTemperatures(IHardware hardware)
    {
        hardware.Update();
        var result = new List<(string, double)>();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value is float value
                && float.IsFinite(value) && value is > 0 and <= 150)
                result.Add((sensor.Name, value));
        }
        foreach (var child in hardware.SubHardware) result.AddRange(ReadTemperatures(child));
        return result;
    }

    private static double? Best(List<(string Name, double Value)> readings, Func<string, bool> matches) =>
        readings.Where(reading => matches(reading.Name)).Select(reading => (double?)reading.Value).Max();

    private static double? Max(double? left, double? right) =>
        left is { } a && right is { } b ? Math.Max(a, b) : left ?? right;

    private static bool IsSensorError(Exception error) =>
        error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException
            or TypeInitializationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException
            or ArgumentException;

    public void Dispose()
    {
        try { computer?.Close(); }
        catch (Exception error) when (IsSensorError(error)) { }
        finally { computer = null; }
    }
}

// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SapphireGameHud;

internal sealed class PresentMonCapture : IDisposable
{
    private readonly string executable;
    private readonly string session = $"SapphireGameHud-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly FrameSamples samples;
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private int disposed;
    private volatile bool exited;
    private volatile bool permissionDenied;

    internal PresentMonCapture(string executable, int processId)
    {
        this.executable = executable;
        samples = new FrameSamples(processId);
        worker = Task.Run(() => CaptureAsync(processId));
    }

    internal bool Exited => exited;
    internal bool PermissionDenied => permissionDenied;

    internal FrameReading? Read()
    {
        lock (sync) return samples.Read(Environment.TickCount64);
    }

    private async Task CaptureAsync(int processId)
    {
        Process? process = null;
        try
        {
            if (stop.IsCancellationRequested) return;
            var info = StartInfo("--session_name", session, "--process_id",
                processId.ToString(CultureInfo.InvariantCulture), "--output_stdout", "--v1_metrics",
                "--no_track_input", "--no_track_gpu", "--no_track_display", "--terminate_on_proc_exit");
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            process = Process.Start(info);
            if (process is null) return;
            var drainErrors = ReadErrors(process.StandardError);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(stop.Token) is { } line)
                {
                    lock (sync) samples.Append(line, Environment.TickCount64);
                }
            }
            finally
            {
                try { await drainErrors; }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            // Optional provider. The HUD can continue showing system metrics.
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        using var closer = Process.Start(StartInfo("--session_name", session, "--terminate_existing_session"));
                        if (closer is not null && !closer.WaitForExit(600)) closer.Kill();
                        if (!process.WaitForExit(250)) process.Kill();
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        // A child process may already have exited.
                    }
                }
                process.Dispose();
            }
            exited = true;
        }
    }

    private async Task ReadErrors(StreamReader errors)
    {
        while (await errors.ReadLineAsync(stop.Token) is { } line)
        {
            if (line.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase)
                || line.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || line.Contains("administrative privileges", StringComparison.OrdinalIgnoreCase))
                permissionDenied = true;
        }
    }

    private ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }

    internal Task Completion => worker;
}

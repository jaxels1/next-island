// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace SapphireGameHud;

public partial class App : Application
{
    private const string ReleaseRepository = "https://github.com/jaxels1/next-island";
    private Mutex? instance;
    private UpdateManager? updater;
    private DispatcherTimer? updateTimer;
    private readonly CancellationTokenSource updateLifetime = new();
    private bool checkingForUpdates;

    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    internal bool CanUpdate => updater is not null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SapphireGameHud", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        var manager = new UpdateManager(new GithubSource(ReleaseRepository, null, false));
        if (manager.IsInstalled) updater = manager;

        MainWindow = new HudWindow(e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase));
        MainWindow.Show();

        if (CanUpdate)
        {
            updateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromHours(6) };
            updateTimer.Tick += async (_, _) => await CheckForUpdates();
            updateTimer.Start();
            _ = CheckForUpdates();
        }
    }

    internal async Task<string> CheckForUpdates()
    {
        if (updater is not { } manager) return "Install Next Island to enable updates.";
        if (checkingForUpdates) return "An update check is already running.";

        checkingForUpdates = true;
        var cancellation = updateLifetime.Token;
        try
        {
            return await Task.Run(async () =>
            {
                if (manager.UpdatePendingRestart is null)
                {
                    var update = await manager.CheckForUpdatesAsync();
                    if (update is null) return "Next Island is up to date.";
                    cancellation.ThrowIfCancellationRequested();
                    await manager.DownloadUpdatesAsync(update, cancelToken: cancellation);
                }
                return "Update ready. It will install when you exit Next Island.";
            }, cancellation);
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Next Island update check failed: {0}", error.Message);
            return "Couldn’t check for updates. Try again later.";
        }
        finally { checkingForUpdates = false; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        updateTimer?.Stop();
        updateLifetime.Cancel();
        try
        {
            if (!checkingForUpdates && updater?.UpdatePendingRestart is { } pending)
                updater.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception error) { Trace.TraceWarning("Next Island could not apply the update: {0}", error.Message); }
        instance?.Dispose();
        base.OnExit(e);
    }
}

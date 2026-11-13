// SPDX-License-Identifier: AGPL-3.0-only
// Added 2026-11-12 for the local Windows gaming HUD.
using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace SapphireGameHud;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\SapphireGameHud", out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        MainWindow = new HudWindow(e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase));
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}

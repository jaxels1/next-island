// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SapphireGameHud;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NextIsland";
    private const string LegacyShortcutName = "Next Island.lnk";

    internal static bool IsSupported =>
        Environment.ProcessPath is { Length: > 0 } path
        && Path.GetFileName(path).Equals("NextIsland.exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value))
                return true;
        }
        catch (Exception error) when (IsRegistryOrFileError(error)) { }

        return File.Exists(LegacyShortcutPath);
    }

    internal static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!IsSupported || Environment.ProcessPath is not { Length: > 0 } path) return false;
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key is null) return false;
                key.SetValue(RunValueName, $"\"{path}\"", RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            if (File.Exists(LegacyShortcutPath))
                File.Delete(LegacyShortcutPath);
            return true;
        }
        catch (Exception error) when (IsRegistryOrFileError(error))
        {
            return false;
        }
    }

    private static string LegacyShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LegacyShortcutName);

    private static bool IsRegistryOrFileError(Exception error) =>
        error is UnauthorizedAccessException or IOException or SecurityException;
}

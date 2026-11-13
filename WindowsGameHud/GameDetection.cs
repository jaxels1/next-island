// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SapphireGameHud;

internal sealed class GameDetection
{
    private const string RobloxPlayerIdentity = "game:roblox-player";
    private const string PackageIdentityPrefix = "package:";
    private static readonly HashSet<string> RobloxPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROBLOXCorporation.RobloxGDK_55nm5eh3cm0pr", "ROBLOXCorporation.ROBLOX_55nm5eh3cm0pr"
    };

    private static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "opera_gx", "waterfox",
        "arc", "zen", "librewolf", "vlc", "mpv", "potplayer", "wmplayer", "microsoft.media.player",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "galaxyclient", "battle.net",
        "eadesktop", "ubisoftconnect", "upc", "riotclientservices", "robloxstudio", "robloxstudiobeta",
        "robloxplayerinstaller", "obs64", "applicationframehost"
    };

    private readonly string settingsPath;
    private readonly string epicManifests;
    private readonly Dictionary<string, bool> overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> detected = new(StringComparer.OrdinalIgnoreCase);

    internal bool ShowGraph { get; set; }
    internal bool LargeText { get; set; }

    internal GameDetection() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sapphire Island", "game-hud.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests")) { }

    internal GameDetection(string settingsPath, string epicManifests)
    {
        this.settingsPath = settingsPath;
        this.epicManifests = epicManifests;
        try
        {
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            if (settings?.Apps is { } apps)
                foreach (var (path, isGame) in apps)
                    if (RuleIdentity(path) is { } identity) overrides[identity] = isGame;
            ShowGraph = settings?.ShowGraph == true;
            LargeText = settings?.LargeText == true;
        }
        catch (Exception error) when (IsReadError(error)) { }
    }

    internal static string? IdentityFor(ForegroundWindow window)
    {
        if (IsRobloxPlayer(window)) return RobloxPlayerIdentity;
        if (window.PackageFamily is { Length: > 0 } family) return PackageIdentityPrefix + family;
        return ExecutablePath(window.Executable);
    }

    internal bool? OverrideFor(string? identity) =>
        RuleIdentity(identity) is { } key && overrides.TryGetValue(key, out var value) ? value : null;

    internal bool SetOverride(string identity, bool? value)
    {
        if (RuleIdentity(identity) is not { } key) return false;
        if (value is bool isGame) overrides[key] = isGame;
        else overrides.Remove(key);
        detected.Remove(key);
        return Save();
    }

    internal bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var pending = settingsPath + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(new Settings(overrides, ShowGraph, LargeText)));
            File.Move(pending, settingsPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (IsReadError(error)) { return false; }
    }

    internal bool IsFullscreenGame(ForegroundWindow window)
    {
        if (!window.IsFullscreen) return false;
        if (OverrideFor(IdentityFor(window)) is bool choice) return choice;
        if (NonGames.Contains(window.Name)) return false;
        if (IsRobloxPlayer(window)) return true;
        return ExecutablePath(window.Executable) is { } path && Detect(path);
    }

    private static bool IsRobloxPlayer(ForegroundWindow window) =>
        !NonGames.Contains(window.Name) &&
        ((window.PackageFamily is { } family && RobloxPackages.Contains(family))
            || string.Equals(window.Name, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase)
            || IsRobloxPlayerPath(window.Executable));

    private static bool IsRobloxPlayerPath(string? path) =>
        string.Equals(Path.GetFileName(path), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase);

    private bool Detect(string path)
    {
        if (detected.TryGetValue(path, out var value)) return value;
        var name = Path.GetFileNameWithoutExtension(path);
        value = !NonGames.Contains(name) && !name.EndsWith("launcher", StringComparison.OrdinalIgnoreCase)
            && (HasStandaloneGameFiles(path) || IsSteamGame(path) || IsEpicGame(path));
        return detected[path] = value;
    }

    private static bool HasStandaloneGameFiles(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } directory) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        var project = Directory.GetParent(directory)?.Parent;
        try
        {
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) &&
                Directory.Exists(Path.Combine(directory, name + "_Data"))) return true;
            if (IsGodotPack(Path.Combine(directory, name + ".pck"), embedded: false) ||
                IsGodotPack(path, embedded: true)) return true;

            if (name.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase) && project is not null &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Binaries", StringComparison.OrdinalIgnoreCase) &&
                HasUnrealContent(project.FullName)) return true;
        }
        catch (Exception error) when (IsReadError(error)) { }

        // Game metadata can live above a bin or binaries directory, including separate Xbox content volumes.
        var ancestor = new DirectoryInfo(directory);
        for (var depth = 0; depth < 4 && ancestor is { Parent: not null }; depth++, ancestor = ancestor.Parent)
            if (IsListedByLocalManifest(ancestor.FullName, path)) return true;
        return false;
    }

    private static bool HasUnrealContent(string project)
    {
        var packages = Path.Combine(project, "Content", "Paks");
        return Directory.Exists(packages) && Directory.EnumerateFiles(packages).Any(file =>
            Path.GetExtension(file).Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(file).Equals(".utoc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGodotPack(string path, bool embedded)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!embedded) return HasGodotHeader(stream, 0);

            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            foreach (var section in pe.PEHeaders.SectionHeaders)
                if (section.Name == "pck")
                    for (var offset = 0; offset < 8; offset++)
                        if (HasGodotHeader(stream, (long)section.PointerToRawData + offset)) return true;
            return false;
        }
        catch (Exception error) when (IsReadError(error) || error is BadImageFormatException) { return false; }
    }

    private static bool HasGodotHeader(Stream stream, long offset)
    {
        if (offset < 0 || offset > stream.Length - 4) return false;
        stream.Position = offset;
        Span<byte> header = stackalloc byte[4];
        stream.ReadExactly(header);
        return header.SequenceEqual("GDPC"u8);
    }

    private static bool IsListedByLocalManifest(string directory, string path)
    {
        try
        {
            var config = Path.Combine(directory, "MicrosoftGame.config");
            if (File.Exists(config) && XDocument.Load(config).Descendants("Executable").Any(entry =>
                MatchesLaunchPath(directory, (string?)entry.Attribute("Name"), path))) return true;
        }
        catch (Exception error) when (IsReadError(error) || error is System.Xml.XmlException) { }

        try
        {
            return Directory.EnumerateFiles(directory, "goggame-*.info").Any(manifest =>
            {
                try
                {
                    using var data = JsonDocument.Parse(File.ReadAllText(manifest));
                    var item = data.RootElement;
                    return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("playTasks", out var tasks)
                        && tasks.ValueKind == JsonValueKind.Array && tasks.EnumerateArray().Any(task =>
                            task.ValueKind == JsonValueKind.Object && ReadString(task, "type").Equals("FileTask", StringComparison.OrdinalIgnoreCase)
                            && task.TryGetProperty("isPrimary", out var primary) && primary.ValueKind == JsonValueKind.True
                            && MatchesLaunchPath(directory, ReadString(task, "path"), path));
                }
                catch (Exception error) when (IsReadError(error)) { return false; }
            });
        }
        catch (Exception error) when (IsReadError(error)) { return false; }
    }

    private static bool IsSteamGame(string path)
    {
        var directory = new FileInfo(path).Directory;
        while (directory?.Parent is { } common)
        {
            if (common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) &&
                common.Parent is { } steamApps && steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return steamApps.EnumerateFiles("appmanifest_*.acf").Any(manifest =>
                    {
                        try
                        {
                            var install = Regex.Match(File.ReadAllText(manifest.FullName),
                                "^\\s*\"installdir\"[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                            return install.Success && directory.Name.Equals(install.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception error) when (IsReadError(error)) { return false; }
                    });
                }
                catch (Exception error) when (IsReadError(error)) { return false; }
            }
            directory = common;
        }
        return false;
    }

    private bool IsEpicGame(string path)
    {
        if (!Directory.Exists(epicManifests)) return false;
        try
        {
            return Directory.EnumerateFiles(epicManifests, "*.item").Any(manifest =>
            {
                try
                {
                    using var data = JsonDocument.Parse(File.ReadAllText(manifest));
                    var item = data.RootElement;
                    if (item.ValueKind != JsonValueKind.Object) return false;
                    var folder = ReadString(item, "InstallLocation");
                    var launch = ReadString(item, "LaunchExecutable");
                    return Path.IsPathFullyQualified(folder) && MatchesLaunchPath(folder, launch, path);
                }
                catch (Exception error) when (IsReadError(error)) { return false; }
            });
        }
        catch (Exception error) when (IsReadError(error)) { return false; }
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool MatchesLaunchPath(string folder, string? launch, string path) =>
        !string.IsNullOrWhiteSpace(launch) && Path.GetFullPath(Path.Combine(folder, launch)).Equals(path, StringComparison.OrdinalIgnoreCase);

    private static string? ExecutablePath(string? path)
    {
        try { return path is not null && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception error) when (IsReadError(error)) { return null; }
    }

    private static string? RuleIdentity(string? identity)
    {
        if (identity == RobloxPlayerIdentity || identity?.StartsWith(PackageIdentityPrefix, StringComparison.Ordinal) == true)
            return identity;
        var executable = ExecutablePath(identity);
        return IsRobloxPlayerPath(executable) ? RobloxPlayerIdentity : executable;
    }

    private static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private sealed record Settings(Dictionary<string, bool>? Apps, bool ShowGraph, bool LargeText);
}

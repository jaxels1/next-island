// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace SapphireGameHud;

internal sealed class RobloxServerLatency
{
    private static readonly Regex NetworkEvent = new(
        @"^\d{4}-\d{2}-\d{2}T[^,]+,[\d.]+,[\da-fA-F]+,\d+,Info \[DFLog::NetworkClient\] (?:Connection accepted from (?<ip>(?:\d{1,3}\.){3}\d{1,3})\|\d+|Client:Disconnect)\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private string? logPath;
    private long lastLength;
    private IPAddress? endpoint;

    // Called from one background task at a time, with a fresh reader for each game process.
    internal double? Read(int processId)
    {
        try
        {
            if (logPath is null)
            {
                using var process = Process.GetProcessById(processId);
                var started = process.StartTime.ToUniversalTime();
                var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
                logPath = Directory.EnumerateFiles(logs, "*_Player_*_last.log").Where(path =>
                {
                    var fields = Path.GetFileName(path).Split('_');
                    return fields.Length == 5 && DateTime.TryParseExact(fields[1], "yyyyMMdd'T'HHmmss'Z'",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
                        && time >= started.AddSeconds(-2) && time <= started.AddSeconds(45);
                }).Take(2).SingleOrDefault();
                if (logPath is null) return null;
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - 256 * 1024);
            if (lastLength < start || length < lastLength) endpoint = null;
            stream.Position = start;
            using var reader = new StreamReader(stream);
            endpoint = ReadEndpoint(reader.ReadToEnd(), endpoint);
            lastLength = length;
            if (endpoint is null) return null;
            using var ping = new Ping();
            var reply = ping.Send(endpoint, 900);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or PingException)
        {
            return null;
        }
    }

    internal static IPAddress? ReadEndpoint(string log, IPAddress? previous)
    {
        var matches = NetworkEvent.Matches(log);
        if (matches.Count == 0) return previous;
        if (!IPAddress.TryParse(matches[^1].Groups["ip"].Value, out var address)) return null;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 or >= 224
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            ? null : address;
    }
}

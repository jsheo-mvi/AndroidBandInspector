// AndroidBandInspector
// Copyright (C) 2026 jsheo-mvi
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, version 3 only.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

var startedAt = DateTimeOffset.Now;
Directory.CreateDirectory(options.OutputDirectory);

try
{
    var adb = new AdbClient(options.AdbPath, options.CommandTimeout);
    var devices = await adb.ListDevicesAsync();
    if (!string.IsNullOrWhiteSpace(options.Serial))
    {
        devices = devices
            .Where(device => string.Equals(device.Serial, options.Serial, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    if (devices.Count == 0)
    {
        Console.Error.WriteLine("No authorized USB Android device was found.");
        Console.Error.WriteLine("Check USB debugging, RSA authorization, and `adb devices -l`.");
        return 2;
    }

    var reports = new List<DeviceBandReport>();
    foreach (var device in devices)
    {
        Console.WriteLine($"Inspecting {device.Serial} ({device.State})...");
        var collector = new DeviceCollector(adb, options);
        reports.Add(await collector.CollectAsync(device, startedAt));
    }

    foreach (var report in reports)
    {
        var baseName = SanitizeFileName($"{report.Device.Serial}_{startedAt:yyyyMMdd_HHmmss}");
        var jsonPath = Path.Combine(options.OutputDirectory, $"{baseName}.json");
        var mdPath = Path.Combine(options.OutputDirectory, $"{baseName}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonSettings.Options));
        await File.WriteAllTextAsync(mdPath, MarkdownReport.Render(report));

        Console.WriteLine($"Wrote {jsonPath}");
        Console.WriteLine($"Wrote {mdPath}");
    }

    return 0;
}
catch (AdbException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 3;
}

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars().ToHashSet();
    var builder = new StringBuilder(value.Length);
    foreach (var ch in value)
    {
        builder.Append(invalid.Contains(ch) ? '_' : ch);
    }

    return builder.ToString();
}

internal sealed record CliOptions(
    string AdbPath,
    string? Serial,
    string OutputDirectory,
    bool IncludeRawDumps,
    TimeSpan CommandTimeout,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var adbPath = "adb";
        string? serial = null;
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "band-reports");
        var includeRawDumps = false;
        var commandTimeout = TimeSpan.FromSeconds(20);
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--adb":
                    adbPath = ReadValue(args, ref i, arg);
                    break;
                case "--serial":
                case "-s":
                    serial = ReadValue(args, ref i, arg);
                    break;
                case "--out":
                    outputDirectory = ReadValue(args, ref i, arg);
                    break;
                case "--raw":
                    includeRawDumps = true;
                    break;
                case "--timeout-seconds":
                    var seconds = int.Parse(ReadValue(args, ref i, arg), CultureInfo.InvariantCulture);
                    commandTimeout = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    throw new AdbException($"Unknown argument: {arg}{Environment.NewLine}{HelpText}");
            }
        }

        return new CliOptions(adbPath, serial, outputDirectory, includeRawDumps, commandTimeout, showHelp);
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new AdbException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }

    public const string HelpText = """
AndroidBandInspector

Read-only ADB collector for Android radio band evidence.

Usage:
  AndroidBandInspector.exe [--adb <path>] [--serial <adb-serial>] [--out <directory>] [--raw] [--timeout-seconds <n>]

Examples:
  AndroidBandInspector.exe --out .\reports
  AndroidBandInspector.exe --serial R5CT123456A --raw

Notes:
  - This tool does not root the device and does not change radio settings.
  - Full supported modem band capability is often not exposed by stock Android over ADB.
  - Reports separate observed bands from framework-advertised and unavailable evidence.
""";
}

internal sealed class AdbClient(string adbPath, TimeSpan timeout)
{
    public async Task<List<AdbDevice>> ListDevicesAsync()
    {
        var result = await RunAsync("devices -l", serial: null);
        if (result.ExitCode != 0)
        {
            throw new AdbException($"adb devices failed: {result.Error.Trim()}");
        }

        var devices = new List<AdbDevice>();
        foreach (var line in result.Output.SplitLines().Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 2)
            {
                continue;
            }

            var serial = parts[0];
            var state = parts[1];
            var details = parts.Skip(2)
                .Select(part => part.Split(':', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

            if (string.Equals(state, "device", StringComparison.OrdinalIgnoreCase))
            {
                devices.Add(new AdbDevice(serial, state, details));
            }
        }

        return devices;
    }

    public Task<CommandResult> ShellAsync(string serial, string command)
    {
        return RunAsync($"shell {QuoteAdbArgument(command)}", serial);
    }

    private async Task<CommandResult> RunAsync(string arguments, string? serial)
    {
        var fullArguments = serial is null
            ? arguments
            : $"-s {QuoteAdbArgument(serial)} {arguments}";

        var startInfo = new ProcessStartInfo
        {
            FileName = adbPath,
            Arguments = fullArguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new AdbException("Failed to start adb process.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var waitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
            if (completed != waitTask)
            {
                TryKill(process);
                return new CommandResult(fullArguments, -1, await outputTask, "Command timed out.");
            }

            return new CommandResult(fullArguments, process.ExitCode, await outputTask, await errorTask);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AdbException($"Cannot start adb. Install Android Platform Tools or pass --adb <path>. Details: {ex.Message}");
        }
    }

    private static string QuoteAdbArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.All(ch => !char.IsWhiteSpace(ch) && ch != '"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }
}

internal sealed class DeviceCollector(AdbClient adb, CliOptions options)
{
    private static readonly string[] Commands =
    [
        "getprop",
        "dumpsys telephony.registry",
        "dumpsys phone",
        "dumpsys telephony",
        "cmd phone list-modems",
        "cmd phone get-sim-slots",
        "cmd phone get-allowed-network-types-for-users",
        "cmd phone ims get-ims-service -d",
        "cmd phone ims get-ims-service -c",
        "cmd phone cc get-value carrier_volte_available_bool",
        "cmd phone cc get-value carrier_volte_provisioning_required_bool",
        "dumpsys ims",
        "dumpsys activity services org.codeaurora.ims",
        "settings get global preferred_network_mode",
        "settings get global preferred_network_mode1",
        "settings get global preferred_network_mode2"
    ];

    public async Task<DeviceBandReport> CollectAsync(AdbDevice device, DateTimeOffset startedAt)
    {
        var commandResults = new List<CommandCapture>();
        var rawCommandResults = new List<CommandCapture>();
        foreach (var command in Commands)
        {
            var result = await adb.ShellAsync(device.Serial, command);
            rawCommandResults.Add(CommandCapture.From(command, result, includeRawDumps: true));
            commandResults.Add(CommandCapture.From(command, result, options.IncludeRawDumps));
        }

        var props = PropertyParser.Parse(rawCommandResults.FirstOrDefault(c => c.Command == "getprop")?.Output ?? string.Empty);
        var profile = DeviceProfile.From(device, props);
        var observations = BandObservationParser.Parse(rawCommandResults);
        var evidence = BuildEvidence(profile, observations, rawCommandResults);

        return new DeviceBandReport(
            startedAt,
            profile,
            evidence,
            commandResults);
    }

    private static BandEvidence BuildEvidence(
        DeviceProfile profile,
        IReadOnlyList<BandObservation> observations,
        IReadOnlyList<CommandCapture> commandCaptures)
    {
        var grouped = observations
            .GroupBy(item => new
            {
                item.RadioAccessTechnology,
                item.Band,
                item.Channel,
                item.IsRegistered,
                item.Source
            })
            .Select(group => group.First())
            .OrderByDescending(item => item.IsRegistered)
            .ThenBy(item => item.RadioAccessTechnology)
            .ThenBy(item => item.Band ?? int.MaxValue)
            .ThenBy(item => item.Channel ?? int.MaxValue)
            .ToList();

        var currentBands = grouped
            .Where(item => item.Kind == EvidenceKind.Observed || item.Kind == EvidenceKind.DerivedFromChannel)
            .OrderByDescending(item => item.IsRegistered)
            .ToList();

        var koreaMatches = KoreaMobileAllocations.Match(currentBands);
        var primary = PrimaryBandAnalyzer.Assess(profile, currentBands, koreaMatches);
        var capability = CapabilityAnalyzer.Assess(profile, commandCaptures);
        var volte = VolteAnalyzer.Assess(profile, commandCaptures);

        return new BandEvidence(
            "Stock Android/ADB cannot reliably expose the full supported modem band matrix on many devices. This report maps observed serving-cell evidence to Korea mobile allocations from the supplied frequency-status PDF.",
            primary,
            capability,
            volte,
            currentBands,
            grouped,
            koreaMatches);
    }
}

internal static partial class BandObservationParser
{
    public static IReadOnlyList<BandObservation> Parse(IReadOnlyList<CommandCapture> captures)
    {
        var observations = new List<BandObservation>();
        foreach (var capture in captures.Where(c => c.ExitCode == 0))
        {
            foreach (var line in capture.Output.SplitLines())
            {
                ParseLine(capture.Command, line, observations);
            }
        }

        return observations;
    }

    private static void ParseLine(string source, string line, List<BandObservation> observations)
    {
        var rat = DetectRat(line);
        if (rat is null)
        {
            return;
        }

        var radioAccessTechnology = rat.Value;
        var explicitBands = ExtractBandList(line);
        var channel = ExtractChannel(radioAccessTechnology, line);
        var isRegistered = IsRegisteredCell(line);

        foreach (var band in explicitBands)
        {
            observations.Add(new BandObservation(
                radioAccessTechnology,
                band,
                channel,
                isRegistered,
                EvidenceKind.Observed,
                source,
                TrimEvidence(line)));
        }

        if (channel.HasValue && explicitBands.Count == 0)
        {
            var derived = BandPlan.TryDerive(radioAccessTechnology, channel.Value);
            observations.Add(new BandObservation(
                radioAccessTechnology,
                derived?.Band,
                channel,
                isRegistered,
                derived is null ? EvidenceKind.ObservedChannelOnly : EvidenceKind.DerivedFromChannel,
                source,
                derived is null ? TrimEvidence(line) : $"{derived.Name}; {TrimEvidence(line)}"));
        }
    }

    private static bool IsRegisteredCell(string line)
    {
        return RegisteredYesRegex().IsMatch(line) || RegisteredTrueRegex().IsMatch(line);
    }

    private static RadioAccessTechnology? DetectRat(string line)
    {
        if (line.Contains("CellIdentityNr", StringComparison.OrdinalIgnoreCase)
            || line.Contains("CellInfoNr", StringComparison.OrdinalIgnoreCase)
            || line.Contains("nrarfcn", StringComparison.OrdinalIgnoreCase))
        {
            return RadioAccessTechnology.Nr5g;
        }

        if (line.Contains("CellIdentityLte", StringComparison.OrdinalIgnoreCase)
            || line.Contains("CellInfoLte", StringComparison.OrdinalIgnoreCase)
            || line.Contains("earfcn", StringComparison.OrdinalIgnoreCase))
        {
            return RadioAccessTechnology.Lte;
        }

        if (line.Contains("CellIdentityWcdma", StringComparison.OrdinalIgnoreCase)
            || line.Contains("CellInfoWcdma", StringComparison.OrdinalIgnoreCase)
            || line.Contains("uarfcn", StringComparison.OrdinalIgnoreCase))
        {
            return RadioAccessTechnology.Wcdma;
        }

        if (line.Contains("CellIdentityGsm", StringComparison.OrdinalIgnoreCase)
            || line.Contains("CellInfoGsm", StringComparison.OrdinalIgnoreCase)
            || line.Contains("arfcn", StringComparison.OrdinalIgnoreCase))
        {
            return RadioAccessTechnology.Gsm;
        }

        return null;
    }

    private static List<int> ExtractBandList(string line)
    {
        var bands = new List<int>();
        foreach (Match match in BandListRegex().Matches(line))
        {
            foreach (var part in match.Groups["bands"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band))
                {
                    bands.Add(band);
                }
            }
        }

        foreach (Match match in SingleBandRegex().Matches(line))
        {
            if (int.TryParse(match.Groups["band"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band))
            {
                bands.Add(band);
            }
        }

        return bands.Distinct().ToList();
    }

    private static int? ExtractChannel(RadioAccessTechnology rat, string line)
    {
        var regex = rat switch
        {
            RadioAccessTechnology.Nr5g => NrArfcnRegex(),
            RadioAccessTechnology.Lte => EarfcnRegex(),
            RadioAccessTechnology.Wcdma => UarfcnRegex(),
            RadioAccessTechnology.Gsm => ArfcnRegex(),
            _ => throw new ArgumentOutOfRangeException(nameof(rat), rat, null)
        };

        var match = regex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["channel"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
            ? channel
            : null;
    }

    private static string TrimEvidence(string line)
    {
        const int maxLength = 500;
        var trimmed = line.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    [GeneratedRegex(@"m?Bands=\[(?<bands>[0-9,\s]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex BandListRegex();

    [GeneratedRegex(@"(?<![A-Za-z])band\s*[=:]\s*(?<band>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SingleBandRegex();

    [GeneratedRegex(@"m?Nrarfcn\s*[=:]\s*(?<channel>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NrArfcnRegex();

    [GeneratedRegex(@"m?Earfcn\s*[=:]\s*(?<channel>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EarfcnRegex();

    [GeneratedRegex(@"m?Uarfcn\s*[=:]\s*(?<channel>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UarfcnRegex();

    [GeneratedRegex(@"m?Arfcn\s*[=:]\s*(?<channel>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ArfcnRegex();

    [GeneratedRegex(@"mRegistered\s*[=:]\s*YES", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredYesRegex();

    [GeneratedRegex(@"(?<![A-Za-z])registered\s*[=:]\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredTrueRegex();
}

internal static class BandPlan
{
    private static readonly IReadOnlyList<ChannelBandRange> LteRanges =
    [
        new(1, 0, 599, "LTE Band 1"),
        new(2, 600, 1199, "LTE Band 2"),
        new(3, 1200, 1949, "LTE Band 3"),
        new(4, 1950, 2399, "LTE Band 4"),
        new(5, 2400, 2649, "LTE Band 5"),
        new(7, 2750, 3449, "LTE Band 7"),
        new(8, 3450, 3799, "LTE Band 8"),
        new(12, 5010, 5179, "LTE Band 12"),
        new(13, 5180, 5279, "LTE Band 13"),
        new(14, 5280, 5379, "LTE Band 14"),
        new(17, 5730, 5849, "LTE Band 17"),
        new(18, 5850, 5999, "LTE Band 18"),
        new(19, 6000, 6149, "LTE Band 19"),
        new(20, 6150, 6449, "LTE Band 20"),
        new(25, 8040, 8689, "LTE Band 25"),
        new(26, 8690, 9039, "LTE Band 26"),
        new(28, 9210, 9659, "LTE Band 28"),
        new(29, 9660, 9769, "LTE Band 29"),
        new(30, 9770, 9869, "LTE Band 30"),
        new(32, 9920, 10359, "LTE Band 32"),
        new(34, 36200, 36349, "LTE Band 34"),
        new(38, 37750, 38249, "LTE Band 38"),
        new(39, 38250, 38649, "LTE Band 39"),
        new(40, 38650, 39649, "LTE Band 40"),
        new(41, 39650, 41589, "LTE Band 41"),
        new(42, 41590, 43589, "LTE Band 42"),
        new(43, 43590, 45589, "LTE Band 43"),
        new(46, 46790, 54539, "LTE Band 46"),
        new(48, 55240, 56739, "LTE Band 48"),
        new(66, 66436, 67335, "LTE Band 66"),
        new(71, 68586, 68935, "LTE Band 71")
    ];

    private static readonly IReadOnlyList<ChannelBandRange> NrRanges =
    [
        new(1, 422000, 434000, "NR Band n1"),
        new(2, 386000, 398000, "NR Band n2"),
        new(3, 361000, 376000, "NR Band n3"),
        new(5, 173800, 178800, "NR Band n5"),
        new(7, 524000, 538000, "NR Band n7"),
        new(8, 185000, 192000, "NR Band n8"),
        new(12, 145800, 149200, "NR Band n12"),
        new(20, 158200, 164200, "NR Band n20"),
        new(25, 386000, 399000, "NR Band n25"),
        new(28, 151600, 160600, "NR Band n28"),
        new(38, 514000, 524000, "NR Band n38"),
        new(40, 460000, 480000, "NR Band n40"),
        new(41, 499200, 537999, "NR Band n41"),
        new(66, 422000, 440000, "NR Band n66"),
        new(71, 123400, 130400, "NR Band n71"),
        new(77, 620000, 680000, "NR Band n77"),
        new(78, 620000, 653333, "NR Band n78"),
        new(79, 693334, 733333, "NR Band n79"),
        new(257, 2054166, 2104165, "NR Band n257"),
        new(258, 2016667, 2070832, "NR Band n258"),
        new(260, 2229166, 2279165, "NR Band n260"),
        new(261, 2070833, 2084999, "NR Band n261")
    ];

    private static readonly IReadOnlyList<ChannelBandRange> WcdmaRanges =
    [
        new(1, 10562, 10838, "WCDMA Band 1 downlink"),
        new(1, 9612, 9888, "WCDMA Band 1 uplink"),
        new(5, 4357, 4458, "WCDMA Band 5 downlink"),
        new(5, 4132, 4233, "WCDMA Band 5 uplink"),
        new(8, 2937, 3088, "WCDMA Band 8 downlink"),
        new(8, 2712, 2863, "WCDMA Band 8 uplink")
    ];

    public static DerivedBand? TryDerive(RadioAccessTechnology rat, int channel)
    {
        var ranges = rat switch
        {
            RadioAccessTechnology.Lte => LteRanges,
            RadioAccessTechnology.Nr5g => NrRanges,
            RadioAccessTechnology.Wcdma => WcdmaRanges,
            _ => []
        };

        var match = ranges.FirstOrDefault(range => channel >= range.StartChannel && channel <= range.EndChannel);
        return match is null ? null : new DerivedBand(match.Band, match.Name);
    }
}

internal static partial class PropertyParser
{
    private static readonly string[] AllowedPrefixes =
    [
        "ro.product.",
        "ro.vendor.product.",
        "ro.odm.product.",
        "ro.system.product.",
        "ro.boot.hardware",
        "ro.board.platform",
        "ro.hardware",
        "gsm.operator.",
        "gsm.sim.operator.",
        "gsm.version.baseband",
        "ril.",
        "vendor.ril.",
        "persist.dbg.",
        "persist.vendor.ims",
        "persist.vendor.radio",
        "persist.radio.",
        "persist.ims.",
        "persist.volte.",
        "ro.telephony."
    ];

    private static readonly string[] RedactedKeyMarkers =
    [
        "imei",
        "imsi",
        "iccid",
        "msisdn",
        "meid",
        "esn",
        "serialno",
        "subscriber"
    ];

    public static IReadOnlyDictionary<string, string> Parse(string getPropOutput)
    {
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in getPropOutput.SplitLines())
        {
            var match = GetPropRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!AllowedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            values[key] = ShouldRedact(key) ? "<redacted>" : match.Groups["value"].Value;
        }

        return values;
    }

    private static bool ShouldRedact(string key)
    {
        return RedactedKeyMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^\[(?<key>[^]]+)\]:\s+\[(?<value>.*)\]$")]
    private static partial Regex GetPropRegex();
}

internal static class MarkdownReport
{
    public static string Render(DeviceBandReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Android Band Inspection Report");
        builder.AppendLine();
        builder.AppendLine($"- CollectedAt: `{report.CollectedAt:O}`");
        builder.AppendLine($"- Serial: `{report.Device.Serial}`");
        builder.AppendLine($"- Model: `{report.Device.Model ?? "unknown"}`");
        builder.AppendLine($"- Product: `{report.Device.Product ?? "unknown"}`");
        builder.AppendLine($"- Device: `{report.Device.Device ?? "unknown"}`");
        builder.AppendLine($"- Baseband: `{report.Device.Baseband ?? "unknown"}`");
        builder.AppendLine($"- Operator: `{report.Device.OperatorName ?? "unknown"}`");
        builder.AppendLine($"- OperatorNumeric: `{report.Device.OperatorNumeric ?? "unknown"}`");
        builder.AppendLine();
        builder.AppendLine("## Interpretation");
        builder.AppendLine();
        builder.AppendLine(report.Evidence.Limitation);
        builder.AppendLine();
        builder.AppendLine("## Primary Band Assessment");
        builder.AppendLine();
        builder.AppendLine($"- MainRole: `{report.Evidence.PrimaryAssessment.MainRole}`");
        builder.AppendLine($"- MainRAT: `{report.Evidence.PrimaryAssessment.MainRat?.ToString() ?? "unknown"}`");
        builder.AppendLine($"- MainBand: `{FormatBand(report.Evidence.PrimaryAssessment.MainRat, report.Evidence.PrimaryAssessment.MainBand)}`");
        builder.AppendLine($"- Channel: `{Format(report.Evidence.PrimaryAssessment.MainChannel)}`");
        builder.AppendLine($"- OperatorHint: `{report.Evidence.PrimaryAssessment.OperatorHint ?? "unknown"}`");
        builder.AppendLine($"- Note: {Escape(report.Evidence.PrimaryAssessment.Note)}");
        builder.AppendLine();
        builder.AppendLine("## No-SIM Capability Assessment");
        builder.AppendLine();
        AppendCapability(builder, report.Evidence.CapabilityAssessment);
        builder.AppendLine();
        builder.AppendLine("## VoLTE / IMS Assessment");
        builder.AppendLine();
        AppendVolte(builder, report.Evidence.VolteAssessment);
        builder.AppendLine();
        builder.AppendLine("## Korea Frequency Allocation Matches");
        builder.AppendLine();
        AppendKoreaMatchTable(builder, report.Evidence.KoreaMatches);
        builder.AppendLine();
        builder.AppendLine("## Current Observed Bands");
        builder.AppendLine();
        AppendObservationTable(builder, report.Evidence.CurrentObservedBands);
        builder.AppendLine();
        builder.AppendLine("## All Parsed Radio Evidence");
        builder.AppendLine();
        AppendObservationTable(builder, report.Evidence.AllObservations);
        builder.AppendLine();
        builder.AppendLine("## Relevant Properties");
        builder.AppendLine();
        foreach (var property in report.Device.Properties)
        {
            builder.AppendLine($"- `{property.Key}` = `{property.Value}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Command Status");
        builder.AppendLine();
        builder.AppendLine("| Command | ExitCode | OutputBytes | Error |");
        builder.AppendLine("|---|---:|---:|---|");
        foreach (var command in report.CommandCaptures)
        {
            builder.AppendLine($"| `{Escape(command.Command)}` | {command.ExitCode} | {command.Output.Length} | `{Escape(command.Error.Trim())}` |");
        }

        return builder.ToString();
    }

    private static void AppendObservationTable(StringBuilder builder, IReadOnlyList<BandObservation> observations)
    {
        if (observations.Count == 0)
        {
            builder.AppendLine("No radio band evidence was parsed from ADB output.");
            return;
        }

        builder.AppendLine("| RAT | Band | Channel | Registered | EvidenceKind | Source | Evidence |");
        builder.AppendLine("|---|---:|---:|---|---|---|---|");
        foreach (var item in observations)
        {
            builder.AppendLine($"| {item.RadioAccessTechnology} | {FormatBand(item.RadioAccessTechnology, item.Band)} | {Format(item.Channel)} | {item.IsRegistered} | {item.Kind} | `{Escape(item.Source)}` | {Escape(item.Evidence)} |");
        }
    }

    private static void AppendCapability(StringBuilder builder, DeviceCapabilityAssessment assessment)
    {
        builder.AppendLine($"- HardwarePlatform: `{assessment.HardwarePlatform ?? "unknown"}`");
        builder.AppendLine($"- ChipsetProfile: `{assessment.ChipsetProfile ?? "unknown"}`");
        builder.AppendLine($"- ModemProfile: `{assessment.ModemProfile ?? "unknown"}`");
        builder.AppendLine($"- OsAllowedRats: `{string.Join(", ", assessment.OsAllowedRats)}`");
        builder.AppendLine($"- PreferredNetworkModes: `{string.Join(", ", assessment.PreferredNetworkModes.Select(mode => $"{mode.RawValue}:{mode.Name}"))}`");
        builder.AppendLine($"- Confidence: `{assessment.Confidence}`");
        builder.AppendLine($"- Note: {Escape(assessment.Note)}");
        builder.AppendLine();

        if (assessment.CandidateBands.Count == 0)
        {
            builder.AppendLine("No candidate Korea bands could be inferred from OS/chipset evidence.");
            return;
        }

        builder.AppendLine("| Candidate | Operator | Service | Allocation | Evidence | Confidence |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var candidate in assessment.CandidateBands)
        {
            builder.AppendLine($"| {FormatBand(candidate.RadioAccessTechnology, candidate.Band)} | {candidate.Operator} | {candidate.Service} | {Escape(candidate.Description)} | {Escape(candidate.Evidence)} | {candidate.Confidence} |");
        }
    }

    private static void AppendVolte(StringBuilder builder, VolteAssessment assessment)
    {
        builder.AppendLine($"- Status: `{assessment.Status}`");
        builder.AppendLine($"- DeviceImsService: `{assessment.DeviceImsService ?? "unknown"}`");
        builder.AppendLine($"- ImsServiceBound: `{assessment.ImsServiceBound}`");
        builder.AppendLine($"- ImsDumpsysAvailable: `{assessment.ImsDumpsysAvailable}`");
        builder.AppendLine($"- CarrierVolteAvailable: `{assessment.CarrierVolteAvailable?.ToString() ?? "unknown"}`");
        builder.AppendLine($"- CarrierProvisioningRequired: `{assessment.CarrierProvisioningRequired?.ToString() ?? "unknown"}`");
        builder.AppendLine($"- CurrentImsRegistered: `{assessment.CurrentImsRegistered?.ToString() ?? "unknown"}`");
        builder.AppendLine($"- Confidence: `{assessment.Confidence}`");
        builder.AppendLine($"- Note: {Escape(assessment.Note)}");
        builder.AppendLine();

        if (assessment.EvidenceLines.Count == 0)
        {
            builder.AppendLine("No IMS/VoLTE evidence was found.");
            return;
        }

        builder.AppendLine("| Evidence |");
        builder.AppendLine("|---|");
        foreach (var evidence in assessment.EvidenceLines)
        {
            builder.AppendLine($"| {Escape(evidence)} |");
        }
    }

    private static void AppendKoreaMatchTable(StringBuilder builder, IReadOnlyList<KoreaAllocationMatch> matches)
    {
        if (matches.Count == 0)
        {
            builder.AppendLine("No observed band matched the embedded Korea allocation table.");
            return;
        }

        builder.AppendLine("| Observed | Operator | Service | Allocation | Role | Source |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var match in matches)
        {
            builder.AppendLine($"| {FormatBand(match.Observation.RadioAccessTechnology, match.Observation.Band)} | {match.Allocation.Operator} | {match.Allocation.Service} | {Escape(match.Allocation.Description)} | {Escape(match.Allocation.Role)} | {Escape(match.Allocation.Source)} |");
        }
    }

    private static string Format(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static string FormatBand(RadioAccessTechnology? rat, int? band)
    {
        if (!band.HasValue)
        {
            return "";
        }

        return rat == RadioAccessTechnology.Nr5g ? $"n{band.Value}" : $"B{band.Value}";
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .ReplaceLineEndings(" ")
            .Trim();
    }
}

internal sealed record AdbDevice(
    string Serial,
    string State,
    IReadOnlyDictionary<string, string> Details);

internal sealed record CommandResult(
    string Arguments,
    int ExitCode,
    string Output,
    string Error);

internal sealed record CommandCapture(
    string Command,
    int ExitCode,
    string Output,
    string Error)
{
    public static CommandCapture From(string command, CommandResult result, bool includeRawDumps)
    {
        return new CommandCapture(
            command,
            result.ExitCode,
            includeRawDumps ? result.Output : Truncate(result.Output, 4000),
            Truncate(result.Error, 2000));
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + Environment.NewLine + "<truncated>";
    }
}

internal sealed record DeviceProfile(
    string Serial,
    string? Model,
    string? Product,
    string? Device,
    string? Baseband,
    string? OperatorName,
    string? OperatorNumeric,
    IReadOnlyDictionary<string, string> Properties)
{
    public static DeviceProfile From(AdbDevice device, IReadOnlyDictionary<string, string> props)
    {
        var operatorNumeric = ReadFirst(props, "gsm.operator.numeric", "gsm.sim.operator.numeric");
        return new DeviceProfile(
            device.Serial,
            ReadFirst(props, "ro.product.model", "ro.product.vendor.model"),
            ReadFirst(props, "ro.product.name", "ro.product.vendor.name"),
            ReadFirst(props, "ro.product.device", "ro.product.vendor.device"),
            ReadFirst(props, "gsm.version.baseband"),
            ReadFirst(props, "gsm.operator.alpha", "gsm.sim.operator.alpha") ?? KoreanOperatorMap.FromMccMnc(operatorNumeric),
            operatorNumeric,
            props);
    }

    private static string? ReadFirst(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && IsUsableValue(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsUsableValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Trim(',', ' ', '\t') is { Length: > 0 };
    }
}

internal sealed record DeviceBandReport(
    DateTimeOffset CollectedAt,
    DeviceProfile Device,
    BandEvidence Evidence,
    IReadOnlyList<CommandCapture> CommandCaptures);

internal sealed record BandEvidence(
    string Limitation,
    PrimaryBandAssessment PrimaryAssessment,
    DeviceCapabilityAssessment CapabilityAssessment,
    VolteAssessment VolteAssessment,
    IReadOnlyList<BandObservation> CurrentObservedBands,
    IReadOnlyList<BandObservation> AllObservations,
    IReadOnlyList<KoreaAllocationMatch> KoreaMatches);

internal sealed record BandObservation(
    RadioAccessTechnology RadioAccessTechnology,
    int? Band,
    int? Channel,
    bool IsRegistered,
    EvidenceKind Kind,
    string Source,
    string Evidence);

internal sealed record PrimaryBandAssessment(
    string MainRole,
    RadioAccessTechnology? MainRat,
    int? MainBand,
    int? MainChannel,
    string? OperatorHint,
    string Note);

internal sealed record KoreaMobileAllocation(
    RadioAccessTechnology RadioAccessTechnology,
    int Band,
    string Operator,
    string Service,
    string Description,
    string Role,
    string Source);

internal sealed record KoreaAllocationMatch(
    BandObservation Observation,
    KoreaMobileAllocation Allocation);

internal sealed record DeviceCapabilityAssessment(
    string? HardwarePlatform,
    string? ChipsetProfile,
    string? ModemProfile,
    IReadOnlyList<string> OsAllowedRats,
    IReadOnlyList<PreferredNetworkMode> PreferredNetworkModes,
    IReadOnlyList<SupportedBandCandidate> CandidateBands,
    string Confidence,
    string Note);

internal sealed record PreferredNetworkMode(
    int RawValue,
    string Name,
    IReadOnlyList<string> Rats);

internal sealed record SupportedBandCandidate(
    RadioAccessTechnology RadioAccessTechnology,
    int Band,
    string Operator,
    string Service,
    string Description,
    string Evidence,
    string Confidence);

internal sealed record VolteAssessment(
    string Status,
    string? DeviceImsService,
    bool ImsServiceBound,
    bool ImsDumpsysAvailable,
    bool? CarrierVolteAvailable,
    bool? CarrierProvisioningRequired,
    bool? CurrentImsRegistered,
    string Confidence,
    string Note,
    IReadOnlyList<string> EvidenceLines);

internal sealed record ChannelBandRange(
    int Band,
    int StartChannel,
    int EndChannel,
    string Name);

internal sealed record DerivedBand(
    int Band,
    string Name);

internal static class KoreaMobileAllocations
{
    private const string PdfSource = "이동통신용 주파수 주요 이용 현황 (2025.02), supplied PDF";

    public static readonly IReadOnlyList<KoreaMobileAllocation> All =
    [
        new(RadioAccessTechnology.Wcdma, 1, "SKT", "3G WCDMA", "2.1GHz, 10MHz allocation", "Legacy 3G", PdfSource),
        new(RadioAccessTechnology.Wcdma, 1, "KT", "3G WCDMA", "2.1GHz, 10MHz allocation", "Legacy 3G", PdfSource),

        new(RadioAccessTechnology.Lte, 5, "SKT", "LTE", "800MHz band, 20MHz nationwide allocation", "LTE coverage anchor", PdfSource),
        new(RadioAccessTechnology.Lte, 5, "LGU+", "LTE", "800MHz band, 20MHz nationwide allocation", "LTE coverage anchor", PdfSource),
        new(RadioAccessTechnology.Lte, 8, "KT", "LTE", "900MHz band, 20MHz allocation", "LTE low-band coverage", PdfSource),
        new(RadioAccessTechnology.Lte, 3, "SKT", "LTE", "1.8GHz band, 35MHz allocation", "LTE capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 3, "KT", "LTE", "1.8GHz band, 55MHz total allocation", "LTE primary/capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 1, "SKT", "LTE", "2.1GHz band, 30MHz allocation", "LTE capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 1, "KT", "LTE", "2.1GHz band, 30MHz allocation", "LTE capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 1, "LGU+", "LTE", "2.1GHz band, 40MHz allocation", "LTE capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 7, "SKT", "LTE", "2.6GHz band, 60MHz allocation", "LTE high-capacity", PdfSource),
        new(RadioAccessTechnology.Lte, 7, "LGU+", "LTE", "2.6GHz band, 40MHz allocation", "LTE high-capacity", PdfSource),

        new(RadioAccessTechnology.Nr5g, 78, "LGU+", "5G NR", "3.42-3.50GHz, 100MHz allocation", "5G main C-band", PdfSource),
        new(RadioAccessTechnology.Nr5g, 78, "KT", "5G NR", "3.50-3.60GHz, 100MHz allocation", "5G main C-band", PdfSource),
        new(RadioAccessTechnology.Nr5g, 78, "SKT", "5G NR", "3.60-3.70GHz, 100MHz allocation", "5G main C-band", PdfSource)
    ];

    public static IReadOnlyList<KoreaAllocationMatch> Match(IReadOnlyList<BandObservation> observations)
    {
        return observations
            .Where(observation => observation.Band is not null)
            .SelectMany(observation => All
                .Where(allocation => allocation.RadioAccessTechnology == observation.RadioAccessTechnology
                    && allocation.Band == observation.Band)
                .Select(allocation => new KoreaAllocationMatch(observation, allocation)))
            .ToList();
    }
}

internal static partial class CapabilityAnalyzer
{
    public static DeviceCapabilityAssessment Assess(DeviceProfile profile, IReadOnlyList<CommandCapture> commandCaptures)
    {
        var hardwarePlatform = ReadProperty(profile, "ro.board.platform")
            ?? ReadProperty(profile, "ro.boot.hardware")
            ?? ReadProperty(profile, "ro.hardware");
        var chipsetProfile = InferChipsetProfile(hardwarePlatform, profile);
        var modemProfile = InferModemProfile(hardwarePlatform, profile);
        var preferredModes = ParsePreferredNetworkModes(profile, commandCaptures);
        var allowedRats = ParseAllowedRats(commandCaptures)
            .Concat(preferredModes.SelectMany(mode => mode.Rats))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item)
            .ToList();

        var candidateBands = BuildCandidates(allowedRats, hardwarePlatform, chipsetProfile);
        var note = "Without a USIM, Android can still expose device default/preferred RAT policy, but stock ADB generally does not expose the modem RF band mask. Candidate bands are inferred from OS-allowed RATs plus the Korea allocation table, not certified hardware band support.";
        var confidence = allowedRats.Count > 0 && candidateBands.Count > 0 ? "Medium" : "Low";

        return new DeviceCapabilityAssessment(
            hardwarePlatform,
            chipsetProfile,
            modemProfile,
            allowedRats,
            preferredModes,
            candidateBands,
            confidence,
            note);
    }

    private static string? ReadProperty(DeviceProfile profile, string key)
    {
        return profile.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string? InferChipsetProfile(string? hardwarePlatform, DeviceProfile profile)
    {
        var joined = $"{hardwarePlatform} {profile.Baseband} {ReadProperty(profile, "ro.hardware")}";
        if (joined.Contains("bengal", StringComparison.OrdinalIgnoreCase))
        {
            return "Qualcomm Bengal family; LTE-class Qualcomm platform inferred from ro.board.platform";
        }

        if (joined.Contains("qcom", StringComparison.OrdinalIgnoreCase))
        {
            return "Qualcomm platform; exact modem SKU not exposed by stock ADB";
        }

        if (joined.Contains("mt", StringComparison.OrdinalIgnoreCase) || joined.Contains("mediatek", StringComparison.OrdinalIgnoreCase))
        {
            return "MediaTek platform; exact modem SKU not exposed by stock ADB";
        }

        return null;
    }

    private static string? InferModemProfile(string? hardwarePlatform, DeviceProfile profile)
    {
        var joined = $"{hardwarePlatform} {profile.Baseband}";
        if (joined.Contains("bengal", StringComparison.OrdinalIgnoreCase)
            || joined.Contains("MPSS.HA", StringComparison.OrdinalIgnoreCase))
        {
            return "LTE/3G/2G modem profile inferred; no NR/5G capability evidence found in OS policy";
        }

        return profile.Baseband is null ? null : "Modem profile not identified; baseband string is present";
    }

    private static IReadOnlyList<PreferredNetworkMode> ParsePreferredNetworkModes(
        DeviceProfile profile,
        IReadOnlyList<CommandCapture> commandCaptures)
    {
        var rawValues = new List<string>();
        if (profile.Properties.TryGetValue("ro.telephony.default_network", out var defaultNetwork))
        {
            rawValues.Add(defaultNetwork);
        }

        rawValues.AddRange(commandCaptures
            .Where(c => c.Command.StartsWith("settings get global preferred_network_mode", StringComparison.OrdinalIgnoreCase)
                && c.ExitCode == 0)
            .Select(c => c.Output.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)));

        return rawValues
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode) ? mode : (int?)null)
            .Where(mode => mode.HasValue)
            .Select(mode => NetworkModeCatalog.Lookup(mode!.Value))
            .GroupBy(mode => mode.RawValue)
            .Select(group => group.First())
            .ToList();
    }

    private static IReadOnlyList<string> ParseAllowedRats(IReadOnlyList<CommandCapture> commandCaptures)
    {
        var allowed = commandCaptures
            .FirstOrDefault(c => c.Command == "cmd phone get-allowed-network-types-for-users" && c.ExitCode == 0)
            ?.Output ?? string.Empty;

        var values = allowed
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(MapNetworkTypeToRats)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return values;
    }

    private static IEnumerable<string> MapNetworkTypeToRats(string networkType)
    {
        var normalized = NonLettersRegex().Replace(networkType, "").ToUpperInvariant();
        if (normalized is "NR")
        {
            yield return "NR";
        }

        if (normalized.Contains("LTE", StringComparison.OrdinalIgnoreCase))
        {
            yield return "LTE";
        }

        if (normalized is "UMTS" or "HSDPA" or "HSUPA" or "HSPA" or "HSPAP" or "WCDMA")
        {
            yield return "WCDMA";
        }

        if (normalized is "GPRS" or "EDGE" or "GSM")
        {
            yield return "GSM";
        }

        if (normalized.Contains("CDMA", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("EVDO", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("EHRPD", StringComparison.OrdinalIgnoreCase))
        {
            yield return "CDMA/EVDO";
        }

        if (normalized.Contains("TDSCDMA", StringComparison.OrdinalIgnoreCase))
        {
            yield return "TD-SCDMA";
        }
    }

    private static IReadOnlyList<SupportedBandCandidate> BuildCandidates(
        IReadOnlyList<string> allowedRats,
        string? hardwarePlatform,
        string? chipsetProfile)
    {
        var supportsNr = allowedRats.Contains("NR", StringComparer.OrdinalIgnoreCase);
        var supportsLte = allowedRats.Contains("LTE", StringComparer.OrdinalIgnoreCase);
        var supportsWcdma = allowedRats.Contains("WCDMA", StringComparer.OrdinalIgnoreCase);
        var evidence = $"OS allowed RATs: {string.Join(",", allowedRats)}; platform: {hardwarePlatform ?? "unknown"}; chipset: {chipsetProfile ?? "unknown"}";

        return KoreaMobileAllocations.All
            .Where(allocation =>
                allocation.RadioAccessTechnology == RadioAccessTechnology.Nr5g && supportsNr
                || allocation.RadioAccessTechnology == RadioAccessTechnology.Lte && supportsLte
                || allocation.RadioAccessTechnology == RadioAccessTechnology.Wcdma && supportsWcdma)
            .Select(allocation => new SupportedBandCandidate(
                allocation.RadioAccessTechnology,
                allocation.Band,
                allocation.Operator,
                allocation.Service,
                allocation.Description,
                evidence,
                "Candidate"))
            .ToList();
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonLettersRegex();
}

internal static partial class VolteAnalyzer
{
    public static VolteAssessment Assess(DeviceProfile profile, IReadOnlyList<CommandCapture> commandCaptures)
    {
        var evidence = new List<string>();
        var defaultImsService = ReadOutput(commandCaptures, "cmd phone ims get-ims-service -d");
        var carrierImsService = ReadOutput(commandCaptures, "cmd phone ims get-ims-service -c");
        var deviceImsService = FirstUsable(defaultImsService, carrierImsService);
        if (deviceImsService is not null)
        {
            evidence.Add($"IMS service configured: {deviceImsService}");
        }

        var imsDumpsys = Find(commandCaptures, "dumpsys ims");
        var imsDumpsysText = imsDumpsys is null ? string.Empty : $"{imsDumpsys.Output} {imsDumpsys.Error}";
        var imsDumpsysAvailable = imsDumpsys is { ExitCode: 0 }
            && !imsDumpsysText.Contains("Can't find service", StringComparison.OrdinalIgnoreCase);
        if (!imsDumpsysAvailable && imsDumpsys is not null)
        {
            evidence.Add($"dumpsys ims unavailable: {Trim(imsDumpsysText)}");
        }

        var activityServices = ReadOutput(commandCaptures, "dumpsys activity services org.codeaurora.ims");
        var imsServiceBound = activityServices is not null
            && activityServices.Contains("BIND_IMS_SERVICE", StringComparison.OrdinalIgnoreCase)
            && activityServices.Contains("ImsService", StringComparison.OrdinalIgnoreCase);
        if (imsServiceBound)
        {
            evidence.Add("IMS service is installed and bound to com.android.phone.");
        }

        var volteAvailable = ParseCarrierBool(commandCaptures, "cmd phone cc get-value carrier_volte_available_bool", evidence);
        var provisioningRequired = ParseCarrierBool(commandCaptures, "cmd phone cc get-value carrier_volte_provisioning_required_bool", evidence);
        var currentRegistered = ParseImsRegistered(commandCaptures);
        if (currentRegistered.HasValue)
        {
            evidence.Add($"IMS registration state found: {currentRegistered.Value}");
        }

        foreach (var property in profile.Properties.Where(item => IsVolteProperty(item.Key)))
        {
            evidence.Add($"{property.Key}={property.Value}");
        }

        var status = BuildStatus(deviceImsService, imsServiceBound, imsDumpsysAvailable, volteAvailable, currentRegistered);
        var confidence = deviceImsService is not null && imsServiceBound ? "Medium" : "Low";
        var note = "VoLTE needs device IMS support, carrier config/provisioning, a valid USIM, and network IMS registration. Without a USIM, this report can verify IMS stack readiness but cannot certify carrier VoLTE availability.";

        return new VolteAssessment(
            status,
            deviceImsService,
            imsServiceBound,
            imsDumpsysAvailable,
            volteAvailable,
            provisioningRequired,
            currentRegistered,
            confidence,
            note,
            evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string BuildStatus(
        string? deviceImsService,
        bool imsServiceBound,
        bool imsDumpsysAvailable,
        bool? carrierVolteAvailable,
        bool? currentRegistered)
    {
        if (currentRegistered == true && carrierVolteAvailable == true)
        {
            return "VoLTE active/registered";
        }

        if (deviceImsService is not null && imsServiceBound)
        {
            return "IMS stack present; VoLTE carrier activation unverified";
        }

        if (deviceImsService is not null || imsDumpsysAvailable)
        {
            return "Partial IMS evidence found";
        }

        return "No IMS/VoLTE support evidence found";
    }

    private static CommandCapture? Find(IReadOnlyList<CommandCapture> captures, string command)
    {
        return captures.FirstOrDefault(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadOutput(IReadOnlyList<CommandCapture> captures, string command)
    {
        var capture = Find(captures, command);
        return capture is { ExitCode: 0 } && IsUsable(capture.Output)
            ? capture.Output.Trim()
            : null;
    }

    private static string? FirstUsable(params string?[] values)
    {
        return values.FirstOrDefault(IsUsable);
    }

    private static bool IsUsable(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ParseCarrierBool(
        IReadOnlyList<CommandCapture> captures,
        string command,
        List<string> evidence)
    {
        var capture = Find(captures, command);
        if (capture is null)
        {
            return null;
        }

        var text = $"{capture.Output} {capture.Error}".Trim();
        if (capture.ExitCode != 0)
        {
            evidence.Add($"{command}: unavailable ({Trim(text)})");
            return null;
        }

        if (TrueRegex().IsMatch(text))
        {
            evidence.Add($"{command}: true");
            return true;
        }

        if (FalseRegex().IsMatch(text))
        {
            evidence.Add($"{command}: false");
            return false;
        }

        evidence.Add($"{command}: {Trim(text)}");
        return null;
    }

    private static bool? ParseImsRegistered(IReadOnlyList<CommandCapture> captures)
    {
        var joined = string.Join(Environment.NewLine, captures
            .Where(c => c.Command.Contains("ims", StringComparison.OrdinalIgnoreCase)
                || c.Command.Contains("activity services org.codeaurora.ims", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Output));

        if (RegisteredTrueRegex().IsMatch(joined))
        {
            return true;
        }

        if (RegisteredFalseRegex().IsMatch(joined))
        {
            return false;
        }

        return null;
    }

    private static bool IsVolteProperty(string key)
    {
        return key.Contains("ims", StringComparison.OrdinalIgnoreCase)
            || key.Contains("volte", StringComparison.OrdinalIgnoreCase)
            || key.Contains("vowifi", StringComparison.OrdinalIgnoreCase)
            || key.Contains("wfc", StringComparison.OrdinalIgnoreCase)
            || key.Contains("vilte", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 220 ? normalized : normalized[..220] + "...";
    }

    [GeneratedRegex(@"\btrue\b|=\s*true|:\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex TrueRegex();

    [GeneratedRegex(@"\bfalse\b|=\s*false|:\s*false", RegexOptions.IgnoreCase)]
    private static partial Regex FalseRegex();

    [GeneratedRegex(@"registered\w*\s*[=:]\s*true|ims.*registered", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredTrueRegex();

    [GeneratedRegex(@"registered\w*\s*[=:]\s*false|ims.*not\s+registered", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredFalseRegex();
}

internal static class NetworkModeCatalog
{
    private static readonly IReadOnlyDictionary<int, PreferredNetworkMode> Modes = new Dictionary<int, PreferredNetworkMode>
    {
        [0] = new(0, "WCDMA preferred", ["GSM", "WCDMA"]),
        [1] = new(1, "GSM only", ["GSM"]),
        [2] = new(2, "WCDMA only", ["WCDMA"]),
        [3] = new(3, "GSM/WCDMA auto", ["GSM", "WCDMA"]),
        [4] = new(4, "CDMA/EVDO auto", ["CDMA/EVDO"]),
        [5] = new(5, "CDMA only", ["CDMA/EVDO"]),
        [6] = new(6, "EVDO only", ["CDMA/EVDO"]),
        [7] = new(7, "GSM/WCDMA/CDMA/EVDO auto", ["GSM", "WCDMA", "CDMA/EVDO"]),
        [8] = new(8, "LTE/CDMA/EVDO", ["LTE", "CDMA/EVDO"]),
        [9] = new(9, "LTE/GSM/WCDMA", ["LTE", "GSM", "WCDMA"]),
        [10] = new(10, "LTE/GSM/WCDMA/CDMA/EVDO", ["LTE", "GSM", "WCDMA", "CDMA/EVDO"]),
        [11] = new(11, "LTE only", ["LTE"]),
        [12] = new(12, "LTE/WCDMA", ["LTE", "WCDMA"]),
        [13] = new(13, "TD-SCDMA only", ["TD-SCDMA"]),
        [14] = new(14, "TD-SCDMA/WCDMA", ["TD-SCDMA", "WCDMA"]),
        [15] = new(15, "LTE/TD-SCDMA", ["LTE", "TD-SCDMA"]),
        [16] = new(16, "TD-SCDMA/GSM", ["TD-SCDMA", "GSM"]),
        [17] = new(17, "LTE/TD-SCDMA/GSM", ["LTE", "TD-SCDMA", "GSM"]),
        [18] = new(18, "TD-SCDMA/GSM/WCDMA", ["TD-SCDMA", "GSM", "WCDMA"]),
        [19] = new(19, "LTE/TD-SCDMA/WCDMA", ["LTE", "TD-SCDMA", "WCDMA"]),
        [20] = new(20, "LTE/TD-SCDMA/GSM/WCDMA", ["LTE", "TD-SCDMA", "GSM", "WCDMA"]),
        [21] = new(21, "TD-SCDMA/CDMA/EVDO/GSM/WCDMA", ["TD-SCDMA", "CDMA/EVDO", "GSM", "WCDMA"]),
        [22] = new(22, "LTE/TD-SCDMA/CDMA/EVDO/GSM/WCDMA", ["LTE", "TD-SCDMA", "CDMA/EVDO", "GSM", "WCDMA"]),
        [23] = new(23, "NR/LTE", ["NR", "LTE"]),
        [24] = new(24, "NR/LTE/CDMA/EVDO", ["NR", "LTE", "CDMA/EVDO"]),
        [25] = new(25, "NR/LTE/GSM/WCDMA", ["NR", "LTE", "GSM", "WCDMA"]),
        [26] = new(26, "NR/LTE/GSM/WCDMA/CDMA/EVDO", ["NR", "LTE", "GSM", "WCDMA", "CDMA/EVDO"]),
        [27] = new(27, "NR/LTE/WCDMA", ["NR", "LTE", "WCDMA"]),
        [28] = new(28, "NR/LTE/TD-SCDMA", ["NR", "LTE", "TD-SCDMA"]),
        [29] = new(29, "NR/LTE/TD-SCDMA/GSM", ["NR", "LTE", "TD-SCDMA", "GSM"]),
        [30] = new(30, "NR/LTE/TD-SCDMA/WCDMA", ["NR", "LTE", "TD-SCDMA", "WCDMA"]),
        [31] = new(31, "NR/LTE/TD-SCDMA/GSM/WCDMA", ["NR", "LTE", "TD-SCDMA", "GSM", "WCDMA"]),
        [32] = new(32, "NR/LTE/TD-SCDMA/CDMA/EVDO/GSM/WCDMA", ["NR", "LTE", "TD-SCDMA", "CDMA/EVDO", "GSM", "WCDMA"]),
        [33] = new(33, "NR/LTE/TD-SCDMA/CDMA/EVDO/GSM/WCDMA", ["NR", "LTE", "TD-SCDMA", "CDMA/EVDO", "GSM", "WCDMA"])
    };

    public static PreferredNetworkMode Lookup(int rawValue)
    {
        return Modes.TryGetValue(rawValue, out var mode)
            ? mode
            : new PreferredNetworkMode(rawValue, "Unknown", []);
    }
}

internal static class PrimaryBandAnalyzer
{
    public static PrimaryBandAssessment Assess(
        DeviceProfile profile,
        IReadOnlyList<BandObservation> currentBands,
        IReadOnlyList<KoreaAllocationMatch> koreaMatches)
    {
        var registered = currentBands.Where(item => item.IsRegistered).ToList();
        var main = registered.FirstOrDefault(item => item.RadioAccessTechnology == RadioAccessTechnology.Nr5g)
            ?? registered.FirstOrDefault(item => item.RadioAccessTechnology == RadioAccessTechnology.Lte)
            ?? registered.FirstOrDefault();

        if (main is null)
        {
            var fallback = currentBands.FirstOrDefault();
            if (fallback is not null)
            {
                return new PrimaryBandAssessment(
                    "no registered serving cell",
                    fallback.RadioAccessTechnology,
                    fallback.Band,
                    fallback.Channel,
                    ResolveOperatorHint(profile, fallback, koreaMatches),
                    "No registered serving cell was found. The RAT/band shown here is the first parsed non-registered or historical radio evidence, not the active main band.");
            }

            return new PrimaryBandAssessment(
                "unknown",
                null,
                null,
                null,
                profile.OperatorName ?? KoreanOperatorMap.FromMccMnc(profile.OperatorNumeric),
                "No serving-cell band evidence was parsed from ADB output.");
        }

        var hasNr = currentBands.Any(item => item.RadioAccessTechnology == RadioAccessTechnology.Nr5g);
        var hasRegisteredNr = registered.Any(item => item.RadioAccessTechnology == RadioAccessTechnology.Nr5g);
        var role = main.RadioAccessTechnology switch
        {
            RadioAccessTechnology.Nr5g when hasRegisteredNr => "5G SA or registered NR serving cell",
            RadioAccessTechnology.Lte when hasNr => "LTE anchor/PCell with observed 5G NR secondary carrier",
            RadioAccessTechnology.Lte => "LTE serving/PCell",
            RadioAccessTechnology.Wcdma => "3G serving cell",
            RadioAccessTechnology.Gsm => "2G serving cell",
            _ => "observed serving cell"
        };

        var operatorHint = ResolveOperatorHint(profile, main, koreaMatches);
        var note = main.RadioAccessTechnology == RadioAccessTechnology.Lte && hasNr
            ? "In 5G NSA, Android commonly reports LTE as the registered anchor while NR n78 appears as a secondary carrier. Treat LTE as the main control/anchor and NR as the 5G data layer unless NR is explicitly registered."
            : "Primary assessment is based on registered serving-cell evidence when available; otherwise it falls back to the first parsed band observation.";

        return new PrimaryBandAssessment(
            role,
            main.RadioAccessTechnology,
            main.Band,
            main.Channel,
            operatorHint,
            note);
    }

    private static string? ResolveOperatorHint(
        DeviceProfile profile,
        BandObservation main,
        IReadOnlyList<KoreaAllocationMatch> koreaMatches)
    {
        var fromDevice = profile.OperatorName ?? KoreanOperatorMap.FromMccMnc(profile.OperatorNumeric);
        if (!string.IsNullOrWhiteSpace(fromDevice))
        {
            return fromDevice;
        }

        var operators = koreaMatches
            .Where(match => match.Observation.RadioAccessTechnology == main.RadioAccessTechnology
                && match.Observation.Band == main.Band)
            .Select(match => match.Allocation.Operator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return operators.Count == 0 ? null : string.Join("/", operators);
    }
}

internal static class KoreanOperatorMap
{
    public static string? FromMccMnc(string? numeric)
    {
        if (string.IsNullOrWhiteSpace(numeric))
        {
            return null;
        }

        return numeric.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MapOne)
            .FirstOrDefault(value => value is not null);
    }

    private static string? MapOne(string numeric)
    {
        return numeric switch
        {
            "45000" or "45005" or "45012" => "SKT",
            "45002" or "45004" or "45008" => "KT",
            "45006" => "LGU+",
            _ => null
        };
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RadioAccessTechnology
{
    Gsm,
    Wcdma,
    Lte,
    Nr5g
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum EvidenceKind
{
    Observed,
    DerivedFromChannel,
    ObservedChannelOnly
}

internal sealed class AdbException(string message) : Exception(message);

internal static class StringExtensions
{
    public static IEnumerable<string> SplitLines(this string value)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

internal static class JsonSettings
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

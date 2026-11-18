using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinNotch;

/// <summary>One BBC booking today. An empty end means the person is still clocked in.</summary>
internal readonly record struct WorkBlock(TimeSpan Start, TimeSpan? End);

internal enum WorkPresence
{
    /// <summary>No reading yet.</summary>
    Unknown,
    /// <summary>A booking has a start and no end.</summary>
    Working,
    /// <summary>No booking is open, and today's gaps are still under the required break.</summary>
    OnBreak,
    /// <summary>No booking is open, and the required break is already in the gaps.</summary>
    ClockedOut,
}

/// <summary>
/// The two figures the notch shows, both taken from the extranet's total flexitime.
/// While a booking is open the balance moves with the clock. A break does not add to it.
/// </summary>
internal readonly record struct GleitzeitSnapshot(
    TimeSpan Balance,
    TimeSpan UntilNeutral,
    WorkPresence Presence,
    TimeSpan BreakTaken,
    bool Stale);

/// <summary>Pure flexitime rules. No network, no clock of its own: the caller passes the time.</summary>
internal static class Gleitzeit
{
    /// <summary>BBC requires half an hour of break every day. Gaps between bookings are that break.</summary>
    public static readonly TimeSpan RequiredBreak = TimeSpan.FromMinutes(30);

    /// <summary>A reading older than this is shown as it was. The notch does not invent minutes past it.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);

    public static TimeSpan BreakTaken(IReadOnlyList<WorkBlock> blocks, TimeSpan now)
    {
        var ordered = blocks.OrderBy(b => b.Start).ToList();
        var gaps = TimeSpan.Zero;
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1].End is { } end && ordered[i].Start > end)
                gaps += ordered[i].Start - end;
        }
        if (ordered.Count > 0 && ordered.All(b => b.End is { } end && end >= b.Start))
        {
            var lastEnd = ordered.Max(b => b.End!.Value);
            if (now > lastEnd) gaps += now - lastEnd;
        }
        return gaps < TimeSpan.Zero ? TimeSpan.Zero : gaps;
    }

    public static WorkPresence Presence(IReadOnlyList<WorkBlock> blocks, TimeSpan now)
    {
        if (blocks.Any(b => b.End == null)) return WorkPresence.Working;
        if (blocks.Count == 0) return WorkPresence.ClockedOut;
        return BreakTaken(blocks, now) < RequiredBreak ? WorkPresence.OnBreak : WorkPresence.ClockedOut;
    }

    /// <summary>
    /// Balance if the person left at <paramref name="now"/>.
    /// <paramref name="reportedIncludesOpen"/> is true once two reads show the extranet already growing the saldo
    /// while a booking is open. Until then the open booking is added from its start, because the header saldo on a
    /// closed day does not contain a running booking.
    /// </summary>
    public static TimeSpan Project(TimeSpan reportedSaldo, IReadOnlyList<WorkBlock> blocks, TimeSpan now, bool reportedIncludesOpen, TimeSpan sinceRead)
    {
        var open = blocks.Where(b => b.End == null).OrderByDescending(b => b.Start).FirstOrDefault();
        bool working = blocks.Any(b => b.End == null);
        if (!working) return reportedSaldo;
        if (reportedIncludesOpen) return reportedSaldo + (sinceRead < TimeSpan.Zero ? TimeSpan.Zero : sinceRead);
        if (now <= open.Start) return reportedSaldo;
        return reportedSaldo + (now - open.Start);
    }

    public static TimeSpan UntilNeutral(TimeSpan balance) => balance <= TimeSpan.Zero ? balance.Negate() : TimeSpan.Zero;

    public static GleitzeitSnapshot At(TimeSpan reportedSaldo, IReadOnlyList<WorkBlock> blocks, TimeSpan now, DateTimeOffset readAt, DateTimeOffset asOf, bool reportedIncludesOpen)
    {
        var sinceRead = asOf - readAt;
        bool stale = sinceRead > FreshFor || sinceRead < TimeSpan.FromMinutes(-1);
        var balance = stale
            ? reportedSaldo
            : Project(reportedSaldo, blocks, now, reportedIncludesOpen, sinceRead);
        return new GleitzeitSnapshot(balance, UntilNeutral(balance), Presence(blocks, now), BreakTaken(blocks, now), stale);
    }

    /// <summary>
    /// After a second read while a booking stays open: the extranet saldo grew with the clock, or it did not.
    /// Null when the two reads do not say either thing clearly.
    /// </summary>
    public static bool? DetectIncludesOpen(TimeSpan previousSaldo, TimeSpan nextSaldo, TimeSpan pollGap, bool wasOpen)
    {
        if (!wasOpen || pollGap < TimeSpan.FromSeconds(8)) return null;
        var grew = nextSaldo - previousSaldo;
        if ((grew - pollGap).Duration() <= TimeSpan.FromSeconds(8)) return true;
        if (grew.Duration() <= TimeSpan.FromSeconds(8)) return false;
        return null;
    }

    public static string Format(TimeSpan value, bool seconds)
    {
        int total = (int)Math.Round(value.Duration().TotalSeconds, MidpointRounding.AwayFromZero);
        int hours = total / 3600;
        int minutes = total % 3600 / 60;
        int secs = total % 60;
        string body = seconds ? $"{hours}:{minutes:00}:{secs:00}" : $"{hours}:{minutes:00}";
        if (value < TimeSpan.Zero && total != 0) return "\u2212" + body;
        if (total == 0) return seconds ? "0:00:00" : "0:00";
        return "+" + body;
    }
}

/// <summary>Pulls the total flexitime and today's bookings out of extranet JSON or the legacy page text.</summary>
internal static partial class GleitzeitParser
{
    static readonly Regex DurationText = DurationRegex();
    static readonly Regex SaldoLabel = SaldoLabelRegex();
    static readonly string[] StartNames = { "start", "from", "begin", "beginn", "startedat", "timefrom", "timestart", "starttime", "von", "checkin", "clockin", "cometime" };
    static readonly string[] EndNames = { "end", "to", "ende", "endedat", "timeto", "timeend", "endtime", "bis", "checkout", "clockout", "leavetime" };
    static readonly string[] SaldoNames = { "gleitzeitsaldo", "gleitzeitsaldoaktuell", "flextimesaldo", "flexsaldo", "runningbalance" };
    static readonly string[] SkipNameParts = { "ferien", "vacation", "holiday", "urlaub", "abschluss", "closing", "completion", "soll", "target", "aktuell" };

    public readonly record struct Parsed(TimeSpan Saldo, IReadOnlyList<WorkBlock> Today);

    public static bool TryParse(string? todayJson, string? balanceJson, string? summaryJson, string? html, out Parsed parsed)
    {
        var blocks = new List<WorkBlock>();
        CollectBlocks(todayJson, blocks);
        TimeSpan? saldo = BestSaldo(balanceJson) ?? BestSaldo(summaryJson) ?? BestSaldo(todayJson);
        if (saldo == null && html != null)
        {
            var match = SaldoLabel.Match(html);
            if (match.Success && TryDuration(match.Groups[1].Value, out var fromPage)) saldo = fromPage;
        }
        parsed = new Parsed(saldo ?? default, blocks);
        return saldo != null;
    }

    static void CollectBlocks(string? json, List<WorkBlock> blocks)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            WalkBlocks(document.RootElement, blocks);
        }
        catch (JsonException) { }
    }

    static TimeSpan? BestSaldo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            int best = 0;
            TimeSpan? found = null;
            WalkSaldo(document.RootElement, "", ref best, ref found);
            return found;
        }
        catch (JsonException) { return null; }
    }

    static void WalkBlocks(JsonElement element, List<WorkBlock> blocks)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryBlock(element, out var block) && !blocks.Any(b => b.Start == block.Start && b.End == block.End))
                    blocks.Add(block);
                foreach (var property in element.EnumerateObject()) WalkBlocks(property.Value, blocks);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) WalkBlocks(item, blocks);
                break;
        }
    }

    static void WalkSaldo(JsonElement element, string path, ref int best, ref TimeSpan? found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    string next = path + "/" + property.Name;
                    int score = SaldoScore(next, property.Name, property.Value);
                    if (score > best && TryValueDuration(property.Value, out var value))
                    {
                        best = score;
                        found = value;
                    }
                    WalkSaldo(property.Value, next, ref best, ref found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) WalkSaldo(item, path, ref best, ref found);
                break;
        }
    }

    static int SaldoScore(string path, string name, JsonElement value)
    {
        string foldedPath = Fold(path);
        if (SkipNameParts.Any(foldedPath.Contains)) return 0;
        string key = Fold(name);
        if (SkipNameParts.Any(key.Contains)) return 0;
        bool text = value.ValueKind == JsonValueKind.String;
        if (SaldoNames.Contains(key) || key.Contains("gleitzeit") && key.Contains("saldo")) return text ? 100 : 80;
        if (key.Contains("flextime") && key.Contains("balance")) return text ? 90 : 70;
        if (key is "saldo" or "balance") return text ? 40 : 0;
        return 0;
    }

    static bool TryBlock(JsonElement element, out WorkBlock block)
    {
        block = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        TimeSpan? start = null, end = null;
        bool sawEnd = false;
        foreach (var property in element.EnumerateObject())
        {
            string key = Fold(property.Name);
            if (start == null && StartNames.Contains(key) && TryValueDuration(property.Value, out var from))
                start = from;
            if (EndNames.Contains(key))
            {
                sawEnd = true;
                if (TryValueDuration(property.Value, out var to)) end = to;
            }
        }
        if (start == null || !sawEnd && end == null) return false;
        if (end is { } closed && closed < start) return false;
        block = new WorkBlock(start.Value, end);
        return true;
    }

    static bool TryValueDuration(JsonElement element, out TimeSpan value)
    {
        value = default;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryDuration(element.GetString(), out value);
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out double minutes) || minutes is < -20000 or > 20000) return false;
                value = TimeSpan.FromMinutes(minutes);
                return true;
            default:
                return false;
        }
    }

    internal static bool TryDuration(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().Replace('\u2212', '-');
        var match = DurationText.Match(text);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["h"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hours)) return false;
        if (!int.TryParse(match.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) || minutes > 59) return false;
        int seconds = 0;
        if (match.Groups["s"].Success && (!int.TryParse(match.Groups["s"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds > 59))
            return false;
        value = new TimeSpan(hours, minutes, seconds);
        if (match.Groups["sign"].Value == "-") value = value.Negate();
        return true;
    }

    static string Fold(string name) => new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"^(?<sign>[+-])?(?:(?<date>\d{4}-\d{2}-\d{2})[T ])?(?<h>\d{1,4}):(?<m>\d{2})(?::(?<s>\d{2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Gleitzeit\s*Saldo(?:(?!Gleitzeit)[\s\S]){0,120}?((?:\u2212|-|\+)?\d{1,4}:\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaldoLabelRegex();
}

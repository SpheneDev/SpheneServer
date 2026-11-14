using System.Globalization;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Expands <see cref="SyncshellCommunityEvent.RecurrenceRule"/> into concrete
/// occurrences. Grammar is a strict RRULE subset -
/// <c>FREQ=DAILY|WEEKLY|MONTHLY;INTERVAL=n;COUNT=n;UNTIL=utc;BYDAY=SU,MO,…</c> -
/// parsed defensively: anything unparseable means "no recurrence" so a bad
/// string can never generate garbage occurrences.
///
/// DST: series anchor on their <em>local</em> wall-clock time inside
/// <see cref="SyncshellCommunityEvent.TimezoneId"/> - a 19:00 Europe/Berlin
/// weekly stays 19:00 across DST while its UTC instant shifts.
/// Expansion is hard-capped (<see cref="MaxOccurrences"/>) so a series can
/// never produce unbounded results.
/// </summary>
public static class EventRecurrenceService
{
    public const int MaxOccurrences = 500;

    public enum Frequency { None = 0, Daily = 1, Weekly = 2, Monthly = 3 }

    public sealed record RecurrenceSpec(Frequency Freq, int Interval,
        List<DayOfWeek>? ByDays, int? Count, DateTime? UntilUtc);

    public sealed record Occurrence(DateTime OccurrenceStartUtc,
        DateTime? OccurrenceEndUtc, int SeriesIndex);

    /// <summary>Parses the stored rule; null/invalid → null (single event).</summary>
    public static RecurrenceSpec? ParseRule(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        Frequency freq = Frequency.None;
        var interval = 1;
        List<DayOfWeek>? byDays = null;
        int? count = null;
        DateTime? until = null;
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) return null;
            var key = kv[0].Trim().ToUpperInvariant();
            var val = kv[1].Trim();
            switch (key)
            {
                case "FREQ":
                    if (!Enum.TryParse<Frequency>(val, true, out freq)
                        || freq == Frequency.None) return null;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out interval)
                        || interval < 1 || interval > 52) return null;
                    break;
                case "BYDAY":
                    byDays = [];
                    foreach (var d in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var day = ParseDay(d.Trim());
                        if (day == null) return null;
                        byDays.Add(day.Value);
                    }
                    if (byDays.Count == 0) return null;
                    break;
                case "COUNT":
                    if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                        || c < 1 || c > MaxOccurrences) return null;
                    count = c;
                    break;
                case "UNTIL":
                    if (!DateTime.TryParse(val, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var u)) return null;
                    until = DateTime.SpecifyKind(u, DateTimeKind.Utc);
                    break;
                default:
                    return null; // unsupported key → not our subset
            }
        }
        return freq == Frequency.None ? null : new RecurrenceSpec(freq, interval, byDays, count, until);
    }

    /// <summary>Serializes a spec back to the stored grammar.</summary>
    public static string FormatRule(RecurrenceSpec spec)
    {
        var parts = new List<string> { "FREQ=" + spec.Freq.ToString().ToUpperInvariant() };
        if (spec.Interval > 1) parts.Add($"INTERVAL={spec.Interval}");
        if (spec.ByDays is { Count: > 0 } days)
            parts.Add("BYDAY=" + string.Join(',', days.Select(FormatDay)));
        if (spec.Count.HasValue) parts.Add($"COUNT={spec.Count.Value}");
        if (spec.UntilUtc.HasValue)
            parts.Add("UNTIL=" + spec.UntilUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        return string.Join(';', parts);
    }

    /// <summary>
    /// Expands occurrences of <paramref name="evt"/> inside [fromUtc, toUtc).
    /// Non-recurring events contribute at most one row (the event itself when
    /// it intersects the window). Occurrence end stays a fixed duration offset
    /// from the original start/end delta.
    /// </summary>
    public static List<Occurrence> Expand(SyncshellCommunityEvent evt,
        DateTime fromUtc, DateTime toUtc, int cap = MaxOccurrences)
    {
        var result = new List<Occurrence>();
        if (toUtc <= fromUtc) return result;
        var spec = ParseRule(evt.RecurrenceRule);
        var duration = evt.EndUtc.HasValue ? evt.EndUtc.Value - evt.StartUtc : (TimeSpan?)null;
        if (spec == null)
        {
            if (evt.StartUtc < toUtc && (evt.EndUtc ?? evt.StartUtc) >= fromUtc)
                result.Add(new(evt.StartUtc, evt.EndUtc, 0));
            return result;
        }

        var zone = ResolveZone(evt.TimezoneId);
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(evt.StartUtc, DateTimeKind.Utc), zone);
        var untilUtc = spec.UntilUtc;
        var produced = 0;

        // Candidate local "series days": for weekly+BYDAY we enumerate the
        // matching weekdays; otherwise a step per interval from the anchor.
        var cursorLocal = localStart.Date; // anchor keeps its wall time
        var anchorTime = localStart.TimeOfDay;
        var hardStop = toUtc.AddDays(370); // safety net for window+until math
        while (produced < spec.Count.GetValueOrDefault(MaxOccurrences)
               && result.Count < cap && cursorLocal < hardStop)
        {
            foreach (var local in CandidateLocals(spec, cursorLocal, localStart, anchorTime))
            {
                var occUtc = LocalToUtc(zone, local);
                if (occUtc < evt.StartUtc) continue;                 // never before anchor
                if (untilUtc.HasValue && occUtc > untilUtc.Value) break;
                produced++;
                if (produced > MaxOccurrences) break;
                if (occUtc >= toUtc) { produced = spec.Count.GetValueOrDefault(MaxOccurrences); break; }
                if (occUtc >= fromUtc || (duration.HasValue && occUtc + duration.Value >= fromUtc))
                    result.Add(new(occUtc, duration.HasValue ? occUtc + duration.Value : null,
                        produced - 1));
            }
            cursorLocal = NextCycle(spec, cursorLocal, localStart);
        }
        return result;
    }

    /// <summary>The occurrence immediately at/after <paramref name="afterUtc"/>,
    /// or null - used by feeds and the reminder sweep.</summary>
    public static Occurrence? NextOccurrence(SyncshellCommunityEvent evt, DateTime afterUtc)
    {
        var window = Expand(evt, afterUtc, afterUtc.AddDays(370), cap: 1);
        return window.Count > 0 ? window[0] : null;
    }

    private static IEnumerable<DateTime> CandidateLocals(RecurrenceSpec spec,
        DateTime cycleStartLocal, DateTime anchorLocal, TimeSpan anchorTime)
    {
        switch (spec.Freq)
        {
            case Frequency.Weekly when spec.ByDays is { Count: > 0 } days:
            {
                // Every matching weekday inside this interval-week, wall time.
                var weekStart = cycleStartLocal.Date;
                foreach (var day in days.OrderBy(d => ((int)d + 7 - (int)weekStart.DayOfWeek) % 7))
                {
                    var offset = ((int)day - (int)weekStart.DayOfWeek + 7) % 7;
                    var candidate = weekStart.AddDays(offset) + anchorTime;
                    if (candidate >= anchorLocal) yield return candidate;
                }
                yield break;
            }
            case Frequency.Weekly:
            {
                var aligned = FirstOnOrAfter(cycleStartLocal, anchorLocal.DayOfWeek) + anchorTime;
                if (aligned >= anchorLocal) yield return aligned;
                yield break;
            }
            default:
            {
                // Monthly anchors clamp to month length (Jan 31 → Feb 28 →
                // Mar 31) instead of drifting or skipping short months.
                var dim = DateTime.DaysInMonth(cycleStartLocal.Year, cycleStartLocal.Month);
                var day = spec.Freq == Frequency.Monthly
                    ? Math.Min(anchorLocal.Day, dim)
                    : cycleStartLocal.Day;
                var candidate = new DateTime(cycleStartLocal.Year, cycleStartLocal.Month, day)
                    + anchorTime;
                if (candidate >= anchorLocal) yield return candidate;
                yield break;
            }
        }
    }

    private static DateTime NextCycle(RecurrenceSpec spec, DateTime cursorLocal, DateTime anchorLocal)
        => spec.Freq switch
    {
        Frequency.Daily => cursorLocal.AddDays(spec.Interval),
        Frequency.Weekly => cursorLocal.AddDays(7 * spec.Interval),
        // Advance from the anchor (not the clamped cursor) so long-day
        // anchors self-correct: Jan 31 → Feb 28 → Mar 31.
        Frequency.Monthly => anchorLocal.AddMonths(
            (cursorLocal.Year - anchorLocal.Year) * 12
            + cursorLocal.Month - anchorLocal.Month + spec.Interval),
        _ => cursorLocal.AddYears(1000), // unreachable - terminates the loop
    };

    private static DateTime FirstOnOrAfter(DateTime from, DayOfWeek day)
    {
        var delta = ((int)day - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(delta).Date;
    }

    private static DateTime LocalToUtc(TimeZoneInfo zone, DateTime local)
    {
        if (local.Kind == DateTimeKind.Unspecified)
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Ambiguous wall times (fall-back hour) map to the earlier instant;
        // invalid times (spring-forward gap) shift to the zone's next valid.
        if (zone.IsInvalidTime(local))
            local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    private static DayOfWeek? ParseDay(string s) => s.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
        _ => null,
    };

    private static string FormatDay(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => "SU", DayOfWeek.Monday => "MO", DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE", DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR", _ => "SA",
    };
}

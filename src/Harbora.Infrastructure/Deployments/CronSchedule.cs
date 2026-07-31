using System.Globalization;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// A five-field cron expression, parsed and asked "when next?".
///
/// Written rather than taken from a library because the whole surface needed is one question — the
/// next occurrence after a given instant — and because a scheduler that silently drifts, double-fires
/// or never fires is the kind of bug nobody notices until a nightly job has been dead for a month.
/// Every rule here is one testable statement.
///
/// Supported per field: <c>*</c>, a number, a list (<c>1,15</c>), a range (<c>1-5</c>) and a step
/// (<c>*/10</c>, <c>1-30/5</c>). Deliberately no <c>@daily</c> aliases, no seconds field, no <c>L</c>
/// or <c>#</c> — anything not understood is refused at the point someone types it rather than
/// interpreted into something they did not mean.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32];   // 1..31
    private readonly bool[] _months = new bool[13];        // 1..12
    private readonly bool[] _daysOfWeek = new bool[7];     // 0 = Sunday

    /// <summary>True when the expression restricts both day-of-month and day-of-week.</summary>
    private readonly bool _bothDayFieldsRestricted;

    public string Expression { get; }

    private CronSchedule(string expression, bool bothDayFieldsRestricted)
    {
        Expression = expression;
        _bothDayFieldsRestricted = bothDayFieldsRestricted;
    }

    /// <summary>Parses, or explains why it will not. Never throws.</summary>
    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;

        var text = expression?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "Enter a schedule, for example \"0 3 * * *\" for 03:00 every day.";
            return false;
        }

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"A schedule has five fields — minute, hour, day of month, month, day of week — " +
                    $"but this has {fields.Length}. Example: \"0 3 * * *\" is 03:00 every day.";
            return false;
        }

        var result = new CronSchedule(text, fields[2] != "*" && fields[4] != "*");

        if (!Fill(fields[0], 0, 59, result._minutes, "minute", ref error)) return false;
        if (!Fill(fields[1], 0, 23, result._hours, "hour", ref error)) return false;
        if (!Fill(fields[2], 1, 31, result._daysOfMonth, "day of month", ref error)) return false;
        if (!Fill(fields[3], 1, 12, result._months, "month", ref error)) return false;
        if (!Fill(fields[4], 0, 6, result._daysOfWeek, "day of week", ref error)) return false;

        schedule = result;
        return true;
    }

    /// <summary>
    /// The first matching minute strictly after <paramref name="after"/>, or null if the expression
    /// can never match (29 February in a month that has no 29th, say).
    /// </summary>
    public DateTimeOffset? NextOccurrence(DateTimeOffset after)
    {
        // Start at the next whole minute: a job due at 03:00 must not fire twice within that minute.
        var candidate = new DateTimeOffset(
            after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Offset).AddMinutes(1);

        // Four years covers every leap-year combination, so a schedule that never matches is reported
        // rather than looped over forever.
        var limit = candidate.AddYears(4);
        while (candidate < limit)
        {
            if (!_months[candidate.Month])
            {
                candidate = new DateTimeOffset(candidate.Year, candidate.Month, 1, 0, 0, 0, candidate.Offset).AddMonths(1);
                continue;
            }
            if (!MatchesDay(candidate))
            {
                candidate = new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, candidate.Offset).AddDays(1);
                continue;
            }
            if (!_hours[candidate.Hour])
            {
                candidate = candidate.AddHours(1).AddMinutes(-candidate.Minute);
                continue;
            }
            if (!_minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Standard cron's odd rule, kept deliberately: when both day fields are restricted a day matches
    /// if <b>either</b> does. Every other cron behaves this way, and quietly differing would make
    /// schedules copied from elsewhere fire on the wrong days.
    /// </summary>
    private bool MatchesDay(DateTimeOffset moment)
    {
        var byMonth = _daysOfMonth[moment.Day];
        var byWeek = _daysOfWeek[(int)moment.DayOfWeek];
        return _bothDayFieldsRestricted ? byMonth || byWeek : byMonth && byWeek;
    }

    private static bool Fill(string field, int min, int max, bool[] into, string name, ref string? error)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var range = part;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"\"{part}\" is not a valid step in the {name} field.";
                    return false;
                }
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else if (range.Contains('-'))
            {
                var ends = range.Split('-');
                if (ends.Length != 2
                    || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out from)
                    || !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out to))
                {
                    error = $"\"{range}\" is not a valid range in the {name} field.";
                    return false;
                }
            }
            else if (int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out var single))
            {
                (from, to) = (single, single);
            }
            else
            {
                error = $"\"{part}\" is not a valid {name}.";
                return false;
            }

            // Sunday is both 0 and 7 in common usage; accepting 7 avoids a schedule that silently
            // never runs.
            if (name == "day of week")
            {
                if (from == 7) from = 0;
                if (to == 7) to = 0;
            }

            if (from < min || to > max || from > to)
            {
                error = $"The {name} field accepts {min}–{max}, but got \"{part}\".";
                return false;
            }

            for (var value = from; value <= to; value += step) into[value] = true;
        }

        return true;
    }

    /// <summary>A plain-language reading, so someone can check the expression means what they meant.</summary>
    public static string Describe(string expression)
    {
        if (!TryParse(expression, out _, out _)) return "";

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (minute, hour, dayOfMonth, month, dayOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);

        if (minute.StartsWith("*/") && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
            return $"Every {minute[2..]} minutes.";
        if (minute == "*" && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
            return "Every minute.";
        if (int.TryParse(minute, out var m) && hour == "*")
            return $"At {m} minutes past every hour.";
        if (int.TryParse(minute, out var mm) && int.TryParse(hour, out var hh))
        {
            var time = $"{hh:00}:{mm:00}";
            if (dayOfMonth == "*" && month == "*" && dayOfWeek == "*") return $"At {time} every day.";
            if (dayOfWeek != "*") return $"At {time} on day {dayOfWeek} of the week.";
            if (dayOfMonth != "*") return $"At {time} on day {dayOfMonth} of the month.";
        }

        return "Runs on the given schedule.";
    }
}

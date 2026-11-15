#nullable enable
using System.Globalization;
using CosmosDB.InMemoryEmulator.Domain.Primitives;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos SQL date and time functions (<c>GETCURRENTDATETIME</c>, <c>DATETIMEADD</c>,
/// <c>DATETIMEPART</c>, <c>DATETIMEDIFF</c>, <c>DATETIMEFROMPARTS</c>, <c>DATETIMEBIN</c>,
/// <c>DATETIMETOTICKS</c>, <c>TICKSTODATETIME</c>, <c>DATETIMETOTIMESTAMP</c>, <c>TIMESTAMPTODATETIME</c>).
/// </summary>
internal static class DateTimeFunctions
{
    public static bool TryEvaluate(
        string functionName,
        object?[] args,
        QueryExecutionContext context,
        out object? result)
    {
        switch (functionName)
        {
            case "GETCURRENTDATETIME":
            case "GETCURRENTDATETIMESTATIC":
                result = context.StaticDateTime;
                return true;

            case "GETCURRENTTIMESTAMP":
            case "GETCURRENTTIMESTAMPSTATIC":
                result = context.StaticTimestamp;
                return true;

            case "GETCURRENTTICKS":
            case "GETCURRENTTICKSSTATIC":
                result = context.StaticTicks;
                return true;

            case "DATETIMEADD":
                if (args.Length < 3) { result = UndefinedValue.Instance; return true; }
                var addPart = args[0]?.ToString()?.ToLowerInvariant();
                var addNumber = SqlMathHelpers.ToLong(args[1]);
                var addDateTimeStr = args[2] is not null and not UndefinedValue ? args[2]?.ToString() : null;
                if (addPart is null || !addNumber.HasValue || addDateTimeStr is null ||
                    !DateTime.TryParse(addDateTimeStr, null, DateTimeStyles.RoundtripKind, out var addDt))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var n = (int)addNumber.Value;
                try
                {
                    DateTime? added = addPart switch
                    {
                        "year" or "yyyy" or "yy" => addDt.AddYears(n),
                        "month" or "mm" or "m" => addDt.AddMonths(n),
                        "day" or "dd" or "d" => addDt.AddDays(n),
                        "hour" or "hh" => addDt.AddHours(n),
                        "minute" or "mi" or "n" => addDt.AddMinutes(n),
                        "second" or "ss" or "s" => addDt.AddSeconds(n),
                        "millisecond" or "ms" => addDt.AddMilliseconds(n),
                        "microsecond" or "mcs" => addDt.AddTicks(n * 10L),
                        "nanosecond" or "ns" => addDt.AddTicks(n / 100L),
                        _ => null,
                    };
                    result = added.HasValue ? added.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") : UndefinedValue.Instance;
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "DATETIMEPART":
                if (args.Length < 2) { result = UndefinedValue.Instance; return true; }
                var partName = args[0]?.ToString()?.ToLowerInvariant();
                var partDtStr = args[1] is not null and not UndefinedValue ? args[1]?.ToString() : null;
                if (partName is null || partDtStr is null ||
                    !DateTime.TryParse(partDtStr, null, DateTimeStyles.RoundtripKind, out var partDt))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = partName switch
                {
                    "year" or "yyyy" or "yy" => (object)(long)partDt.Year,
                    "month" or "mm" or "m" => (long)partDt.Month,
                    "day" or "dd" or "d" => (long)partDt.Day,
                    "hour" or "hh" => (long)partDt.Hour,
                    "minute" or "mi" or "n" => (long)partDt.Minute,
                    "second" or "ss" or "s" => (long)partDt.Second,
                    "millisecond" or "ms" => (long)partDt.Millisecond,
                    "microsecond" or "mcs" => (long)(partDt.Ticks % TimeSpan.TicksPerSecond / 10),
                    "nanosecond" or "ns" => (long)(partDt.Ticks % TimeSpan.TicksPerSecond * 100),
                    "weekday" or "dw" or "w" => (long)(partDt.DayOfWeek + 1),
                    _ => null,
                };
                return true;

            case "DATETIMEDIFF":
                if (args.Length < 3) { result = UndefinedValue.Instance; return true; }
                var diffPart = args[0]?.ToString()?.ToLowerInvariant();
                var startStr = args[1]?.ToString();
                var endStr = args[2]?.ToString();
                if (diffPart is null || startStr is null || endStr is null ||
                    !DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out var dtStart) ||
                    !DateTime.TryParse(endStr, null, DateTimeStyles.RoundtripKind, out var dtEnd))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                result = diffPart switch
                {
                    "year" or "yyyy" or "yy" => (object)(long)(dtEnd.Year - dtStart.Year),
                    "month" or "mm" or "m" => (long)((dtEnd.Year - dtStart.Year) * 12 + dtEnd.Month - dtStart.Month),
                    "day" or "dd" or "d" => (long)(dtEnd.Date - dtStart.Date).TotalDays,
                    "hour" or "hh" => (long)(SqlMathHelpers.FloorToUnit(dtEnd, TimeSpan.TicksPerHour) - SqlMathHelpers.FloorToUnit(dtStart, TimeSpan.TicksPerHour)),
                    "minute" or "mi" or "n" => (long)(SqlMathHelpers.FloorToUnit(dtEnd, TimeSpan.TicksPerMinute) - SqlMathHelpers.FloorToUnit(dtStart, TimeSpan.TicksPerMinute)),
                    "second" or "ss" or "s" => (long)(SqlMathHelpers.FloorToUnit(dtEnd, TimeSpan.TicksPerSecond) - SqlMathHelpers.FloorToUnit(dtStart, TimeSpan.TicksPerSecond)),
                    "millisecond" or "ms" => (long)(SqlMathHelpers.FloorToUnit(dtEnd, TimeSpan.TicksPerMillisecond) - SqlMathHelpers.FloorToUnit(dtStart, TimeSpan.TicksPerMillisecond)),
                    "microsecond" or "mcs" => (long)((dtEnd - dtStart).Ticks / 10),
                    "nanosecond" or "ns" => (long)((dtEnd - dtStart).Ticks * 100),
                    _ => UndefinedValue.Instance,
                };
                return true;

            case "DATETIMEFROMPARTS":
                if (args.Length < 3) { result = UndefinedValue.Instance; return true; }
                var y = SqlMathHelpers.ToLong(args[0]);
                var mo = SqlMathHelpers.ToLong(args[1]);
                var d = SqlMathHelpers.ToLong(args[2]);
                var h = args.Length > 3 ? SqlMathHelpers.ToLong(args[3]) : 0;
                var mi = args.Length > 4 ? SqlMathHelpers.ToLong(args[4]) : 0;
                var s = args.Length > 5 ? SqlMathHelpers.ToLong(args[5]) : 0;
                var fraction = args.Length > 6 ? SqlMathHelpers.ToLong(args[6]) : 0;
                if (!y.HasValue || !mo.HasValue || !d.HasValue || !h.HasValue || !mi.HasValue || !s.HasValue || !fraction.HasValue ||
                    fraction.Value < 0 || fraction.Value > 9999999)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                try
                {
                    var dt = new DateTime((int)y.Value, (int)mo.Value, (int)d.Value,
                        (int)h.Value, (int)mi.Value, (int)s.Value, DateTimeKind.Utc).AddTicks(fraction.Value);
                    result = dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "DATETIMEBIN":
                if (args.Length < 2) { result = UndefinedValue.Instance; return true; }
                var binDtStr = args[0] is not null and not UndefinedValue ? args[0]?.ToString() : null;
                var binPart = args[1]?.ToString()?.ToLowerInvariant();
                var binSize = args.Length >= 3 ? SqlMathHelpers.ToLong(args[2]) : 1;
                if (binDtStr is null || binPart is null || !binSize.HasValue ||
                    !DateTime.TryParse(binDtStr, null, DateTimeStyles.RoundtripKind, out var binDt))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                var bs = (int)binSize.Value;
                if (bs <= 0) { result = UndefinedValue.Instance; return true; }
                DateTime origin;
                if (args.Length >= 4)
                {
                    if (args[3] is not string originStr || !DateTime.TryParse(originStr, null, DateTimeStyles.RoundtripKind, out origin))
                    {
                        result = UndefinedValue.Instance;
                        return true;
                    }
                }
                else
                {
                    origin = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                }
                if (origin.Year < 1601) { result = UndefinedValue.Instance; return true; }
                if (binPart is "year" or "yyyy" or "yy")
                {
                    var yearBin = (int)(Math.Floor((double)(binDt.Year - origin.Year) / bs) * bs);
                    binDt = new DateTime(origin.Year + yearBin, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                }
                else if (binPart is "month" or "mm" or "m")
                {
                    var totalMonths = (binDt.Year - origin.Year) * 12 + (binDt.Month - origin.Month);
                    var binned = (int)(Math.Floor((double)totalMonths / bs) * bs);
                    binDt = new DateTime(origin.Year, origin.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(binned);
                }
                else
                {
                    var ticksPerUnit = binPart switch
                    {
                        "day" or "dd" or "d" => TimeSpan.TicksPerDay,
                        "hour" or "hh" => TimeSpan.TicksPerHour,
                        "minute" or "mi" or "n" => TimeSpan.TicksPerMinute,
                        "second" or "ss" or "s" => TimeSpan.TicksPerSecond,
                        "millisecond" or "ms" => TimeSpan.TicksPerMillisecond,
                        "microsecond" or "mcs" => 10L,
                        "nanosecond" or "ns" => 1L,
                        _ => -1L,
                    };
                    if (ticksPerUnit < 0) { result = UndefinedValue.Instance; return true; }
                    var tickSpan = (binDt - origin).Ticks;
                    var binTicks = ticksPerUnit * bs;
                    var binnedTicks = (long)Math.Floor((double)tickSpan / binTicks) * binTicks;
                    binDt = origin.AddTicks(binnedTicks);
                }
                result = binDt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                return true;

            case "DATETIMETOTICKS":
                if (args.Length < 1) { result = null; return true; }
                var toTicksStr = args[0]?.ToString();
                if (toTicksStr is null || !DateTime.TryParse(toTicksStr, null, DateTimeStyles.RoundtripKind, out var toTicksDt))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                if (toTicksDt.Kind == DateTimeKind.Unspecified) toTicksDt = DateTime.SpecifyKind(toTicksDt, DateTimeKind.Utc);
                result = toTicksDt.ToUniversalTime().Ticks;
                return true;

            case "TICKSTODATETIME":
                if (args.Length < 1) { result = UndefinedValue.Instance; return true; }
                var ticksVal = SqlMathHelpers.ToLong(args[0]);
                if (!ticksVal.HasValue) { result = UndefinedValue.Instance; return true; }
                try
                {
                    var fromTicksDt = new DateTime(ticksVal.Value, DateTimeKind.Utc);
                    result = fromTicksDt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = UndefinedValue.Instance;
                }
                return true;

            case "DATETIMETOTIMESTAMP":
                if (args.Length < 1) { result = UndefinedValue.Instance; return true; }
                var toTsStr = args[0]?.ToString();
                if (toTsStr is null || !DateTime.TryParse(toTsStr, null, DateTimeStyles.RoundtripKind, out var toTsDt))
                {
                    result = UndefinedValue.Instance;
                    return true;
                }
                if (toTsDt.Kind == DateTimeKind.Unspecified) toTsDt = DateTime.SpecifyKind(toTsDt, DateTimeKind.Utc);
                result = new DateTimeOffset(toTsDt.ToUniversalTime()).ToUnixTimeMilliseconds();
                return true;

            case "TIMESTAMPTODATETIME":
                if (args.Length < 1) { result = UndefinedValue.Instance; return true; }
                var msVal = SqlMathHelpers.ToLong(args[0]);
                if (!msVal.HasValue) { result = UndefinedValue.Instance; return true; }
                var fromTsDt = DateTimeOffset.FromUnixTimeMilliseconds(msVal.Value).UtcDateTime;
                result = fromTsDt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
                return true;

            default:
                result = null;
                return false;
        }
    }
}

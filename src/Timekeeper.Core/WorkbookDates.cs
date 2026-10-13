using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Timekeeper.Core;

public static partial class EmailWorkbookReader
{
    // Convert only schema date fields. Formatting never turns IDs, minutes or
    // descriptions into dates. General-format and text serials also work here.
    private static void NormalizeDateCells(SortedDictionary<int, Dictionary<int, string>> cells, string name, bool use1904Dates)
    {
        int reviewHeader = name == "Review"
            ? cells.FirstOrDefault(r => Enumerable.Range(1, ReviewHeaders.Length).All(i => r.Value.GetValueOrDefault(i) == ReviewHeaders[i - 1])).Key
            : 0;
        foreach (var (number, row) in cells)
        {
            int column = name switch
            {
                "Metadata" when number > 1 && row.GetValueOrDefault(1) is "period_start" or "period_end" => 2,
                "Activities" when number > 1 => 2,
                "Review" when reviewHeader > 0 && number > reviewHeader && !string.Equals(row.GetValueOrDefault(1), "TOTAL", StringComparison.OrdinalIgnoreCase) => 1,
                _ => 0
            };
            if (column == 0 || string.IsNullOrEmpty(row.GetValueOrDefault(column))) continue;
            row[column] = WorkbookDate(row[column], use1904Dates, $"{name}!{(char)('A' + column - 1)}{number}").ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private static DateOnly WorkbookDate(string value, bool use1904Dates, string cell)
    {
        if (DateOnly.TryParseExact(value, ["yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d",
            "d MMM yyyy", "d MMMM yyyy", "MMM d, yyyy", "MMMM d, yyyy", "MMM d yyyy", "MMMM d yyyy", "d-MMM-yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) return date;

        // Keep an ISO date/time's written calendar date without applying the PC's
        // time zone. Time of day is irrelevant in the work-date columns.
        if (Regex.IsMatch(value, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?\z"))
        {
            try
            {
                _ = XmlConvert.ToDateTimeOffset(value);
                return Date(value[..10], cell);
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
            { throw Bad($"{cell} contains an invalid date/time '{value}'."); }
        }

        var numericText = Regex.Match(value, @"\A(\d{1,2})([/\-])(\d{1,2})\2(\d{4})\z");
        if (numericText.Success)
        {
            int first = int.Parse(numericText.Groups[1].Value, CultureInfo.InvariantCulture);
            int second = int.Parse(numericText.Groups[3].Value, CultureInfo.InvariantCulture);
            if (first is >= 1 and <= 12 && second is >= 1 and <= 12 && first != second)
                throw Bad($"{cell} has an ambiguous date '{value}'. Use YYYY-MM-DD or a real Excel date cell so the month and day are clear.");
            int month = first > 12 ? second : first;
            int day = first > 12 ? first : second;
            if (DateOnly.TryParseExact($"{numericText.Groups[4].Value}-{month:D2}-{day:D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
        }

        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            // Excel 1900 serial 60 is a fictitious leap day; reject it instead of
            // silently substituting another date. The 1904 epoch has no such gap.
            var epoch = new DateOnly(use1904Dates ? 1904 : 1899, use1904Dates ? 1 : 12, use1904Dates ? 1 : 31);
            decimal wholeDays = decimal.Floor(serial);
            if ((!use1904Dates && wholeDays < 1) || (use1904Dates && wholeDays < 0) || (!use1904Dates && wholeDays == 60))
                throw Bad($"{cell} contains an invalid Excel date serial '{value}'. Use YYYY-MM-DD or a valid Excel date.");
            decimal days = wholeDays - (!use1904Dates && wholeDays > 60 ? 1 : 0);
            if (days > DateOnly.MaxValue.DayNumber - epoch.DayNumber)
                throw Bad($"{cell} contains an out-of-range Excel date serial '{value}'.");
            return epoch.AddDays((int)days);
        }
        throw Bad($"{cell} must be a valid Excel date or recognizable date text (prefer YYYY-MM-DD). Received '{value}'.");
    }
}

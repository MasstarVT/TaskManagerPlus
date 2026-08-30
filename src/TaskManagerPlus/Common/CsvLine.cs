using System.Text;

namespace TaskManagerPlus.Common;

/// <summary>
/// The one shared quoted-CSV line splitter (Round 18, #1085) - the byte-identical private
/// ParseCsvLine that had been pasted into six services (DriverInventoryService,
/// ScheduledTaskService, KernelModuleService, BloatwareInventoryService,
/// KernelEventFamilyService, LogReplayService), consolidated so a future quoting bug is fixable
/// once. Handles the RFC-4180 core the tools these services parse (driverquery /fo csv,
/// schtasks /fo csv, PowerShell ConvertTo-Csv) all emit: comma-separated fields, optional
/// double-quote wrapping, and a doubled quote ("") inside a quoted field meaning one literal
/// quote. Deliberately single-line - none of these tools emit embedded newlines, and the callers
/// all split on lines first. (UsnJournalService keeps its own splitter: fsutil's CSV never
/// escapes quotes, and that parser's quote handling intentionally differs.)
/// </summary>
public static class CsvLine
{
    /// <summary>Splits one CSV line into its fields, honoring quotes. Always returns at least
    /// one field (an empty line yields a single empty field), matching string.Split semantics.</summary>
    public static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}

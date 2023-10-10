using System.Diagnostics;
using System.Globalization;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using CsvHelper;
using tempo_reporter;
using TimeSpanParserUtil;

[Command("import", Description = "Replaces all tempo worklogs for a given day with data contained in CSV read from stdin.")]
public class ImportCommand : BaseJiraCommand, ICommand
{
    [CommandOption(
        "file",
        'f',
        Description = "Csv file to import. Defaults to std input")]
    public string? File { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var input = OpenInputFile(console);
        var data = ParseCsv(input, console);

        var importList = await MatchImportRowsToExistingWorkItems(data, console);

        var startTimes = new Dictionary<DateOnly, TimeOnly>();
        foreach (var item in importList) 
            await ImportCsvRow(item, startTimes, console);

        if (input != console.Input)
            input.Dispose();
    }

    private async Task<List<ImportItem>> MatchImportRowsToExistingWorkItems(List<CsvRow> data, IConsole console)
    {
        Debug.Assert(JiraClient != null, nameof(JiraClient) + " != null");
        var issueIdMap = await JiraClient.GetIssueIdMap(data.Select(d => d.IssueKey));
        List<ImportItem> importList = new(data.Count);
        var unmatched = data.ToList();
        foreach (var worklog in await JiraClient.GetWorklogs(data.Select(d => d.Date)))
        {
            var key = issueIdMap.FirstOrDefault(m => m.Id == worklog.IssueId).Key;
            if (key != null)
            {
                //find a CsvRow for the same issue on the same day, and say that it matches this worklog
                //the match makes that row ineligible to match other worklogs
                var row = unmatched.FirstOrDefault(r => r.IssueKey == key && r.Date.ToDateTime(default) == worklog.StartDate.Date);
                if (row == null)
                {
                    console.Output.WriteLine($"Deleting worklog for {worklog.IssueId} on {worklog.Started:yyyy-MM-dd}");
                    await JiraClient.DeleteWorklog(worklog);
                }
                else
                {
                    importList.Add(new ImportItem(row, worklog));
                    unmatched.Remove(row);
                }
            }
        }

        foreach (var row in unmatched)
            importList.Add(new ImportItem(row, null));
        importList.Sort((a, b) => a.Row.Date.CompareTo(b.Row.Date));
        return importList;
    }

    private StreamReader OpenInputFile(IConsole console)
    {
        StreamReader input;
        if (string.IsNullOrEmpty(File))
        {
            input = console.Input;
        }
        else
        {
            if (File.StartsWith('~'))
            {
                var homeDir = Environment.GetEnvironmentVariable("HOME") ??
                              Environment.GetEnvironmentVariable("USERPROFILE") ??
                              throw new InvalidOperationException($"Unable to find home dir. Cannot expand ~ in '{File}'");
                File = Path.Combine(homeDir, File.Substring(1).TrimStart('/', '\\'));
            }

            input = new StreamReader(new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        return input;
    }

    private static List<CsvRow> ParseCsv(StreamReader input, IConsole console)
    {
        List<CsvRow> data = new();

        using var csv = new CsvReader(input, CultureInfo.CurrentCulture);
        csv.Context.TypeConverterCache.AddConverter<TimeSpan>(new PermissiveTimeSpanConverter());
        var readHeaders = true;
        var hasDescription = false;
        var isValid = true;
        while (csv.Read())
        {
            if (readHeaders)
            {
                csv.ReadHeader();
                readHeaders = false;
                var headers = csv.HeaderRecord ?? throw new InvalidOperationException("Missing csv headers");
                hasDescription = headers.Any(h => h.Equals("Description", StringComparison.OrdinalIgnoreCase));

                if (headers.All(h => !string.Equals(h, "Date") && !string.Equals(h, "Time") && !string.Equals(h, "IssueKey")))
                {
                    console.Output.WriteLine("Invalid CSV file. Column headers are required and must include Date, Time, and IssueKey.");
                    break;
                }

                continue;
            }

            var date = csv.GetField<string?>("Date");
            var time = csv.GetField<string?>("Time");
            var key = csv.GetField<string?>("IssueKey");
            var desc = hasDescription ? csv.GetField<string?>("Description") : null;
            if (!DateOnly.TryParse(date, out var dt))
            {
                console.Output.WriteLine($"Invalid or missing Date on row {csv.CurrentIndex}: '{date}'");
                isValid = false;
            }

            if (!TimeSpanParser.TryParse(time, out var t))
            {
                if (double.TryParse(time, out var d))
                {
                    t = TimeSpan.FromSeconds(d);
                }
                else
                {
                    console.Output.WriteLine($"Invalid or missing Time on row {csv.CurrentIndex}: '{time}'");
                    isValid = false;
                }
            }

            if (string.IsNullOrEmpty(key))
            {
                console.Output.WriteLine($"Missing IssueKey on row {csv.CurrentIndex}");
                isValid = false;
            }

            if (isValid)
                data.Add(new CsvRow {Date = dt, Time = t, IssueKey = key!, Description = desc});
        }

        if (!isValid)
            throw new CommandException("Please fix CSV data and retry the command");

        return data;
    }

    private async Task ImportCsvRow(ImportItem item, Dictionary<DateOnly, TimeOnly> startTimes, IConsole console)
    {
        var row = item.Row;
        var timeSpent = row.Time;
        if (!startTimes.TryGetValue(row.Date, out var startTime))
            startTime = new TimeOnly(8, 0);
        startTimes[row.Date] = startTime.Add(timeSpent).AddMinutes(1);
        var worklogDateTime = row.Date.ToDateTime(startTime, DateTimeKind.Local).ToUniversalTime();

        var timeSpentDescription = timeSpent.GetHoursMinutesString();
        Debug.Assert(JiraClient != null, nameof(JiraClient) + " != null");
        if (item.ExistingWorklog == null)
        {
            console.Output.WriteLine(
                "Creating worklog for issue {0} on {1:yyyy-MM-dd} for {2}",
                row.IssueKey,
                worklogDateTime,
                timeSpent.GetHoursMinutesString());
            await JiraClient.CreateWorklog(row.IssueKey, worklogDateTime, timeSpent, row.Description);
        }
        else
            await UpdateWorklog(item, worklogDateTime, timeSpentDescription, console);
    }

    private async Task UpdateWorklog(ImportItem item, DateTime worklogDateTime, string timeSpentDescription, IConsole console)
    {
        var (row, worklog) = item;
        ArgumentNullException.ThrowIfNull(worklog);

        var description = row.Description ?? $"Working on issue {row.IssueKey}";
        var timeSpentSeconds = (int)row.Time.TotalSeconds;

        var started = worklog.StartDate.ToUniversalTime().DateTime;
        if (worklog.Comment == description &&
            started == worklogDateTime &&
            worklog.TimeSpentSeconds == timeSpentSeconds)
        {
            return; // nothing to do
        }

        var oldTime = TimeSpan.FromSeconds(worklog.TimeSpentSeconds).GetHoursMinutesString();
        console.Output.WriteLine($"Updating worklog for issue {row.IssueKey} on {row.Date:yyyy-MM-dd} from {oldTime} to {timeSpentDescription}");
        Debug.Assert(JiraClient != null, nameof(JiraClient) + " != null");
        await JiraClient.UpdateWorklog(worklog, description, started, timeSpentSeconds);
    }

    private class CsvRow
    {
        public DateOnly Date { get; set; }
        public TimeSpan Time { get; set; }
        public string IssueKey { get; set; } = "";
        public string? Description { get; set; }
    }

    private readonly record struct ImportItem(CsvRow Row, JiraWorklog? ExistingWorklog);
}
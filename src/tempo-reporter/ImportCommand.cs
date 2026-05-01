using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using CsvHelper;
using tempo_reporter;
using TimeSpanParserUtil;

[Command("import", Description = "Replaces all tempo worklogs for a given day with data contained in CSV read from stdin.")]
public partial class ImportCommand : BaseTempoCommand, ICommand
{
    public static readonly HttpClient Client = new();
    private JiraClient? _jiraclient;

    [CommandOption(
        "file",
        'f',
        Description = "Csv file to import. Defaults to std input")]
    public string? File { get; set; }

    [CommandOption(
        "jira-token",
        Description =
            "Jira api token. See https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/#authentication",
        EnvironmentVariable = "JIRA_TOKEN")]
    public required string JiraApiToken { get; set; }

    [CommandOption(
        "jira-user",
        Description =
            "Jira user name. See https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/#authentication",
        EnvironmentVariable = "JIRA_USER")]
    public required string JiraUser { get; set; }

    [CommandOption(
        "jira-domain",
        Description = "Domain name of your jira cloud instance. E.g my-jira.atlassian.net",
        EnvironmentVariable = "JIRA_DOMAIN")]
    public required string JiraDomain { get; set; }

    [CommandOption(
        "capex",
        Description = "If a CSV row's issue is linked to a Tempo account whose category type is CAPITALIZED, log the row to this work item key instead.")]
    public string? CapexTarget { get; set; }

    [CommandOption(
        "opex",
        Description = "If a CSV row's issue is linked to a Tempo account whose category type is OPERATIONAL, log the row to this work item key instead.")]
    public string? OpexTarget { get; set; }

    [CommandOption(
        "squash",
        Description = "Before logging hours, collapse rows sharing (Date, IssueKey): sum the time and join descriptions with newlines.")]
    public bool Squash { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        _jiraclient = new JiraClient(JiraDomain, JiraUser, JiraApiToken, console);

        var input = OpenInputFile(console);
        var data = ParseCsv(input, console);

        if (CapexTarget != null || OpexTarget != null)
            await RedirectByAccountCategory(data, console);

        if (Squash)
            data = SquashByDateAndIssue(data);

        var importList = await MatchImportRowsToExistingWorkItems(console, data);

        var startTimes = new Dictionary<DateOnly, TimeOnly>();
        foreach (var item in importList)
            await ImportCsvRow(item, startTimes, console);

        if (input != console.Input)
            input.Dispose();
    }

    private async Task RedirectByAccountCategory(List<CsvRow> rows, IConsole console)
    {
        Debug.Assert(_jiraclient != null, nameof(_jiraclient) + " != null");

        var distinctKeys = rows.Select(r => r.IssueKey).Distinct().ToList();
        var issueAccounts = await _jiraclient.GetIssueAccountIds(distinctKeys);

        var distinctAccountIds = issueAccounts.Values.Where(v => v != null).Select(v => v!.Value).Distinct().ToList();
        var accountTypes = new Dictionary<int, string?>();
        foreach (var accountId in distinctAccountIds)
            accountTypes[accountId] = await GetTempoAccountCategoryType(accountId);

        foreach (var row in rows)
        {
            if (!issueAccounts.TryGetValue(row.IssueKey, out var accountId) || accountId == null) continue;
            if (!accountTypes.TryGetValue(accountId.Value, out var typeName) || typeName == null) continue;

            string? target = null;
            if (string.Equals(typeName, "CAPITALIZED", StringComparison.OrdinalIgnoreCase))
                target = CapexTarget;
            else if (string.Equals(typeName, "OPERATIONAL", StringComparison.OrdinalIgnoreCase))
                target = OpexTarget;

            if (target == null || target == row.IssueKey) continue;

            console.Output.WriteLine($"Redirecting {row.IssueKey} ({typeName} account #{accountId}) to {target} on {row.Date:yyyy-MM-dd}");
            row.IssueKey = target;
        }
    }

    private static List<CsvRow> SquashByDateAndIssue(List<CsvRow> rows)
    {
        var groups = new Dictionary<(DateOnly, string), CsvRow>();
        var ordered = new List<CsvRow>(rows.Count);
        var descriptions = new Dictionary<(DateOnly, string), List<string>>();

        foreach (var row in rows)
        {
            var key = (row.Date, row.IssueKey);
            if (!groups.TryGetValue(key, out var existing))
            {
                existing = new CsvRow {Date = row.Date, IssueKey = row.IssueKey, Time = row.Time, Description = null};
                groups[key] = existing;
                descriptions[key] = new List<string>();
                ordered.Add(existing);
            }
            else
            {
                existing.Time = existing.Time.Add(row.Time);
            }

            if (!string.IsNullOrEmpty(row.Description))
                descriptions[key].Add(row.Description);
        }

        foreach (var (key, row) in groups)
        {
            var lines = descriptions[key];
            row.Description = lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        return ordered;
    }

    private async Task<List<ImportItem>> MatchImportRowsToExistingWorkItems(IConsole console, List<CsvRow> data)
    {
        Debug.Assert(_jiraclient != null, nameof(_jiraclient) + " != null");
        var issueIdMap = await _jiraclient.GetIssueIdMap(data.Select(d => d.IssueKey));
        List<ImportItem> importList = new(data.Count);
        var unmatched = data.ToList();
        await foreach (var worklog in GetWorklogs(data.Select(d => d.Date)))
        {
            var key = issueIdMap.FirstOrDefault(m => m.Id == worklog.Issue!.Id).Key;
            if (key != null)
            {
                //find a CsvRow for the same issue on the same day, and say that it matches this worklog
                //the match makes that row ineligible to match other worklogs
                var row = unmatched.FirstOrDefault(r => r.IssueKey == key && r.Date == worklog.StartDate);
                if (row == null)
                    await DeleteWorklog(worklog, console);
                else
                {
                    importList.Add(new ImportItem(row, worklog));
                    unmatched.Remove(row);
                }
            }
        }

        foreach (var row in unmatched)
            importList.Add(new ImportItem(row, null));
        importList.Sort((a, b) =>
        {
            var d = a.Row.Date.CompareTo(b.Row.Date);
            return d == 0 ? string.CompareOrdinal(a.Row.IssueKey, b.Row.IssueKey) : d;
        });
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
                    t = TimeSpan.FromHours(d);
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
        Debug.Assert(_jiraclient != null, nameof(_jiraclient) + " != null");
        if (item.ExistingWorklog == null)
            await _jiraclient.CreateWorklog(row.IssueKey, worklogDateTime, timeSpent, row.Description);
        else
            await UpdateWorklog(item, new TimeOnly(worklogDateTime.TimeOfDay.Ticks), timeSpentDescription, console);
    }

    private async Task UpdateWorklog(ImportItem item, TimeOnly startTime, string timeSpentDescription, IConsole console)
    {
        var (row, worklog) = item;
        ArgumentNullException.ThrowIfNull(worklog);

        var description = row.Description ?? $"Working on issue {row.IssueKey}";
        var timeSpentSeconds = (int)row.Time.TotalSeconds;

        if (worklog.Description == description &&
            worklog.StartDate == row.Date &&
            worklog.StartTime == startTime &&
            worklog.TimeSpentSeconds == timeSpentSeconds)
        {
            return; // nothing to do
        }

        var oldTime = TimeSpan.FromSeconds(worklog.TimeSpentSeconds).GetHoursMinutesString();
        console.Output.WriteLine($"Updating worklog for issue {row.IssueKey} on {row.Date:yyyy-MM-dd} from {oldTime} to {timeSpentDescription}");
        var request = MakeTempoRequest(HttpMethod.Put, $"worklogs/{worklog.TempoWorklogId}");
        request.Content = JsonContent.Create(new
        {
            authorAccountId = worklog.Author?.AccountId,
            description = description,
            startDate = row.Date.ToString("yyyy-MM-dd"),
            startTime = startTime.ToString("HH:mm:ss"),
            timeSpentSeconds = timeSpentSeconds,
        });
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private class CsvRow
    {
        public DateOnly Date { get; set; }
        public TimeSpan Time { get; set; }
        public string IssueKey { get; set; } = "";
        public string? Description { get; set; }
    }

    private readonly record struct ImportItem(CsvRow Row, TempoWorklogsResult.WorklogResult? ExistingWorklog);
}
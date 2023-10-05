using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using CsvHelper;
using tempo_reporter;
using TimeSpanParserUtil;

[Command("import", Description = "Replaces all tempo worklogs for a given day with data contained in CSV read from stdin.")]
public class ImportCommand : BaseTempoCommand, ICommand
{
    public static readonly HttpClient Client = new();

    [CommandOption(
        "file",
        'f',
        Description = "Csv file to import. Defaults to std input")]
    public string? File { get; set; }

    [CommandOption(
        "jira-token",
        Description =
            "Jira api token. See https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/#authentication",
        EnvironmentVariable = "JIRA_TOKEN",
        IsRequired = true)]
    public string? JiraApiToken { get; set; }

    [CommandOption(
        "jira-user",
        Description =
            "Jira user name. See https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro/#authentication",
        EnvironmentVariable = "JIRA_USER",
        IsRequired = true)]
    public string? JiraUser { get; set; }

    [CommandOption(
        "jira-domain",
        Description = "Domain name of your jira cloud instance. E.g my-jira.atlassian.net",
        EnvironmentVariable = "JIRA_DOMAIN",
        IsRequired = true)]
    public string? JiraDomain { get; set; }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var jiraBaseUri = new Uri($"https://{JiraDomain}/rest/api/2/");
        ArgumentException.ThrowIfNullOrEmpty(TempoApiToken);
        ArgumentException.ThrowIfNullOrEmpty(JiraApiToken);
        ArgumentException.ThrowIfNullOrEmpty(JiraUser);

        var input = OpenInputFile(console);
        var data = ParseCsv(input, console);

        var importList = await MatchImportRowsToExistingWorkItems(console, data, jiraBaseUri);

        var startTimes = new Dictionary<DateOnly, TimeOnly>();
        foreach (var item in importList) 
            await ImportCsvRow(item, startTimes, jiraBaseUri, console);

        if (input != console.Input)
            input.Dispose();
    }

    private async Task<List<ImportItem>> MatchImportRowsToExistingWorkItems(IConsole console, List<CsvRow> data, Uri jiraBaseUri)
    {
        var issueIdMap = await GetIssueIdMap(data, jiraBaseUri);
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
        importList.Sort((a, b) => a.Row.Date.CompareTo(b.Row.Date));
        return importList;
    }

    private async Task<List<IssueId>> GetIssueIdMap(IEnumerable<CsvRow> rows, Uri jiraBaseUri)
    {
        const int pageSize = 500;
        var keys = rows.Select(r => r.IssueKey).Distinct();
        var keysJql = $"key in ('{string.Join("', '", keys)}')";
        var request = MakeJiraRequest(
            jiraBaseUri,
            HttpMethod.Get,
            $"search?maxResults={pageSize}&fields=id,key&jql={UrlEncoder.Default.Encode(keysJql)}");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IssueSearchResult>() ??
                     throw new InvalidOperationException("Jira issue search returned a null response");
        if (result.Total > pageSize)
            throw new CommandException($"The jira search returned more than the maximum allowed results: {pageSize}");
        if (result.Issues == null)
            throw new CommandException("The jira search returned no issues");
        return result.Issues.Select(i => new IssueId(i.Key, int.Parse(i.Id))).ToList();
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

            data.Add(new CsvRow {Date = dt, Time = t, IssueKey = key, Description = desc});
        }

        if (!isValid)
            throw new CommandException("Please fix CSV data and retry the command");

        return data;
    }

    private async Task ImportCsvRow(
        ImportItem item,
        Dictionary<DateOnly, TimeOnly> startTimes,
        Uri jiraBaseUri,
        IConsole console)
    {
        var row = item.Row;
        var timeSpent = row.Time;
        if (!startTimes.TryGetValue(row.Date, out var startTime))
            startTime = new TimeOnly(8, 0);
        startTimes[row.Date] = startTime.Add(timeSpent).AddMinutes(1);
        var worklogDateTime = row.Date.ToDateTime(startTime, DateTimeKind.Local).ToUniversalTime();

        var timeSpentDescription = GetHoursMinutesString(timeSpent);
        if (item.ExistingWorklog == null)
            await CreateWorklog(row, worklogDateTime, jiraBaseUri, timeSpentDescription, console);
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

        var oldTime = GetHoursMinutesString(TimeSpan.FromSeconds(worklog.TimeSpentSeconds));
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

    private async Task CreateWorklog(CsvRow row,
        DateTime worklogDateTime,
        Uri jiraBaseUri,
        string timeSpentDescription,
        IConsole console)
    {
        console.Output.WriteLine($"Creating worklog for issue {row.IssueKey} on {row.Date:yyyy-MM-dd} for {timeSpentDescription}");

        var request = MakeJiraRequest(
            jiraBaseUri,
            HttpMethod.Post,
            $"issue/{row.IssueKey}/worklog?adjustEstimate=leave&notifyUsers=true"); 
        request.Content = JsonContent.Create(new
        {
            comment = row.Description ?? $"Working on issue {row.IssueKey}",
            started = $"{worklogDateTime:yyyy-MM-ddTHH:mm:ss.fff}+0000",
            timeSpentSeconds = (int) row.Time.TotalSeconds
        });
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage MakeJiraRequest(Uri jiraBaseUri, HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage
        {
            Method = method,
            RequestUri = new Uri(jiraBaseUri, relativeUri),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{JiraUser}:{JiraApiToken}"))),
                Accept = {new MediaTypeWithQualityHeaderValue("application/json")}
            }
        };
        return request;
    }

    private static string GetHoursMinutesString(TimeSpan timeSpent)
    {
        return Math.Truncate(timeSpent.TotalHours) > 0.0
            ? $"{Math.Truncate(timeSpent.TotalHours):0}h {timeSpent.Minutes}m"
            : $"{timeSpent.Minutes}m";
    }

    private class CsvRow
    {
        public DateOnly Date { get; set; }
        public TimeSpan Time { get; set; }
        public string IssueKey { get; set; } = "";
        public string? Description { get; set; }
    }

    private readonly record struct IssueId(string Key, int Id);
    private readonly record struct ImportItem(CsvRow Row, TempoWorklogsResult.WorklogResult? ExistingWorklog);
}
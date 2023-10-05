using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CliFx.Infrastructure;
using CsvHelper;
using TimeSpanParserUtil;

[Command("import",
    Description = "Replaces all tempo worklogs for a given day with data contained in CSV read from stdin.")]
public class ImportCommand : ICommand
{
    public static readonly HttpClient Client = new();
    private static readonly Uri TempoBaseUri = new("https://api.tempo.io/4/");

    [CommandOption(
        "file",
        'f',
        Description = "Csv file to import. Defaults to std input")]
    public string? File { get; set; }

    [CommandOption(
        "tempo-token",
        Description = "Tempo api token. See https://apidocs.tempo.io/#section/Authentication",
        EnvironmentVariable = "TEMPO_TOKEN",
        IsRequired = true)]
    public string? TempoApiToken { get; set; }

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
        var data = ParseCsv(input);

        await foreach (var worklog in GetWorklogs(data.Select(d => d.Date)))
            await DeleteWorklog(worklog, console);

        var startTimes = new Dictionary<DateOnly, TimeOnly>();
        foreach (var row in data)
            await CreateWorklog(row, startTimes, jiraBaseUri, console);

        if (input != console.Input)
            input.Dispose();
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

    private static List<CsvRow> ParseCsv(StreamReader input)
    {
        List<CsvRow> data = new();

        using (var csv = new CsvReader(input, CultureInfo.CurrentCulture))
        {
            // Register globally.
            csv.Context.TypeConverterCache.AddConverter<TimeSpan>(new PermissiveTimeSpanConverter());
            var readHeaders = true;
            var hasDescription = false;
            while (csv.Read())
            {
                if (readHeaders)
                {
                    csv.ReadHeader();
                    readHeaders = false;
                    var headers = csv.HeaderRecord ?? throw new InvalidOperationException("Missing csv headers");
                    hasDescription = headers.Any(h => h.Equals("Description", StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                var date = csv.GetField<string?>("Date");
                var time = csv.GetField<string?>("Time");
                var key = csv.GetField<string?>("IssueKey");
                var desc = hasDescription ? csv.GetField<string?>("Description") : null;
                if (string.IsNullOrEmpty(date))
                    throw new CommandException(
                        "Invalid CSV data. One or more rows is missing the Date column. Expected file to contain the " +
                        "following columns: Date, Time, IssueKey and may optionally contain a Description column.");

                if (string.IsNullOrEmpty(time))
                    throw new CommandException(
                        "Invalid CSV data. One or more rows is missing the Time column. Expected file to contain the " +
                        "following columns: Date, Time, IssueKey and may optionally contain a Description column.");

                if (string.IsNullOrEmpty(key))
                    throw new CommandException(
                        "Invalid CSV data. One or more rows is missing the IssueKey column. Expected file to contain the " +
                        "following columns: Date, Time, IssueKey and may optionally contain a Description column.");

                if (!DateOnly.TryParse(date, out var dt))
                    throw new CommandException($"Unsupported date on row {csv.CurrentIndex}: {date}");
                if (!TimeSpanParser.TryParse(time, out var t))
                    throw new CommandException($"Unsupported time on row {csv.CurrentIndex}: {time}");
                data.Add(new CsvRow {Date = dt, Time = t, IssueKey = key, Description = desc});
            }
        }

        return data;
    }

    private async IAsyncEnumerable<TempoWorklogsResult.WorklogResult> GetWorklogs(IEnumerable<DateOnly> dates)
    {
        const int pageSize = 500;
        foreach (var date in dates.Distinct())
        {
            var request = MakeTempoRequest(HttpMethod.Get, $"worklogs?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
            var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TempoWorklogsResult>() ??
                         throw new InvalidOperationException($"{request.RequestUri} returned a null response");

            if (result.Metadata!.Count > pageSize)
                throw new InvalidOperationException(
                    $"{request.RequestUri} returned {result.Metadata.Count} result, which is more than the maximum: {pageSize}");

            foreach (var r in result.Results!)
                yield return r;
        }
    }

    private async Task DeleteWorklog(TempoWorklogsResult.WorklogResult worklog, IConsole console)
    {
        var time = TimeSpan.FromSeconds(worklog.TimeSpentSeconds);
        var timeSpent = Math.Truncate(time.TotalHours) > 0.0
            ? $"{Math.Truncate(time.TotalHours):0}h {time.Minutes}m"
            : $"{time.Minutes}m";
        
        console.Output.WriteLine($"Deleting worklog {worklog.TempoWorklogId} for {timeSpent} on {worklog.StartDate} for issue {worklog.Issue?.Id}");
        var request = MakeTempoRequest(HttpMethod.Delete, $"worklogs/{worklog.TempoWorklogId}");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task CreateWorklog(
        CsvRow row,
        Dictionary<DateOnly, TimeOnly> startTimes,
        Uri jiraBaseUri,
        IConsole console)
    {
        var time = row.Time;
        var timeSpent = Math.Truncate(time.TotalHours) > 0.0
            ? $"{Math.Truncate(time.TotalHours):0}h {time.Minutes}m"
            : $"{time.Minutes}m";
        console.Output.WriteLine($"Creating worklog for {timeSpent} on {row.Date:yyyy-MM-dd} for issue {row.IssueKey}");
        
        if (!startTimes.TryGetValue(row.Date, out var startTime))
            startTime = new TimeOnly(8, 0);
        startTimes[row.Date] = startTime.Add(time).AddMinutes(1);

        var worklogDateTime = row.Date.ToDateTime(startTime, DateTimeKind.Local).ToUniversalTime();
        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri(jiraBaseUri, $"issue/{row.IssueKey}/worklog?adjustEstimate=leave&notifyUsers=true"),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{JiraUser}:{JiraApiToken}"))),
                Accept = {new MediaTypeWithQualityHeaderValue("application/json")}
            },
            Content = JsonContent.Create(new
            {
                comment = row.Description ?? $"Working on issue {row.IssueKey}",
                started = $"{worklogDateTime:yyyy-MM-ddTHH:mm:ss.fff}+0000",
                timeSpentSeconds = (int)time.TotalSeconds
            })
        };
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage MakeTempoRequest(HttpMethod httpMethod, string? relativeUri)
    {
        var request = new HttpRequestMessage
        {
            Method = httpMethod,
            RequestUri = new Uri(TempoBaseUri, relativeUri),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Bearer", TempoApiToken),
                Accept = {new MediaTypeWithQualityHeaderValue("application/json")}
            }
        };
        return request;
    }

    private class CsvRow
    {
        public DateOnly Date { get; set; }
        public TimeSpan Time { get; set; }
        public string IssueKey { get; set; } = "";
        public string? Description { get; set; }
    }
}

public class TempoWorklogsResult
{
    public WorklogMetadata? Metadata { get; set; }
    public WorklogResult[]? Results { get; set; }

    public class WorklogMetadata
    {
        public int Count { get; set; }
    }

    public class WorklogResult
    {
        public long TempoWorklogId { get; set; }
        public int TimeSpentSeconds { get; set; }
        public string? StartDate { get; set; }
        public WorklogIssue? Issue { get; set; }
    }

    public class WorklogIssue
    {
        public int Id { get; set; }
    }
}
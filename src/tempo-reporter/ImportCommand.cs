using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using CliFx.Infrastructure;
using CsvHelper;
using TimeSpanParserUtil;

[Command("import",
    Description = "Replaces all tempo worklogs for a given day with data contained in CSV read from stdin.")]
public class ImportCommand : ICommand
{
    public static readonly HttpClient Client = new();
    private static readonly Uri TempoBaseUri = new("https://api.tempo.io/4/");
    private IConsole? _console;
    private Uri? _jiraBaseUri;

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
        _console = console;
        _jiraBaseUri = new Uri($"https://{JiraDomain}/rest/api/2/");
        ArgumentException.ThrowIfNullOrEmpty(TempoApiToken);
        ArgumentException.ThrowIfNullOrEmpty(JiraApiToken);
        ArgumentException.ThrowIfNullOrEmpty(JiraUser);

        var input = string.IsNullOrEmpty(File)
            ? console.Input
            : new StreamReader(new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read));
        var data = ParseCsv(input);

        var issueIdMap = await GetIssueIdMap(data);
        await foreach (var worklog in GetWorklogs(data.Select(d => d.Date)))
            await DeleteWorklog(worklog);

        if (input != console.Input)
            input.Dispose();
    }

    private async Task DeleteWorklog(TempoWorklogsResult.WorklogResult worklog)
    {
        var time = TimeSpan.FromSeconds(worklog.TimeSpentSeconds);
        var timeSpent = Math.Truncate(time.TotalHours) > 0.0
            ? $"{Math.Truncate(time.TotalHours):0}h {time.Minutes}m"
            : $"{time.Minutes}m";
        
        _console!.Output.WriteLine($"Deleting worklog {worklog.TempoWorklogId} for {timeSpent} on {worklog.StartDate} for issue {worklog.Issue?.Id}");
        var request = MakeTempoRequest(HttpMethod.Delete, $"worklogs/{worklog.TempoWorklogId}");
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
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

    private async Task<IDictionary<string, string>> GetIssueIdMap(IEnumerable<CsvRow> rows)
    {
        const int pageSize = 500;
        var keys = rows.Select(r => r.IssueKey).Distinct();
        var keysJql = UrlEncoder.Default.Encode($"key in ({string.Join(", ", keys)}");
        Debug.Assert(_jiraBaseUri != null, nameof(_jiraBaseUri) + " != null");
        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(_jiraBaseUri, $"search?maxResults={pageSize}&fields=id,key&jql={keysJql}"),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{JiraUser}:{JiraApiToken}"))),
                Accept = {new MediaTypeWithQualityHeaderValue("application/json")}
            }
        };
        var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IssueSearchResult>() ??
                     throw new InvalidOperationException("Jira issue search returned a null response");
        if (result.Total > pageSize)
            throw new CommandException($"The jira search returned more than the maximum allowed results: {pageSize}");
        if (result.Issues == null)
            throw new CommandException("The jira search returned no issues");
        return result.Issues.ToDictionary(i => i.Key, i => i.Id);
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

    private class CsvRow
    {
        public DateOnly Date { get; set; }
        public TimeSpan Time { get; set; }
        public string IssueKey { get; set; } = "";
        public string? Description { get; set; }
    }
}

public class IssueSearchResult
{
    //https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issue-search/#api-rest-api-3-search-get
    public int Total { get; set; }
    public Issue[]? Issues { get; set; }

    public class Issue
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
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
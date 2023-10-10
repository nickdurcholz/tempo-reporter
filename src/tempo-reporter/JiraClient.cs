using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using CliFx.Exceptions;

namespace tempo_reporter;

public class JiraClient
{
    private static readonly HttpClient _httpClient = new();
    private readonly string _userName;
    private readonly string _apiKey;
    private readonly Uri _jiraBaseUri;
    private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public JiraClient(string jiraDomain, string userName, string apiKey)
    {
        _jiraBaseUri = new Uri($"https://{jiraDomain}/rest/api/2/");
        _userName = userName;
        _apiKey = apiKey;
    }
    
    public async Task<List<JiraIssueIdentifiers>> GetIssueIdMap(IEnumerable<string> keys)
    {
        const int pageSize = 500;
        var keysJql = $"key in ('{string.Join("', '", keys)}')";
        var request = MakeJiraRequest(HttpMethod.Get,
            $"search?maxResults={pageSize}&fields=id,key&jql={UrlEncoder.Default.Encode(keysJql)}");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<IssueSearchResult>() ??
                     throw new InvalidOperationException("Jira issue search returned a null response");
        if (result.Total > pageSize)
            throw new CommandException($"The jira search returned more than the maximum allowed results: {pageSize}");
        if (result.Issues == null)
            throw new CommandException("The jira search returned no issues");
        return result.Issues.Select(i => new JiraIssueIdentifiers(i.Key, int.Parse(i.Id))).ToList();
    }

    public async Task CreateWorklog(string issueKey, DateTime worklogDateTime, TimeSpan timeSpent, string? worklogDescription)
    {
        var request = MakeJiraRequest(HttpMethod.Post,
            $"issue/{issueKey}/worklog?adjustEstimate=leave&notifyUsers=true"); 
        request.Content = JsonContent.Create(new
        {
            comment = worklogDescription ?? $"Working on issue {issueKey}",
            started = $"{worklogDateTime:yyyy-MM-ddTHH:mm:ss.fff}+0000",
            timeSpentSeconds = (int)timeSpent.TotalSeconds
        });
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<JiraWorklog>> GetWorklogs(IEnumerable<DateOnly> dates)
    {
        List<JiraWorklog> result = new();
        foreach (var interval in Pack(dates))
        {
            var start = interval.start;
            var end = interval.end;
            var since = (long)(start.ToDateTime(default, UnixEpoch.Kind) - UnixEpoch).TotalMilliseconds;
            var startDateTime = start.ToDateTime(default, DateTimeKind.Local);
            var endDateTime = end.ToDateTime(default, DateTimeKind.Local);

            var request = MakeJiraRequest(HttpMethod.Get, $"worklog/updated?since={since}");
            UpdatedWorklogsResult? findWorklogsResult;
            do
            {
                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                findWorklogsResult = await response.Content.ReadFromJsonAsync<UpdatedWorklogsResult>() ??
                         throw new InvalidOperationException("Jira API did not return a value");
                var worklogIds = findWorklogsResult.Values!
                    .Select(v => (updated: UnixEpoch.AddMilliseconds(v.UpdatedTime), v))
                    .Where(x => new DateOnly(x.updated.Year, x.updated.Month, x.updated.Day) < end)
                    .Select(x => x.v.WorklogId)
                    .ToList();

                if (worklogIds.Count > 0)
                {
                    request = MakeJiraRequest(HttpMethod.Post, "worklog/list");
                    request.Content = JsonContent.Create(new
                    {
                        ids = worklogIds
                    });

                    response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();

                    var worklogResults = await response.Content.ReadFromJsonAsync<List<JiraWorklog>>() ??
                                         throw new InvalidOperationException("Jira API did not return a value");
                    result.AddRange(worklogResults.Where(w => w.StartDate.ToLocalTime() >= startDateTime &&
                                                              w.StartDate.ToLocalTime() < endDateTime));

                    throw new Exception("Need to debug here. This is way more data than should be returned");
                }
            } while (!findWorklogsResult.LastPage);
        }

        return result;
    }

    public async Task DeleteWorklog(JiraWorklog worklog)
    {
        var request = MakeJiraRequest(HttpMethod.Delete, $"issue/{worklog.IssueId}/worklog/{worklog.Id}");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private IEnumerable<(DateOnly start, DateOnly end)> Pack(IEnumerable<DateOnly> dates)
    {
        DateOnly start = default;
        foreach (var date in dates.Distinct().OrderBy(x => x))
        {
            if (start == default)
            {
                start = date;
            }
            else if (date != start.AddDays(1))
            {
                yield return (start, date);
                start = default;
            }
        }

        if (start != default)
            yield return (start, start.AddDays(1));
    }

    private HttpRequestMessage MakeJiraRequest(HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage
        {
            Method = method,
            RequestUri = new Uri(_jiraBaseUri, relativeUri),
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_userName}:{_apiKey}"))),
                Accept = {new MediaTypeWithQualityHeaderValue("application/json")}
            }
        };
        return request;
    }

    public async Task UpdateWorklog(JiraWorklog worklog, string description, DateTime started, int timeSpentSeconds)
    {
        var request = MakeJiraRequest(HttpMethod.Put, $"issue/{worklog.IssueId}/worklog/{worklog.Id}");
        request.Content = JsonContent.Create(new
        {
            comment = description,
            started = started,
            timeSpentSeconds = timeSpentSeconds
        });
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private class UpdatedWorklogsResult
    {
        public bool LastPage { get; set; }
        public string? NextPage { get; set; }
        public long Until { get; set; }
        public List<Worklog>? Values { get; set; }

        public class Worklog
        {
            public long WorklogId { get; set; }
            public long UpdatedTime { get; set; }
        }
    }
}
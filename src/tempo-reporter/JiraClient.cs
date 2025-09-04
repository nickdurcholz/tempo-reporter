using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CliFx.Exceptions;
using CliFx.Infrastructure;

namespace tempo_reporter;

public class JiraClient
{
    private static readonly HttpClient _httpClient = new();
    private readonly string _apiKey;
    private readonly IConsole _console;
    private readonly Uri _jiraBaseUri;
    private readonly string _userName;

    public JiraClient(string jiraDomain, string userName, string apiKey, IConsole console)
    {
        _jiraBaseUri = new Uri($"https://{jiraDomain}/rest/api/3/");
        _userName = userName;
        _apiKey = apiKey;
        _console = console;
    }

    public async Task<List<JiraIssueIdentifiers>> GetIssueIdMap(IEnumerable<string> keys)
    {
        const int pageSize = 500;
        var keysJql = $"key in ('{string.Join("', '", keys)}')";
        var request = MakeJiraRequest(
            _jiraBaseUri,
            HttpMethod.Get,
            $"search/jql?maxResults={pageSize}&fields=id,key&jql={UrlEncoder.Default.Encode(keysJql)}");
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

    public async Task CreateWorklog(string issueKey, DateTime worklogDateTime, TimeSpan timeSpent,
                                    string? worklogDescription)
    {
        _console.Output.WriteLine(
            "Creating worklog for issue {0} on {1:yyyy-MM-dd} for {2}",
            issueKey,
            worklogDateTime,
            timeSpent.GetHoursMinutesString());

        var uri = $"issue/{issueKey}/worklog?adjustEstimate=leave&notifyUsers=false";
        var request = MakeJiraRequest(_jiraBaseUri, HttpMethod.Post, uri);
        request.Content = JsonContent.Create(new
        {
            comment = worklogDescription ?? $"Working on issue {issueKey}",
            started = $"{worklogDateTime:yyyy-MM-ddTHH:mm:ss.fff}+0000",
            timeSpentSeconds = (int)timeSpent.TotalSeconds
        });
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = await GetJsonErrorMessage(response);
            var status = (int)response.StatusCode;
            throw new JiraException($"POST {uri} returned HTTP {status} {response.ReasonPhrase}. {errorMessage}");
        }
    }

    private async Task<string> GetJsonErrorMessage(HttpResponseMessage response)
    {
        var input = "(error reading response)";
        try
        {
            input = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(input)) return "Body: (empty)";
            var json = JsonSerializer.Deserialize<dynamic>(input);

            JsonElement errors = json?.GetProperty("errorMessages");
            var arrayLength = errors.GetArrayLength();
            if (arrayLength >= 1)
            {
                var errorMessage = errors.EnumerateArray().First().ToString();
                if (!string.IsNullOrEmpty(errorMessage)) return errorMessage;
            }
        }
        catch (Exception ex)
        {
            await _console.Output.WriteLineAsync(ex.Message);
        }

        return $"Body: {input}";
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
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_userName}:{_apiKey}"))),
                Accept = { new MediaTypeWithQualityHeaderValue("application/json") }
            }
        };
        return request;
    }
}
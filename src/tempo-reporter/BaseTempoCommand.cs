using System.Net.Http.Headers;
using System.Net.Http.Json;
using CliFx.Infrastructure;

namespace tempo_reporter;

public class BaseTempoCommand
{
    private static readonly Uri TempoBaseUri = new("https://api.tempo.io/4/");

    [CommandOption(
        "tempo-token",
        Description = "Tempo api token. See https://apidocs.tempo.io/#section/Authentication",
        EnvironmentVariable = "TEMPO_TOKEN",
        IsRequired = true)]
#pragma warning disable CliFx_OptionMustBeInsideCommand
    public string? TempoApiToken { get; set; }
#pragma warning restore CliFx_OptionMustBeInsideCommand

    protected async IAsyncEnumerable<TempoWorklogsResult.WorklogResult> GetWorklogs(IEnumerable<DateOnly> dates)
    {
        const int pageSize = 500;
        foreach (var date in dates.Distinct())
        {
            var request = MakeTempoRequest(HttpMethod.Get, $"worklogs?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
            var response = await ImportCommand.Client.SendAsync(request);
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

    protected async Task DeleteWorklog(TempoWorklogsResult.WorklogResult worklog, IConsole console)
    {
        var time = TimeSpan.FromSeconds(worklog.TimeSpentSeconds);
        var timeSpent = Math.Truncate(time.TotalHours) > 0.0
            ? $"{Math.Truncate(time.TotalHours):0}h {time.Minutes}m"
            : $"{time.Minutes}m";
        
        console.Output.WriteLine($"Deleting worklog {worklog.TempoWorklogId} for {timeSpent} on {worklog.StartDate} for issue {worklog.Issue?.Id}");
        var request = MakeTempoRequest(HttpMethod.Delete, $"worklogs/{worklog.TempoWorklogId}");
        var response = await ImportCommand.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    protected HttpRequestMessage MakeTempoRequest(HttpMethod httpMethod, string? relativeUri)
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
}
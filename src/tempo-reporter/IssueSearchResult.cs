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
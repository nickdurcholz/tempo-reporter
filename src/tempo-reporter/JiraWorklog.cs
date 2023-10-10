namespace tempo_reporter;

public class JiraWorklog
{
    private string? _started;
    public string? Comment { get; set; }

    public string? Started
    {
        get => _started;
        set
        {
            _started = value;
            StartDate = value == null ? default : DateTimeOffset.Parse(value);
        }
    }

    public DateTimeOffset StartDate {get; private set; }
    public int TimeSpentSeconds { get; set; }
    public long IssueId { get; set; }
    public long Id { get; set; }
    public AuthorInfo Author { get; set; }

    public class AuthorInfo
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
    }
}
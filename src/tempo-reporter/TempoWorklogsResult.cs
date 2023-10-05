namespace tempo_reporter;

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
        public Author? Author { get; set; }
        public long TempoWorklogId { get; set; }
        public int TimeSpentSeconds { get; set; }
        public DateOnly StartDate { get; set; }
        public TimeOnly StartTime { get; set; }
        public WorklogIssue? Issue { get; set; }
        public string? Description { get; set; }
    }

    public class WorklogIssue
    {
        public int Id { get; set; }
    }

    public class Author
    {
        public string AccountId { get; set; } = "";
    }
}
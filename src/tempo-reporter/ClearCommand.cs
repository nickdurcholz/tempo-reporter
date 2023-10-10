using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;

namespace tempo_reporter;

[Command("clear", Description = "Deletes all work logged for specific dates")]
public class ClearCommand : BaseJiraCommand, ICommand
{
    [CommandParameter(0, Description = "Dates to be cleared", IsRequired = true)]
    public DateOnly[] Date { get; set; } = Array.Empty<DateOnly>();
    
    public async ValueTask ExecuteAsync(IConsole console)
    {
        foreach (var worklog in await JiraClient.GetWorklogs(Date))
        {
            console.Output.WriteLine($"Deleting worklog for {worklog.IssueId} on {worklog.Started:yyyy-MM-dd}");
            await JiraClient.DeleteWorklog(worklog);
        }
    }
}
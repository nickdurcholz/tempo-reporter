using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;

namespace tempo_reporter;

[Command("clear", Description = "Deletes all work logged for specific dates")]
public class ClearCommand : BaseTempoCommand, ICommand
{
    [CommandParameter(0, Description = "Dates to be cleared", IsRequired = true)]
    public DateOnly[] Date { get; set; } = Array.Empty<DateOnly>();
    
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await foreach (var worklog in GetWorklogs(Date))
            await DeleteWorklog(worklog, console);
    }
}
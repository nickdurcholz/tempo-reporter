using CliFx.Attributes;

namespace tempo_reporter;

public abstract class BaseJiraCommand
{
    private JiraClient? _jiraClient;

    public JiraClient JiraClient
    {
        get
        {
            ValidateArguments();
            return _jiraClient ??= new JiraClient(JiraDomain!, JiraUser!, JiraApiToken!);
        }
    }

#pragma warning disable CliFx_OptionMustBeInsideCommand
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

#pragma warning restore CliFx_OptionMustBeInsideCommand

    protected void ValidateArguments()
    {
        ArgumentException.ThrowIfNullOrEmpty(JiraDomain);
        ArgumentException.ThrowIfNullOrEmpty(JiraUser);
        ArgumentException.ThrowIfNullOrEmpty(JiraApiToken);
    }
}
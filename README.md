# tempo-reporter

This is a cli utility that imports CSV into the Jira Tempo application. Its input is a CSV file that looks like

|Date|Time|IssueKey|Description|
|--|--|--|--|
|2023-10-01|2h13m|PRJ-1234|code review|
|2023-10-01|3h|PRJ-1235||

The Description column is optional.

When importing this file, the utility will update your tempo timecard to reflect 5h 13m on 10/1 split between PRJ-1234 and PRJ-1235. If there are worklogs for other issues on that day, they will be deleted. If there are worklogs for the two issues in the import file, they will be updated. If the worklogs don't already exist, they will be created.

## Example usage

View integrated help:

    tempo-reporter --help
    tempo-reporter import --help

Import a specific file:

    tempo-reporter import -f time.csv

Import this week's recorded time from ttime:

    ttime report week format=CsvSimple disp=HoursMinutes | ~/Add-IssueKeyColumn.ps1 | tempo-reporter import

Delete all recorded time for a Oct 1, Oct 2, and Oct 5:

    tempo-reporter clear 2023-10-01 2023-10-02 2023-10-05

## Setup

In order to use this utility you need to generate api keys for both Jira and tempo. See the following pages for instructions.

* [Manage API tokens for your Atlassian account](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/)
* [REST API documentation | Authentication](https://apidocs.tempo.io/#section/Authentication)

While the api tokens and other configuration values can be provided as command line arguments when invoking the utility, it is easier and safer to set them as environment variables. You can set the following environment variables in order to use this utility:

|Variable|Example|
|--|--|
|JIRA_DOMAIN|example.atlassian.net|
|JIRA_USER|you@example.com|
|JIRA_TOKEN|(api token obtained from jira)|
|TEMPO_TOKEN|(api token obtained from tempo)|

## Installation

This application is only distributed as source code

There is a Dockerfile that you can use to package and run the app. It is not published on docker registry.

    docker build -t tempo-reporter:latest .

Optionally, you can use the dotnet publish command:

    dotnet publish ./src/tempo-reporter -c Release -o ~/tempo-reporter

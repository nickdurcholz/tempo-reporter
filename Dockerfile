FROM mcr.microsoft.com/dotnet/sdk:7.0 as sdk

COPY src/tempo-reporter /tempo-reporter-src

RUN dotnet publish /tempo-reporter-src/tempo-reporter.csproj -o /tempo-reporter -c Release -r linux-x64 --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:7.0

COPY --from=sdk /tempo-reporter /tempo-reporter

ENTRYPOINT ["/tempo-reporter/tempo-reporter"]
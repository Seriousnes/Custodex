FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Custodex.Abstractions/ src/Custodex.Abstractions/
COPY src/Custodex.Protos/ src/Custodex.Protos/
COPY src/Custodex.Core/ src/Custodex.Core/
COPY src/Custodex.Storage.Postgres/ src/Custodex.Storage.Postgres/
COPY src/Custodex.ServiceDefaults/ src/Custodex.ServiceDefaults/
COPY src/Custodex.Service/ src/Custodex.Service/
RUN dotnet publish src/Custodex.Service/Custodex.Service.csproj -c Release -o /app/publish /p:UseAppHost=false /p:MinVerSkip=true
RUN mkdir -p /https && dotnet dev-certs https -ep /https/custodex.pfx -p custodex-dev

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /https/custodex.pfx /https/custodex.pfx
ENV ASPNETCORE_URLS=https://+:8080 \
    ASPNETCORE_Kestrel__Certificates__Default__Path=/https/custodex.pfx \
    ASPNETCORE_Kestrel__Certificates__Default__Password=custodex-dev
EXPOSE 8080
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Custodex.Service.dll"]

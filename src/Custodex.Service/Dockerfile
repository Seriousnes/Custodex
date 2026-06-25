FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .
COPY src/Custodex.Abstractions/ src/Custodex.Abstractions/
COPY src/Custodex.Core/ src/Custodex.Core/
COPY src/Custodex.Storage.Postgres/ src/Custodex.Storage.Postgres/
COPY src/Custodex.ServiceDefaults/ src/Custodex.ServiceDefaults/
COPY src/Custodex.Service/ src/Custodex.Service/
RUN dotnet publish src/Custodex.Service/Custodex.Service.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Custodex.Service.dll"]

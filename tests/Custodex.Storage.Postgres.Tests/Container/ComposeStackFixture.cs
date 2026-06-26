using System.Diagnostics;

namespace Custodex.Storage.Postgres.Tests.Container;

public sealed class ComposeStackFixture : IAsyncLifetime
{
    private readonly string _composeFile = LocateComposeFile();
    public string ServiceUrl { get; } = "https://localhost:8080";

    public HttpClient CreateClient() =>
        new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        { BaseAddress = new Uri(ServiceUrl) };

    public async Task InitializeAsync()
    {
        await RunComposeAsync("down -v --remove-orphans");

        var (ExitCode, Output) = await RunComposeAsync("up --build -d");
        if (ExitCode != 0)
        {
            var failureLogs = await RunComposeAsync("logs --no-color");
            throw new InvalidOperationException(
                $"'docker compose up --build -d' failed (exit {ExitCode}):\n{Output}\n--- logs ---\n{failureLogs.Output}");
        }

        await WaitForHealthyAsync();
    }

    public async Task DisposeAsync() => await RunComposeAsync("down -v --remove-orphans");

    private async Task WaitForHealthyAsync()
    {
        using var client = CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync("/health", cts.Token);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        var (ExitCode, Output) = await RunComposeAsync("logs --no-color custodex");
        throw new InvalidOperationException(
            $"custodex service at {ServiceUrl}/health did not become healthy within the timeout.\n{Output}");
    }

    private async Task<(int ExitCode, string Output)> RunComposeAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", $"compose -f \"{_composeFile}\" {arguments}")
        {
            WorkingDirectory = Path.GetDirectoryName(_composeFile)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start the 'docker' process; is Docker installed and on PATH?");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string LocateComposeFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Could not locate docker-compose.yml above the test output directory.");
    }
}

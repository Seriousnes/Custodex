using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

using Custodex.Service.Auth;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

using Shouldly;

namespace Custodex.Service.Tests.Auth;

[Collection("service")]
public sealed class ApiKeyAuthLoggingTests(PostgresFixture pg)
{
    private const string BadKey = "totally-wrong-not-a-real-key-value";

    [Fact]
    public async Task Check_with_unknown_key_logs_a_warning_without_the_raw_key()
    {
        var capture = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Custodex:ConnectionString", pg.ConnectionString);
            b.UseEnvironment("Development");
            b.UseAdminApiKey();
            b.ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.SetMinimumLevel(LogLevel.Warning);
                lb.AddProvider(capture);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", BadKey);

        var resp = await client.PostAsJsonAsync("/api/check", new { });

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var handlerWarnings = capture.Entries
            .Where(e => e.Category.Contains(nameof(ApiKeyAuthenticationHandler)) && e.Level == LogLevel.Warning)
            .ToArray();

        handlerWarnings.ShouldNotBeEmpty();
        handlerWarnings.ShouldAllBe(e => !e.Message.Contains(BadKey));
    }
}

internal sealed record CapturedLogEntry(string Category, LogLevel Level, string Message);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyCollection<CapturedLogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception)));
    }
}

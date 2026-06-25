using BenchmarkDotNet.Attributes;
using Custodex.Abstractions;

namespace Custodex.Benchmarks;

/// <summary>Measures single-Check latency on the canonical probe across the engine-driven, CTE, and reverse-index paths.</summary>
[MemoryDiagnoser]
public class CheckBenchmarks
{
    /// <summary>The execution path under measurement.</summary>
    public enum Path { Oracle, Cte, Index }

    /// <summary>The path the current benchmark run exercises.</summary>
    [Params(Path.Oracle, Path.Cte, Path.Index)]
    public Path ExecPath { get; set; }

    private ZooScaleFixture _fx = null!;
    private IAuthorizer _auth = null!;
    private CheckRequest _request = null!;

    /// <summary>Builds the fixture once and selects the authorizer + request for the current path.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _fx = new ZooScaleFixture();
        await _fx.InitializeAsync();
        _auth = Select(_fx, ExecPath);
        _request = new CheckRequest(_fx.Tenant, _fx.ProbeObject, _fx.Permission, _fx.ProbeSubject, _fx.Context);
    }

    private static IAuthorizer Select(ZooScaleFixture fx, Path path) => path switch
    {
        Path.Oracle => fx.Oracle,
        Path.Cte => fx.Cte,
        Path.Index => fx.Indexed,
        _ => fx.Cte
    };

    /// <summary>Runs one Check on the canonical probe.</summary>
    [Benchmark]
    public async Task<bool> Check() => (await _auth.CheckAsync(_request)).Allowed;

    /// <summary>Disposes the fixture's Postgres container.</summary>
    [GlobalCleanup]
    public async Task Cleanup() => await _fx.DisposeAsync();
}

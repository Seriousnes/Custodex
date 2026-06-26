using BenchmarkDotNet.Attributes;

using Custodex.Abstractions;

namespace Custodex.Benchmarks;

/// <summary>Measures first-page ListObjects latency on the canonical probe across the engine-driven, CTE, and reverse-index paths.</summary>
[MemoryDiagnoser]
public class ListObjectsBenchmarks
{
    /// <summary>The execution path under measurement.</summary>
    public enum Path { Oracle, Cte, Index }

    /// <summary>The path the current benchmark run exercises.</summary>
    [Params(Path.Oracle, Path.Cte, Path.Index)]
    public Path ExecPath { get; set; }

    private ZooScaleFixture _fx = null!;
    private IAuthorizer _auth = null!;
    private ListObjectsRequest _request = null!;

    /// <summary>Builds the fixture once and selects the authorizer + request for the current path.</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _fx = new ZooScaleFixture();
        await _fx.InitializeAsync();
        _auth = Select(_fx, ExecPath);
        _request = new ListObjectsRequest(_fx.Tenant, _fx.ProbeSubject, "item", ZooScaleFixture.Permission, _fx.Context, PageSize: 100);
    }

    private static IAuthorizer Select(ZooScaleFixture fx, Path path) => path switch
    {
        Path.Oracle => fx.Oracle,
        Path.Cte => fx.Cte,
        Path.Index => fx.Indexed,
        _ => fx.Cte
    };

    /// <summary>Runs one first-page ListObjects on the canonical probe and returns the page count.</summary>
    [Benchmark]
    public async Task<int> ListObjects() => (await _auth.ListObjectsAsync(_request)).ObjectIds.Count;

    /// <summary>Disposes the fixture's Postgres container.</summary>
    [GlobalCleanup]
    public async Task Cleanup() => await _fx.DisposeAsync();
}

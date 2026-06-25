using BenchmarkDotNet.Running;

namespace Custodex.Benchmarks;

/// <summary>Entry point dispatching to the benchmark classes via BenchmarkDotNet's switcher.</summary>
public static class Program
{
    /// <summary>Runs the benchmark selected by the command-line arguments.</summary>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

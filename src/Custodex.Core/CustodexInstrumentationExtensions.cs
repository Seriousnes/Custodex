using Custodex.Abstractions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Custodex.Core;

/// <summary>
/// Extension methods that wire Custodex's <c>"Custodex"</c> <see cref="CustodexDiagnostics.ActivitySource"/>
/// and <see cref="CustodexDiagnostics.Meter"/> into an OpenTelemetry pipeline. Chain these inside
/// <c>AddOpenTelemetry().WithTracing(...)</c> or <c>.WithMetrics(...)</c>.
/// </summary>
public static class CustodexInstrumentationExtensions
{
    /// <summary>
    /// Subscribes the <c>"Custodex"</c> <see cref="System.Diagnostics.ActivitySource"/> so that
    /// activities emitted by the engine appear in tracing exports.
    /// </summary>
    /// <param name="builder">The tracer provider builder to configure.</param>
    /// <returns>The <paramref name="builder"/>, for chaining.</returns>
    public static TracerProviderBuilder AddCustodexInstrumentation(this TracerProviderBuilder builder)
        => builder.AddSource(CustodexDiagnostics.Name);

    /// <summary>
    /// Subscribes the <c>"Custodex"</c> <see cref="System.Diagnostics.Metrics.Meter"/> so that
    /// metrics emitted by the engine appear in metrics exports.
    /// </summary>
    /// <param name="builder">The meter provider builder to configure.</param>
    /// <returns>The <paramref name="builder"/>, for chaining.</returns>
    public static MeterProviderBuilder AddCustodexInstrumentation(this MeterProviderBuilder builder)
        => builder.AddMeter(CustodexDiagnostics.Name);
}

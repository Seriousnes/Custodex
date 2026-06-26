using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace Custodex.Core;

/// <summary>
/// Fluent registration surface for Custodex. Provider packages extend this with methods such as
/// <c>UsePostgres</c>. Chain from <see cref="CustodexServiceCollectionExtensions.AddCustodex"/>.
/// </summary>
/// <remarks>Initialises the builder over the given service collection.</remarks>
public sealed class CustodexBuilder(IServiceCollection services)
{

    /// <summary>The service collection this builder was created from.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// The schema to validate and activate at startup, captured by <see cref="UseSchema(Schema)"/>
    /// or <see cref="UseSchema(SchemaBuilder)"/>. <see langword="null"/> when no startup schema is configured.
    /// </summary>
    public Schema? StartupSchema { get; private set; }

    /// <summary>
    /// Captures <paramref name="schema"/> on <see cref="StartupSchema"/> so the consuming host can
    /// validate and activate it via <see cref="ISchemaManager.SetActiveSchemaAsync"/>. The DI
    /// registration itself does not validate or activate the schema; the host reads
    /// <see cref="StartupSchema"/> and calls the manager at an appropriate point in the startup
    /// sequence.
    /// </summary>
    /// <param name="schema">The schema to capture for host-driven activation.</param>
    /// <returns>This builder, for chaining.</returns>
    public CustodexBuilder UseSchema(Schema schema)
    {
        StartupSchema = schema;
        return this;
    }

    /// <summary>
    /// Builds the schema from <paramref name="builder"/> and captures it on
    /// <see cref="StartupSchema"/> so the consuming host can validate and activate it via
    /// <see cref="ISchemaManager.SetActiveSchemaAsync"/>. The DI registration itself does not
    /// validate or activate the schema.
    /// </summary>
    /// <param name="builder">The schema builder whose result is captured for host-driven activation.</param>
    /// <returns>This builder, for chaining.</returns>
    public CustodexBuilder UseSchema(SchemaBuilder builder) => UseSchema(builder.Build());
}

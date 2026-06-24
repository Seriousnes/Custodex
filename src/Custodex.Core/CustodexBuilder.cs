using Custodex.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Custodex.Core;

/// <summary>
/// Fluent registration surface for Custodex. Provider packages extend this with methods such as
/// <c>UsePostgres</c>. Chain from <see cref="CustodexServiceCollectionExtensions.AddCustodex"/>.
/// </summary>
public sealed class CustodexBuilder
{
    /// <summary>Initialises the builder over the given service collection.</summary>
    public CustodexBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection this builder was created from.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// The schema to validate and activate at startup, captured by <see cref="UseSchema(Schema)"/>
    /// or <see cref="UseSchema(SchemaBuilder)"/>. <see langword="null"/> when no startup schema is configured.
    /// </summary>
    public Schema? StartupSchema { get; private set; }

    /// <summary>Registers a pre-built <see cref="Schema"/> for validation and activation at startup.</summary>
    /// <param name="schema">The schema to activate.</param>
    /// <returns>This builder, for chaining.</returns>
    public CustodexBuilder UseSchema(Schema schema)
    {
        StartupSchema = schema;
        return this;
    }

    /// <summary>
    /// Builds the schema from <paramref name="builder"/> and registers it for activation at startup.
    /// </summary>
    /// <param name="builder">The schema builder to build and activate.</param>
    /// <returns>This builder, for chaining.</returns>
    public CustodexBuilder UseSchema(SchemaBuilder builder) => UseSchema(builder.Build());
}

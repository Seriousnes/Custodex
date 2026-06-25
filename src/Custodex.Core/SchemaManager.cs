using Custodex.Abstractions;
using Custodex.Core.Validation;

namespace Custodex.Core;

/// <summary>Validates and activates a store's schema, and reads the schema currently active.</summary>
public sealed class SchemaManager(ISchemaStore store, IUnitOfWorkFactory uowFactory) : ISchemaManager
{
    private readonly ISchemaStore _store = store;
    private readonly IUnitOfWorkFactory _uowFactory = uowFactory;

    /// <inheritdoc/>
    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    /// <inheritdoc/>
    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var result = SchemaValidator.Validate(schema);
        if (!result.IsValid)
            throw new SchemaValidationException(result.Errors);

        await using var uow = await _uowFactory.BeginAsync(ct);
        await _store.SetActiveAsync(store, schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default) =>
        _store.GetActiveAsync(store, ct);
}

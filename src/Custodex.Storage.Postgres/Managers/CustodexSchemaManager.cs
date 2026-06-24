using Custodex.Abstractions;
using Custodex.Core.Validation;

namespace Custodex.Storage.Postgres.Managers;

/// <summary>
/// Concrete <see cref="ISchemaManager"/> backed by Postgres. Validates the schema before any
/// database write; throws <see cref="SchemaValidationException"/> when validation fails so the
/// caller can surface the errors without leaving a partial write. A valid schema activates and
/// its change log entry and epoch bump commit atomically in one owned unit of work.
/// </summary>
public sealed class CustodexSchemaManager(
    NpgsqlUnitOfWorkFactory uowFactory,
    NpgsqlSchemaStore schemas,
    AuditedWritePath audited) : ISchemaManager
{
    /// <inheritdoc />
    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    /// <inheritdoc />
    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var validation = SchemaValidator.Validate(schema);
        if (!validation.IsValid)
            throw new SchemaValidationException(validation.Errors);

        var tenant = new TenantContext(store, store);
        await using var uow = await uowFactory.BeginAsync(ct);
        await audited.SetSchemaAsync(store, tenant, actor: "schema-author", schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc />
    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default)
        => schemas.GetActiveAsync(store, ct);
}

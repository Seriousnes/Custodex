using Custodex.Abstractions;
using Custodex.Core.Validation;

using Dapper;

namespace Custodex.Storage.Sqlite.Managers;

/// <summary>
/// Concrete <see cref="ISchemaManager"/> backed by SQLite. Validates the schema before any database
/// write; throws <see cref="SchemaValidationException"/> when validation fails so the caller can
/// surface the errors without leaving a partial write. A valid schema activates and its change log
/// entry and epoch bump commit atomically in one owned unit of work. The bookkeeping store and tenant
/// rows are upserted in the same transaction so schema activation succeeds on the natural first-use
/// path without requiring a prior <c>CreateTenantAsync</c> call.
/// </summary>
public sealed class CustodexSchemaManager(
    SqliteUnitOfWorkFactory uowFactory,
    SqliteSchemaStore schemas,
    AuditedWritePath audited) : ISchemaManager
{
    /// <inheritdoc />
    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    /// <inheritdoc />
    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        Validate(schema);
        await using var uow = await uowFactory.BeginAsync(ct);
        await ActivateAsync(store, schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetActiveSchemaAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        Validate(schema);
        await ActivateAsync(store, schema, uow, ct);
    }

    private static void Validate(Schema schema)
    {
        var validation = SchemaValidator.Validate(schema);
        if (!validation.IsValid)
            throw new SchemaValidationException(validation.Errors);
    }

    private async Task ActivateAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct)
    {
        var tenant = new TenantContext(store, store);
        var w = SqliteUnitOfWork.From(uow);
        await w.Connection.ExecuteAsync(
            "INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = store }, w.Transaction);
        await w.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @s) ON CONFLICT DO NOTHING",
            new { s = store }, w.Transaction);
        await audited.SetSchemaAsync(store, tenant, actor: "schema-author", schema, uow, ct);
    }

    /// <inheritdoc />
    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default)
        => schemas.GetActiveAsync(store, ct);
}

using Relkit.Abstractions;
using Relkit.Core.Validation;

namespace Relkit.Core;

public sealed class SchemaManager(ISchemaStore store, IUnitOfWorkFactory uowFactory) : ISchemaManager
{
    private readonly ISchemaStore _store = store;
    private readonly IUnitOfWorkFactory _uowFactory = uowFactory;

    public SchemaValidationResult ValidateSchema(Schema schema) => SchemaValidator.Validate(schema);

    public async Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default)
    {
        var result = SchemaValidator.Validate(schema);
        if (!result.IsValid)
            throw new SchemaValidationException(result.Errors);

        await using var uow = await _uowFactory.BeginAsync(ct);
        await _store.SetActiveAsync(store, schema, uow, ct);
        await uow.CommitAsync(ct);
    }

    public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default) =>
        _store.GetActiveAsync(store, ct);
}

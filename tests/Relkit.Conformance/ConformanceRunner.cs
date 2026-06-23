using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Core.Validation;
using Relkit.Storage.InMemory;
using Shouldly;

namespace Relkit.Conformance;

public static class ConformanceRunner
{
    private static readonly TenantContext Tenant = new("conformance", "t");

    public static Task<CheckResult> RunAsync(ConformanceCase c, CancellationToken ct = default)
        => RunAsync(c, new NullConditionEvaluator(), ct);

    public static async Task<CheckResult> RunAsync(
        ConformanceCase c, IConditionEvaluator conditions, CancellationToken ct = default)
    {
        // Validate the schema through the real m0/03 validator so malformed cases fail loudly.
        // SchemaValidator is a static class (m0/03); EmptyConditionBody passes validation there
        // (body type-checking is deferred to m0/06), so the 12.6 within_hours case validates.
        var validation = SchemaValidator.Validate(c.Schema);
        if (!validation.IsValid)
            throw new SchemaValidationException(validation.Errors);

        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();

        await schemaStore.SetActiveAsync(Tenant.Store, c.Schema, uow, ct);
        if (c.Tuples.Count > 0)
            await relations.WriteAsync(Tenant, c.Tuples, Array.Empty<RelationTuple>(), uow, ct);
        foreach (var seed in c.Attributes)
            await attributes.SetAsync(Tenant, seed.Object, seed.Attributes, uow, ct);
        await uow.CommitAsync(ct);

        var authorizer = new EngineDrivenAuthorizer(schemaStore, relations, attributes, conditions);

        var request = new CheckRequest(
            Tenant, c.Object, c.Permission, c.Subject,
            new RequestContext(c.Now, c.Subject, c.Context));
        return await authorizer.CheckAsync(request, ct);
    }

    public static async Task AssertAsync(ConformanceCase c)
    {
        var result = await RunAsync(c);
        result.Allowed.ShouldBe(c.Expected,
            $"Conformance case '{c.Name}': expected Allowed={c.Expected} for " +
            $"{c.Subject} on {c.Object}#{c.Permission}, got {result.Allowed}.");
    }

    public static async Task AssertAsync(ConformanceCase c, IConditionEvaluator conditions)
    {
        var result = await RunAsync(c, conditions);
        result.Allowed.ShouldBe(c.Expected,
            $"Conformance case '{c.Name}': expected Allowed={c.Expected} for " +
            $"{c.Subject} on {c.Object}#{c.Permission}, got {result.Allowed}.");
    }
}

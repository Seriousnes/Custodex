using CsCheck;
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Conformance.Properties;

public class AlgebraInvariantTests
{
    private static readonly TenantContext T = AlgebraGenerators.Tenant;

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, IReadOnlyList<RelationTuple> tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Count > 0) await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest View(string user) => new(
        T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()));

    [Fact]
    public async Task SelfExclusion_always_denies()
    {
        await Check.SampleAsync(AlgebraGenerators.UserId, async user =>
        {
            var auth = await NewAsync(AlgebraGenerators.SelfExcludeSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", user)) });
            var r = await auth.CheckAsync(View(user));
            return r.Allowed == false;   // a - a = deny, even when viewer holds
        });
    }

    [Fact]
    public async Task Wildcard_grants_every_user()
    {
        await Check.SampleAsync(AlgebraGenerators.UserId, async user =>
        {
            var auth = await NewAsync(AlgebraGenerators.MonotoneSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")) });
            var r = await auth.CheckAsync(View(user));
            return r.Allowed == true;
        });
    }

    [Fact]
    public async Task Nested_group_chain_is_reachable()
    {
        await Check.SampleAsync(Gen.Select(AlgebraGenerators.ChainLength, AlgebraGenerators.UserId),
            async (length, user) =>
            {
                var tuples = AlgebraGenerators.NestedChainTuples(length, user);
                var auth = await NewAsync(AlgebraGenerators.MonotoneSchema(), tuples);
                var r = await auth.CheckAsync(View(user));
                return r.Allowed == true;   // user at the bottom of the chain reaches the top grant
            });
    }

    [Fact]
    public async Task Union_is_monotone_over_exclusion_free_permission()
    {
        // Granting viewer to a subject can only flip deny->allow on an exclusion-free permission.
        await Check.SampleAsync(AlgebraGenerators.UserId, async user =>
        {
            var without = await NewAsync(AlgebraGenerators.MonotoneSchema(), Array.Empty<RelationTuple>());
            var before = (await without.CheckAsync(View(user))).Allowed;

            var with = await NewAsync(AlgebraGenerators.MonotoneSchema(),
                new[] { new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", user)) });
            var after = (await with.CheckAsync(View(user))).Allowed;

            // monotone: before implies after, and after must be true once granted.
            return (!before || after) && after;
        });
    }
}

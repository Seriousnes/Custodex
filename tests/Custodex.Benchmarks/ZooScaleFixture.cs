using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Index;

using Dapper;

using Npgsql;

using Testcontainers.PostgreSql;

namespace Custodex.Benchmarks;

/// <summary>
/// Seeds one Postgres container with a representative dataset (~50 users, ~2k objects, ~20k tuples)
/// and builds every authorizer variant over it: the in-memory engine-driven oracle, the recursive-CTE
/// path, and the reverse-index-backed path. The seed is deterministic for stable measurements.
/// </summary>
public sealed class ZooScaleFixture
{
    private const string Store = "store-a";

    /// <summary>The tenant the dataset is seeded under.</summary>
    public TenantContext Tenant { get; } = new(Store, "main");

    private PostgreSqlContainer _container = null!;

    /// <summary>The connection string of the seeded Postgres container.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>The in-memory engine-driven oracle authorizer.</summary>
    public EngineDrivenAuthorizer Oracle { get; private set; } = null!;

    /// <summary>The recursive-CTE Postgres authorizer.</summary>
    public NpgsqlCteAuthorizer Cte { get; private set; } = null!;

    /// <summary>The reverse-index-backed authorizer.</summary>
    public IndexedAuthorizer Indexed { get; private set; } = null!;

    /// <summary>The probe subject used by every benchmark.</summary>
    public SubjectRef ProbeSubject { get; } = new("user", "user-1");

    /// <summary>The probe object used by the Check benchmark.</summary>
    public EntityRef ProbeObject { get; } = new("item", "item-1");

    /// <summary>The probe permission.</summary>
    public static string Permission => "edit";

    /// <summary>A fresh request context for the probe subject.</summary>
    public RequestContext Context => new(DateTimeOffset.UnixEpoch, ProbeSubject, new Dictionary<string, object?>());

    private static Schema BuildSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("area", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Type("kind", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Type("item", t => t
            .Relation("handler", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("area", s => s.Type("area"))
            .Relation("kind", s => s.Type("kind"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p
                .Relation("handler")
                .Arrow("area", "edit")
                .Arrow("kind", "edit")
                .Exclude(x => x.Relation("blocked"))))
        .Condition("is_creator", c => { })
        .Build();

    private static List<RelationTuple> BuildTuples()
    {
        var rng = new Random(20260623);
        var tuples = new List<RelationTuple>();

        var roles = Enumerable.Range(0, 5).Select(i => $"role-{i}").ToArray();
        var teams = Enumerable.Range(0, 3).Select(i => $"team-{i}").ToArray();
        foreach (var team in teams)
            tuples.Add(new RelationTuple(new EntityRef("group", roles[rng.Next(roles.Length)]), "member",
                new SubjectRef("group", team, "member")));

        for (var i = 0; i < 50; i++)
        {
            var user = $"user-{i}";
            var g1 = rng.Next(2) == 0 ? roles[rng.Next(roles.Length)] : teams[rng.Next(teams.Length)];
            tuples.Add(new RelationTuple(new EntityRef("group", g1), "member", new SubjectRef("user", user)));
            if (rng.Next(2) == 0)
                tuples.Add(new RelationTuple(new EntityRef("group", teams[rng.Next(teams.Length)]), "member",
                    new SubjectRef("user", user)));
        }

        for (var i = 0; i < 20; i++)
        {
            tuples.Add(new RelationTuple(new EntityRef("area", $"area-{i}"), "editor",
                new SubjectRef("group", roles[i % roles.Length], "member")));
            tuples.Add(new RelationTuple(new EntityRef("kind", $"kind-{i}"), "editor",
                new SubjectRef("group", roles[i % roles.Length], "member")));
        }

        for (var i = 0; i < 2000; i++)
        {
            var item = new EntityRef("item", $"item-{i}");
            tuples.Add(new RelationTuple(item, "area", new SubjectRef("area", $"area-{i % 20}")));
            tuples.Add(new RelationTuple(item, "kind", new SubjectRef("kind", $"kind-{i % 20}")));

            switch (i % 10)
            {
                case 0:
                    tuples.Add(new RelationTuple(item, "handler", new SubjectRef("user", "*")));
                    break;
                case 1:
                    tuples.Add(new RelationTuple(item, "handler", new SubjectRef("group", roles[i % roles.Length], "member"),
                        new ConditionRef("is_creator", new Dictionary<string, object?>())));
                    break;
                default:
                    tuples.Add(new RelationTuple(item, "handler", new SubjectRef("group", roles[i % roles.Length], "member")));
                    break;
            }

            if (i % 25 == 0)
                tuples.Add(new RelationTuple(item, "blocked", new SubjectRef("user", $"user-{i % 50}")));
        }

        return tuples;
    }

    /// <summary>Starts the container, applies migrations, seeds the dataset, rebuilds the reverse index, and constructs the three authorizer variants.</summary>
    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_INITDB_ARGS",
                "--locale-provider=icu --icu-locale=en-US --encoding=UTF8 --locale=C.UTF-8")
            .Build();
        await _container.StartAsync();
        ConnectionString =
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { SearchPath = "custodex" }.ToString();

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await MigrationRunner.ApplyAsync(conn);
        }

        var schema = BuildSchema();
        var tuples = BuildTuples();
        var conditions = new NullConditionEvaluator();

        var factory = new NpgsqlUnitOfWorkFactory(ConnectionString);
        var pgSchema = new NpgsqlSchemaStore(ConnectionString);
        var pgRelations = new NpgsqlRelationStore(ConnectionString);
        var pgAttributes = new NpgsqlAttributeStore(ConnectionString);

        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = Store });
            await conn.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = Store, t = Tenant.Tenant });
        }
        await using (var u = await factory.BeginAsync())
        {
            await pgSchema.SetActiveAsync(Store, schema, u);
            await pgRelations.WriteAsync(Tenant, tuples, [], u);
            await u.CommitAsync();
        }

        var index = new NpgsqlIndexStore(ConnectionString);
        var rebuilder = new ReverseIndexRebuilder(ConnectionString, pgSchema, pgRelations, pgAttributes, index);
        await using (var u = await factory.BeginAsync())
        {
            await rebuilder.RebuildAsync(Tenant, u);
            await u.CommitAsync();
        }

        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new NoOpUnitOfWork();
        await memSchema.SetActiveAsync(Store, schema, memUow);
        await memRelations.WriteAsync(Tenant, tuples, [], memUow);
        await memUow.CommitAsync();

        Oracle = new EngineDrivenAuthorizer(memSchema, memRelations, memAttributes, conditions);
        Cte = new NpgsqlCteAuthorizer(ConnectionString, pgSchema, pgAttributes, conditions);
        Indexed = new IndexedAuthorizer(Cte, index, pgSchema);

        await SanityCheckAsync();
    }

    private async Task SanityCheckAsync()
    {
        var req = new CheckRequest(Tenant, ProbeObject, Permission, ProbeSubject, Context);
        var a = (await Oracle.CheckAsync(req)).Allowed;
        var b = (await Cte.CheckAsync(req)).Allowed;
        var c = (await Indexed.CheckAsync(req)).Allowed;
        if (a != b || b != c)
            throw new InvalidOperationException($"Fixture mismatch: oracle={a}, cte={b}, indexed={c}.");
    }

    /// <summary>Disposes the Postgres container.</summary>
    public async Task DisposeAsync() => await _container.DisposeAsync();
}

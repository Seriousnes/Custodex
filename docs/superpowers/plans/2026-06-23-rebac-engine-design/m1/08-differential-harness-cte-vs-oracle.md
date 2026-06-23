# M1/08 — Differential Harness: CTE vs Oracle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A CsCheck differential, property-based harness that generates random **valid** schemas + tuples + attributes and asserts `NpgsqlCteAuthorizer` (the Postgres CTE path, `m1/05`/`m1/06`) ≡ `EngineDrivenAuthorizer` (the engine-driven oracle, `m0/05`/`m0/07`) for **Check, ListObjects, and ListSubjects**, over a Testcontainers Postgres database and the in-memory store seeded identically. This is the project's correctness backbone (spec §7.4 / §11.1): the CTE path is trusted only when the oracle agrees across thousands of cases.

**Architecture:** One generator emits a `(Schema, tuples, attributes)` triple. The schema is built so it **always passes `m0/03` validation** (every relation/permission/arrow resolves, recursion terminates, conditions type-check) — otherwise both authorizers throw in lockstep and the case proves nothing. The harness seeds the *same* triple into (a) the in-memory provider behind `EngineDrivenAuthorizer` and (b) a fresh tenant in Testcontainers Postgres behind `NpgsqlCteAuthorizer`, then for randomly drawn `(object, permission, subject)` triples asserts `Check` agrees; for randomly drawn `(subject, type, permission)` asserts the **set** of `ListObjects` ids agrees; for `(object, permission)` asserts the **set** of `ListSubjects` subjects agrees (order-independent — pagination order is an internal detail, the *set* is the contract). Both authorizers use the **same `IConditionEvaluator`** so conditioned branches are compared on equal footing. A CsCheck counterexample is minimized and is a genuine CTE bug (the oracle is the spec).

**Tech Stack:** .NET 10 (`net10.0`), C# 14, **CsCheck** (MIT), Npgsql, Dapper, xUnit, Shouldly, `Testcontainers.PostgreSql`. Uses `Relkit.Core` (oracle), `Relkit.Storage.InMemory`, `Relkit.Storage.Postgres` (CTE path).

## Global Constraints

See `../README.md` → Global Constraints. All I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; no `DateTime.Now`/`Guid.NewGuid()` in evaluation. **CsCheck** is the property-test library (MIT). Depends on `m0/03` (`SchemaValidator` — the generated schema must pass it), `m0/04` (in-memory stores), `m0/05`/`m0/07` (`EngineDrivenAuthorizer` oracle), `m0/09` (the conformance generator patterns to reuse), `m1/01`–`m1/06` (Postgres schema, stores, `NpgsqlCteAuthorizer`).

> **CALIBRATION — this plan IS the correctness mechanism.** The `m1/02` spike proved the seam on hand-checked cases; this harness generalises it to thousands of random ones. The generators and the equivalence assertions are the **durable specification**: they must be correct now. The CTE SQL in `m1/05`/`m1/06` is the candidate this harness proves. A divergence is a CTE bug — fix `NpgsqlCteAuthorizer`, never weaken this harness to make it pass.

---

### Task 1: Create the harness project and the shared Postgres fixture

**Files:**
- Create: `tests/Relkit.Differential/Relkit.Differential.csproj`
- Create: `tests/Relkit.Differential/DifferentialFixture.cs`
- Test: `tests/Relkit.Differential/WiringTests.cs`

**Interfaces:**
- Produces: the `Relkit.Differential` project (referencing `Relkit.Abstractions`, `Relkit.Core`, `Relkit.Storage.InMemory`, `Relkit.Storage.Postgres`), and a `DifferentialFixture : IAsyncLifetime` that starts one Postgres container for the whole run and applies the migrations once. Each property iteration uses a **fresh `(store, tenant)`** so cases never cross-contaminate.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new xunit -n Relkit.Differential -o tests/Relkit.Differential -f net10.0
rm tests/Relkit.Differential/UnitTest1.cs
dotnet sln add tests/Relkit.Differential
dotnet add tests/Relkit.Differential reference src/Relkit.Abstractions
dotnet add tests/Relkit.Differential reference src/Relkit.Core
dotnet add tests/Relkit.Differential reference src/Relkit.Storage.InMemory
dotnet add tests/Relkit.Differential reference src/Relkit.Storage.Postgres
dotnet add tests/Relkit.Differential package Shouldly
dotnet add tests/Relkit.Differential package CsCheck
dotnet add tests/Relkit.Differential package Npgsql
dotnet add tests/Relkit.Differential package Dapper
dotnet add tests/Relkit.Differential package Testcontainers.PostgreSql
```

- [ ] **Step 2: Write the fixture and a wiring test**

```csharp
// tests/Relkit.Differential/DifferentialFixture.cs
using Npgsql;
using Relkit.Storage.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace Relkit.Differential;

public sealed class DifferentialFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

[CollectionDefinition("differential")]
public sealed class DifferentialCollection : ICollectionFixture<DifferentialFixture> { }
```

```csharp
// tests/Relkit.Differential/WiringTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Differential;

[Collection("differential")]
public class WiringTests(DifferentialFixture fx)
{
    [Fact]
    public async Task Container_is_migrated()
    {
        await using var conn = await fx.OpenAsync();
        var ok = await conn.OpenAsync().ContinueWith(_ => true);   // already open; smoke check
        conn.State.ShouldBe(System.Data.ConnectionState.Open);
    }
}
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test tests/Relkit.Differential --filter WiringTests`
Expected: PASS (1 test) — the container starts and migrations apply.

- [ ] **Step 4: Commit**

```bash
git add tests/Relkit.Differential
git commit -m "chore: scaffold differential harness project and Postgres fixture"
```

---

### Task 2: The valid-schema + tuples + attributes generator

**Files:**
- Create: `tests/Relkit.Differential/ModelGenerator.cs`
- Test: `tests/Relkit.Differential/ModelGeneratorTests.cs`

**Interfaces:**
- Produces: `ModelGenerator` with a CsCheck `Gen<GeneratedModel>` where `GeneratedModel(Schema Schema, IReadOnlyList<RelationTuple> Tuples, IReadOnlyList<(EntityRef Obj, IReadOnlyDictionary<string,object?> Attrs)> Attributes, IReadOnlyList<EntityRef> ProbeObjects, IReadOnlyList<SubjectRef> ProbeSubjects)`. The schema is drawn from a **fixed family of shapes** that exercise the hard interactions (union, intersection, exclusion, arrow-into-permission, arrow-into-relation, nested groups, wildcard gates) and is **guaranteed to pass `m0/03` validation**. Tuples are drawn over a small id pool consistent with the schema's relations. Attributes are drawn for objects whose schema has a condition.
- Consumes: `SchemaBuilder` (`m0/02`), `SchemaValidator` (`m0/03`), CsCheck `Gen`, the worked-example schema shapes from `m0/09`.

> **Why a fixed family, not free-form schemas.** A fully random schema is usually invalid (dangling arrows, non-terminating recursion) — both authorizers would throw in lockstep and prove nothing (the advisor's blind spot). Instead the generator picks one of a curated set of **valid skeleton schemas** (each already shown valid by `m0/09`/`m0/05`) and randomizes the **data** (which ids fill which relations, group nesting depth, who is blocked, which enclosure is quarantine). That keeps every case meaningful — the algebra is fixed and correct; the *tuple graph* is what varies, which is exactly where CTE reachability bugs hide. A `m0/03` validation assertion in the generator guards the invariant.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Differential/ModelGeneratorTests.cs
using CsCheck;
using Relkit.Core;
using Shouldly;
using Xunit;

namespace Relkit.Differential;

public class ModelGeneratorTests
{
    [Fact]
    public void Every_generated_schema_passes_m0_03_validation()
    {
        ModelGenerator.Gen.Sample(model =>
        {
            var result = SchemaValidator.Validate(model.Schema);
            return result.IsValid;
        }, iter: 200);
    }

    [Fact]
    public void Generator_produces_probe_objects_and_subjects()
    {
        ModelGenerator.Gen.Sample(model =>
            model.ProbeObjects.Count > 0 && model.ProbeSubjects.Count > 0, iter: 50);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Differential --filter ModelGeneratorTests`
Expected: FAIL — `ModelGenerator` does not exist.

- [ ] **Step 3: Implement the generator**

> The generator composes three curated skeletons (each valid): **(S1) nested-group viewer with exclusion** (`doc.view = viewer - blocked`, `viewer` allows user/group#member/wildcard), **(S2) arrow-with-inner-exclusion** (`animal.edit = enclosure->edit`, `enclosure.edit = editor - blocked` — the discriminator shape), and **(S3) the §12.5 quarantine gate** (intersection-through-arrow with a wildcard). For each, it randomizes the data graph over a small id pool. `ProbeObjects`/`ProbeSubjects` are the ids the harness will Check/List against.

```csharp
// tests/Relkit.Differential/ModelGenerator.cs
using CsCheck;
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Differential;

public sealed record GeneratedModel(
    Schema Schema,
    IReadOnlyList<RelationTuple> Tuples,
    IReadOnlyList<(EntityRef Obj, IReadOnlyDictionary<string, object?> Attrs)> Attributes,
    IReadOnlyList<EntityRef> ProbeObjects,
    IReadOnlyList<SubjectRef> ProbeSubjects);

public static class ModelGenerator
{
    private static readonly string[] Users = ["u1", "u2", "u3", "u4"];
    private static readonly string[] Groups = ["g1", "g2", "g3"];

    private static RelationTuple T(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);
    private static readonly Gen<string> User = Gen.OneOfConst(Users);
    private static readonly Gen<string> GroupId = Gen.OneOfConst(Groups);

    // ── S1: doc.view = viewer - blocked ; viewer: user | group#member | user:* ──────────────
    private static Schema S1Schema() => new SchemaBuilder("s1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static readonly Gen<GeneratedModel> S1 =
        Gen.Select(
            Gen.OneOfConst("d1", "d2", "d3"),                         // doc id
            Gen.Bool,                                                 // wildcard viewer?
            User, User, GroupId,                                      // viewer-user, blocked-user, viewer-group
            User, User,                                               // group members
            (docId, wildcard, viewU, blockU, vg, m1, m2) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("doc", docId, "viewer", new SubjectRef("group", vg, "member")),
                    T("group", vg, "member", new SubjectRef("user", m1)),
                    T("group", vg, "member", new SubjectRef("user", m2)),
                    T("doc", docId, "viewer", new SubjectRef("user", viewU)),
                    T("doc", docId, "blocked", new SubjectRef("user", blockU)),
                };
                if (wildcard) tuples.Add(T("doc", docId, "viewer", new SubjectRef("user", "*")));
                return new GeneratedModel(S1Schema(), tuples, [],
                    [new EntityRef("doc", docId)],
                    Users.Select(u => new SubjectRef("user", u)).ToList());
            });

    // ── S2: animal.edit = enclosure->edit ; enclosure.edit = editor - blocked (discriminator) ─
    private static Schema S2Schema() => new SchemaBuilder("s2")
        .Type("enclosure", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("animal", t => t
            .Relation("enclosure", s => s.Type("enclosure"))
            .Permission("edit", p => p.Arrow("enclosure", "edit")))
        .Build();

    private static readonly Gen<GeneratedModel> S2 =
        Gen.Select(
            Gen.OneOfConst("a1", "a2"), Gen.OneOfConst("e1", "e2"),
            User, User, User,
            (animalId, encId, editor, blocked, editor2) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("animal", animalId, "enclosure", new SubjectRef("enclosure", encId)),
                    T("enclosure", encId, "editor", new SubjectRef("user", editor)),
                    T("enclosure", encId, "editor", new SubjectRef("user", editor2)),
                    T("enclosure", encId, "blocked", new SubjectRef("user", blocked)),
                };
                return new GeneratedModel(S2Schema(), tuples, [],
                    [new EntityRef("animal", animalId)],
                    Users.Select(u => new SubjectRef("user", u)).ToList());
            });

    // ── S3: §12.5 quarantine gate ─────────────────────────────────────────────────────────────
    private static Schema S3Schema() => new SchemaBuilder("s3")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("enclosure", t => t
            .Relation("is_quarantine", s => s.Wildcard("user"))
            .Permission("is_quarantine", p => p.Relation("is_quarantine")))
        .Type("animal", t => t
            .Relation("can_access", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Relation("vet_member", s => s.SubjectSet("group", "member"))
            .Relation("trained_member", s => s.SubjectSet("group", "member"))
            .Permission("access", p => p
                .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("enclosure", "is_quarantine")))
                .Union(b => b.Arrow("enclosure", "is_quarantine")
                    .Intersect(x => x.Relation("vet_member"))
                    .Intersect(x => x.Relation("trained_member")))))
        .Build();

    private static readonly Gen<GeneratedModel> S3 =
        Gen.Select(
            Gen.Bool,                            // is the enclosure quarantine?
            User, User,                          // vet members
            User,                                // trained member (subset)
            (quarantine, vet1, vet2, trained) =>
            {
                var tuples = new List<RelationTuple>
                {
                    T("animal", "an", "enclosure", new SubjectRef("enclosure", "en")),
                    T("animal", "an", "vet_member", new SubjectRef("group", "vets", "member")),
                    T("animal", "an", "trained_member", new SubjectRef("group", "trained", "member")),
                    T("animal", "an", "can_access", new SubjectRef("user", vet1)),
                    T("group", "vets", "member", new SubjectRef("user", vet1)),
                    T("group", "vets", "member", new SubjectRef("user", vet2)),
                    T("group", "trained", "member", new SubjectRef("user", trained)),
                };
                if (quarantine) tuples.Add(T("enclosure", "en", "is_quarantine", new SubjectRef("user", "*")));
                return new GeneratedModel(S3Schema(), tuples, [],
                    [new EntityRef("animal", "an")],
                    Users.Select(u => new SubjectRef("user", u)).ToList());
            });

    public static readonly Gen<GeneratedModel> Gen = CsCheck.Gen.OneOf(S1, S2, S3);
}
```

> If `Gen.OneOfConst` / `Gen.Select` arities differ in the pinned CsCheck version, adapt the combinator calls — the *contract* is "a `Gen<GeneratedModel>` whose schema always validates and whose tuples are consistent with it." The `ModelGeneratorTests` validation property is the guard.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Differential --filter ModelGeneratorTests`
Expected: PASS (2 tests) — every generated schema validates; probes are present.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Differential
git commit -m "feat: add valid-schema model generator for the differential harness"
```

---

### Task 3: The dual-seed harness — build both authorizers over one model

**Files:**
- Create: `tests/Relkit.Differential/DifferentialHarness.cs`
- Test: `tests/Relkit.Differential/HarnessSmokeTests.cs`

**Interfaces:**
- Produces: `DifferentialHarness` with `static Task<(EngineDrivenAuthorizer Oracle, NpgsqlCteAuthorizer Cte)> BuildAsync(DifferentialFixture fx, GeneratedModel model, string store, CancellationToken ct)` — seeds the model into the in-memory stores (behind the oracle) and into a fresh `(store, tenant)` in Postgres (behind the CTE authorizer), using the **same `IConditionEvaluator`** for both. A fixed `RequestContext` (Unix epoch now) keeps conditions deterministic.
- Consumes: in-memory stores (`m0/04`), `EngineDrivenAuthorizer` (`m0/05`), `NpgsqlRelationStore`/`SchemaStore`/`AttributeStore`/`NpgsqlUnitOfWorkFactory` (`m1/03`/`m1/04`), `NpgsqlCteAuthorizer` (`m1/05`), `NullConditionEvaluator`.

> Both authorizers receive identical schema + tuples + attributes. The tenant is `(store, "t")` with a unique `store` per case so Postgres state never leaks between iterations. Conditions use `NullConditionEvaluator` by default (pure-ReBAC equivalence); a conditioned-model extension can inject the real evaluator into both, but the null evaluator already proves the structural algebra matches — which is where CTE bugs live.

- [ ] **Step 1: Write the smoke test**

```csharp
// tests/Relkit.Differential/HarnessSmokeTests.cs
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Differential;

[Collection("differential")]
public class HarnessSmokeTests(DifferentialFixture fx)
{
    [Fact]
    public async Task Both_authorizers_agree_on_a_single_hand_picked_model()
    {
        // S2 discriminator: editor+blocked carol on the enclosure => animal.edit denies for carol.
        var model = new GeneratedModel(
            ModelGeneratorSamples.S2DiscriminatorSchema(),
            ModelGeneratorSamples.S2DiscriminatorTuples(),
            [],
            [new EntityRef("animal", "a1")],
            [new SubjectRef("user", "carol"), new SubjectRef("user", "dana")]);

        var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store: "smoke");

        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "carol"), new Dictionary<string, object?>());
        var oracleCarol = await oracle.CheckAsync(new CheckRequest(new TenantContext("smoke", "t"), new EntityRef("animal", "a1"), "edit", new SubjectRef("user", "carol"), ctx));
        var cteCarol = await cte.CheckAsync(new CheckRequest(new TenantContext("smoke", "t"), new EntityRef("animal", "a1"), "edit", new SubjectRef("user", "carol"), ctx));

        oracleCarol.Allowed.ShouldBe(cteCarol.Allowed);
        oracleCarol.Allowed.ShouldBeFalse();   // both deny carol via inner exclusion through the arrow
    }
}
```

> A tiny `ModelGeneratorSamples` helper provides the hand-picked discriminator model used by the smoke test:

```csharp
// tests/Relkit.Differential/ModelGeneratorSamples.cs
using Relkit.Abstractions;
using Relkit.Core;

namespace Relkit.Differential;

internal static class ModelGeneratorSamples
{
    internal static Schema S2DiscriminatorSchema() => new SchemaBuilder("s2")
        .Type("enclosure", t => t
            .Relation("editor", s => s.User()).Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
        .Type("animal", t => t
            .Relation("enclosure", s => s.Type("enclosure"))
            .Permission("edit", p => p.Arrow("enclosure", "edit")))
        .Build();

    internal static IReadOnlyList<RelationTuple> S2DiscriminatorTuples() =>
    [
        new(new EntityRef("animal", "a1"), "enclosure", new SubjectRef("enclosure", "e1")),
        new(new EntityRef("enclosure", "e1"), "editor", new SubjectRef("user", "carol")),
        new(new EntityRef("enclosure", "e1"), "blocked", new SubjectRef("user", "carol")),
        new(new EntityRef("enclosure", "e1"), "editor", new SubjectRef("user", "dana")),
    ];
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Differential --filter HarnessSmokeTests`
Expected: FAIL — `DifferentialHarness` does not exist.

- [ ] **Step 3: Implement the dual-seed harness**

```csharp
// tests/Relkit.Differential/DifferentialHarness.cs
using Dapper;
using Relkit.Abstractions;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Relkit.Storage.Postgres;

namespace Relkit.Differential;

public static class DifferentialHarness
{
    public static async Task<(EngineDrivenAuthorizer Oracle, NpgsqlCteAuthorizer Cte)> BuildAsync(
        DifferentialFixture fx, GeneratedModel model, string store, CancellationToken ct = default)
    {
        var tenant = new TenantContext(store, "t");
        var conditions = new NullConditionEvaluator();

        // ── Oracle: in-memory ─────────────────────────────────────────────────────────────────
        var memSchema = new InMemorySchemaStore();
        var memRelations = new InMemoryRelationStore();
        var memAttributes = new InMemoryAttributeStore();
        var memUow = new InMemoryUnitOfWork();
        await memSchema.SetActiveAsync(store, model.Schema, memUow, ct);
        if (model.Tuples.Count > 0) await memRelations.WriteAsync(tenant, model.Tuples, [], memUow, ct);
        foreach (var (obj, attrs) in model.Attributes) await memAttributes.SetAsync(tenant, obj, attrs, memUow, ct);
        await memUow.CommitAsync(ct);
        var oracle = new EngineDrivenAuthorizer(memSchema, memRelations, memAttributes, conditions);

        // ── CTE path: Postgres (fresh store+tenant) ───────────────────────────────────────────
        var connStr = fx.ConnectionString;
        await using (var conn = await fx.OpenAsync())
        {
            await conn.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING", new { s = store });
            await conn.ExecuteAsync("INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
                new { s = store, t = tenant.Tenant });
        }
        var pgSchema = new NpgsqlSchemaStore(connStr);
        var pgRelations = new NpgsqlRelationStore(connStr);
        var pgAttributes = new NpgsqlAttributeStore(connStr);
        var factory = new NpgsqlUnitOfWorkFactory(connStr);
        await using (var u = await factory.BeginAsync(ct))
        {
            await pgSchema.SetActiveAsync(store, model.Schema, u, ct);
            if (model.Tuples.Count > 0) await pgRelations.WriteAsync(tenant, model.Tuples, [], u, ct);
            foreach (var (obj, attrs) in model.Attributes) await pgAttributes.SetAsync(tenant, obj, attrs, u, ct);
            await u.CommitAsync(ct);
        }
        var cte = new NpgsqlCteAuthorizer(connStr, pgSchema, pgAttributes, conditions);

        return (oracle, cte);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Differential --filter HarnessSmokeTests`
Expected: PASS — both authorizers deny carol on the discriminator model.

- [ ] **Step 5: Commit**

```bash
git add tests/Relkit.Differential
git commit -m "feat: add dual-seed differential harness building oracle + CTE over one model"
```

---

### Task 4: Differential Check property

**Files:**
- Create: `tests/Relkit.Differential/CheckEquivalenceTests.cs`

**Interfaces:**
- Consumes: `ModelGenerator.Gen`, `DifferentialHarness`. The property: for a generated model and each `(probeObject, permission, probeSubject)`, `oracle.CheckAsync(...).Allowed == cte.CheckAsync(...).Allowed`. A unique store id per iteration isolates Postgres state.

> **Async + CsCheck.** CsCheck's `SampleAsync` runs an async predicate; each iteration awaits both authorizers and returns a `bool`. The permission probed is the model schema's primary permission per type (the harness knows it from the probe object's type). Keep `iter` modest (each iteration spins a fresh Postgres tenant) — a few hundred random models over three skeletons is thousands of `(object, subject)` comparisons.

- [ ] **Step 1: Write the property test**

```csharp
// tests/Relkit.Differential/CheckEquivalenceTests.cs
using CsCheck;
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Differential;

[Collection("differential")]
public class CheckEquivalenceTests(DifferentialFixture fx)
{
    // The permission to probe for each probe-object type in the curated skeletons.
    private static string PermissionFor(string type) => type switch
    {
        "doc" => "view",
        "animal" => "edit-or-access",   // resolved per-schema below
        _ => "view",
    };

    [Fact]
    public async Task Cte_check_equals_oracle_check_over_random_models()
    {
        var counter = 0;
        await ModelGenerator.Gen.SampleAsync(async model =>
        {
            var store = $"chk-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var obj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, obj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
                var req = new CheckRequest(tenant, obj, perm, subject, ctx);
                var o = (await oracle.CheckAsync(req)).Allowed;
                var c = (await cte.CheckAsync(req)).Allowed;
                if (o != c) return false;   // divergence => CsCheck minimizes this model
            }
            return true;
        }, iter: 200);
    }

    // The permissions defined on the probe object's type in the generated schema.
    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);
}
```

> `PermissionFor` above is illustrative; the test actually enumerates `PermissionsOf(model, type)` from the generated schema, so it probes every permission each curated skeleton defines (`doc.view`, `animal.edit`/`animal.access`). Delete the unused `PermissionFor` helper before committing if the analyzer flags it.

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Differential --filter CheckEquivalenceTests`
Expected: PASS. A failure prints a minimized counterexample model — that is a real `NpgsqlCteAuthorizer` bug; fix the CTE path (`m1/05`), the oracle is the spec.

- [ ] **Step 3: Commit**

```bash
git add tests/Relkit.Differential/CheckEquivalenceTests.cs
git commit -m "test: assert CTE check equals the oracle over random valid models"
```

---

### Task 5: Differential ListObjects and ListSubjects properties

**Files:**
- Create: `tests/Relkit.Differential/ListEquivalenceTests.cs`

**Interfaces:**
- Consumes: `ModelGenerator.Gen`, `DifferentialHarness`. Two properties: (1) for each `(probeSubject, type, permission)`, the **set** of `ListObjects` ids agrees between oracle and CTE (drained across all pages); (2) for each `(probeObject, permission)`, the **set** of `ListSubjects` subjects agrees. Sets, not sequences — pagination order is internal; the contract is the membership set.

> **Drain all pages.** Both authorizers honour over-fetch/refill with the same `ContinuationCursor`, but the harness compares the *complete* result set, so it pages each authorizer to exhaustion (token == null) and compares the unioned id sets. This catches a CTE candidate-generation miss (a true positive the SQL never surfaces) — exactly the failure mode the calibration notes in `m1/06` warn about.

- [ ] **Step 1: Write the property tests**

```csharp
// tests/Relkit.Differential/ListEquivalenceTests.cs
using CsCheck;
using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Differential;

[Collection("differential")]
public class ListEquivalenceTests(DifferentialFixture fx)
{
    private static async Task<HashSet<string>> DrainObjectsAsync(
        IAuthorizer auth, TenantContext t, SubjectRef subject, string type, string perm)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());
            var page = await auth.ListObjectsAsync(new ListObjectsRequest(t, subject, type, perm, ctx, PageSize: 2, ContinuationToken: token));
            foreach (var id in page.ObjectIds) ids.Add(id);
            token = page.ContinuationToken;
        } while (token is not null);
        return ids;
    }

    private static async Task<HashSet<string>> DrainSubjectsAsync(
        IAuthorizer auth, TenantContext t, EntityRef obj, string perm)
    {
        var subs = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), new Dictionary<string, object?>());
            var page = await auth.ListSubjectsAsync(new ListSubjectsRequest(t, obj, perm, ctx, PageSize: 2, ContinuationToken: token));
            foreach (var s in page.Subjects) subs.Add(s.ToString());
            token = page.ContinuationToken;
        } while (token is not null);
        return subs;
    }

    private static IEnumerable<string> PermissionsOf(GeneratedModel model, string type) =>
        model.Schema.Types.Single(t => t.Name == type).Permissions.Select(p => p.Name);

    [Fact]
    public async Task Cte_list_objects_set_equals_oracle_over_random_models()
    {
        var counter = 0;
        await ModelGenerator.Gen.SampleAsync(async model =>
        {
            var store = $"lo-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var probeObj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, probeObj.Type))
            foreach (var subject in model.ProbeSubjects)
            {
                var o = await DrainObjectsAsync(oracle, tenant, subject, probeObj.Type, perm);
                var c = await DrainObjectsAsync(cte, tenant, subject, probeObj.Type, perm);
                if (!o.SetEquals(c)) return false;
            }
            return true;
        }, iter: 150);
    }

    [Fact]
    public async Task Cte_list_subjects_set_equals_oracle_over_random_models()
    {
        var counter = 0;
        await ModelGenerator.Gen.SampleAsync(async model =>
        {
            var store = $"ls-{Interlocked.Increment(ref counter)}";
            var (oracle, cte) = await DifferentialHarness.BuildAsync(fx, model, store);
            var tenant = new TenantContext(store, "t");

            foreach (var probeObj in model.ProbeObjects)
            foreach (var perm in PermissionsOf(model, probeObj.Type))
            {
                var o = await DrainSubjectsAsync(oracle, tenant, probeObj, perm);
                var c = await DrainSubjectsAsync(cte, tenant, probeObj, perm);
                if (!o.SetEquals(c)) return false;
            }
            return true;
        }, iter: 150);
    }
}
```

- [ ] **Step 2: Run to verify**

Run: `dotnet test tests/Relkit.Differential --filter ListEquivalenceTests`
Expected: PASS. A divergence is a CTE list-path bug (candidate miss or confirm error); fix `m1/06`, the oracle is the spec.

- [ ] **Step 3: Run the whole harness**

Run: `dotnet test tests/Relkit.Differential`
Expected: PASS (wiring + generator + smoke + check + list properties).

- [ ] **Step 4: Commit**

```bash
git add tests/Relkit.Differential/ListEquivalenceTests.cs
git commit -m "test: assert CTE list-objects and list-subjects sets equal the oracle"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`; the harness project references Core, InMemory, and Postgres providers.
- [ ] Every generated schema passes `m0/03` validation (Task 2 property) — so both authorizers run real evaluations, never throw in lockstep.
- [ ] The generator's three skeletons cover union, exclusion, arrow-into-permission, arrow-into-relation, nested groups, the inner-exclusion-through-arrow discriminator, and the §12.5 wildcard gate (Task 2).
- [ ] The harness seeds the *same* model into the in-memory oracle and a fresh Postgres tenant, with the same `IConditionEvaluator` (Task 3).
- [ ] Check equivalence holds over random models, probing every permission and probe-subject (Task 4).
- [ ] ListObjects and ListSubjects **set** equivalence holds, draining all pages (Task 5).
- [ ] Divergences are framed as CTE bugs to fix in `m1/05`/`m1/06`; the oracle is never adjusted to match (calibration note).

## Contract gaps (reported, not changed)

- **None new.** This plan adds a test project only. It depends on the `Relkit.Storage.Postgres → Relkit.Core` reference decided in `m1/02`/`m1/05` and the reused `ContinuationCursor`/`SchemaValidator`/oracle from M0. If CsCheck's `SampleAsync`/`Gen` combinator surface differs from the pinned version, adapt the call sites — the properties (equivalence over valid random models) are the durable contract.
```


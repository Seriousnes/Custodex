# M0/04 — In-Memory Storage Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A thread-safe, in-memory implementation of every storage provider interface — `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `ICacheStore`, `IChangeLogStore` — plus a no-op `IUnitOfWork`/`IUnitOfWorkFactory`, in a new `Custodex.Storage.InMemory` project. These back the fast millisecond unit tests for every later M0 plan and serve as the portable reference provider.

**Architecture:** Each store keeps its data in plain in-memory dictionaries guarded by a single per-store lock (simple, correct locking; the engine targets small scale and these providers exist for tests and dev). Every operation filters on `(store, tenant)` as a hard, non-optional predicate (spec §6.3 — tenant isolation is a core invariant of data access, not an ambient filter). The no-op `IUnitOfWork` applies writes immediately and `CommitAsync` is a no-op, matching the transaction seam (spec README: "the in-memory provider uses a no-op; Postgres enlists in an ambient or supplied DbTransaction"). Time-dependent behaviour (cache TTL) flows through an injected `TimeProvider`, never `DateTimeOffset.UtcNow`, so expiry tests are deterministic.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly. Project name: `Custodex.Storage.InMemory`.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard; determinism — no ambient `DateTime.Now`. Depends on `m0/01` (`Custodex.Abstractions`).

---

### Task 1: Create `Custodex.Storage.InMemory` and the no-op unit of work

**Files:**
- Create: `src/Custodex.Storage.InMemory/Custodex.Storage.InMemory.csproj`
- Create: `src/Custodex.Storage.InMemory/NoOpUnitOfWork.cs`
- Create: `tests/Custodex.Storage.InMemory.Tests/Custodex.Storage.InMemory.Tests.csproj`
- Test: `tests/Custodex.Storage.InMemory.Tests/UnitOfWorkTests.cs`

**Interfaces:**
- Produces: `NoOpUnitOfWork : IUnitOfWork`, `NoOpUnitOfWorkFactory : IUnitOfWorkFactory`.
- Consumes: `IUnitOfWork`, `IUnitOfWorkFactory` from `Custodex.Abstractions`.

- [ ] **Step 1: Create the project and references**

Run:
```bash
dotnet new classlib -n Custodex.Storage.InMemory -o src/Custodex.Storage.InMemory -f net10.0
dotnet new xunit -n Custodex.Storage.InMemory.Tests -o tests/Custodex.Storage.InMemory.Tests -f net10.0
rm src/Custodex.Storage.InMemory/Class1.cs tests/Custodex.Storage.InMemory.Tests/UnitTest1.cs
dotnet sln add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
dotnet add src/Custodex.Storage.InMemory reference src/Custodex.Abstractions
dotnet add tests/Custodex.Storage.InMemory.Tests reference src/Custodex.Storage.InMemory
dotnet add tests/Custodex.Storage.InMemory.Tests reference src/Custodex.Abstractions
dotnet add tests/Custodex.Storage.InMemory.Tests package Shouldly
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Custodex.Storage.InMemory.Tests/UnitOfWorkTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class UnitOfWorkTests
{
    [Fact]
    public async Task Factory_yields_a_committable_disposable_unit_of_work()
    {
        IUnitOfWorkFactory factory = new NoOpUnitOfWorkFactory();

        await using var uow = await factory.BeginAsync();
        await uow.CommitAsync();   // no-op, must not throw
        uow.ShouldBeAssignableTo<IUnitOfWork>();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter UnitOfWorkTests`
Expected: FAIL — `NoOpUnitOfWorkFactory` does not exist.

- [ ] **Step 4: Implement the no-op unit of work**

```csharp
// src/Custodex.Storage.InMemory/NoOpUnitOfWork.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NoOpUnitOfWorkFactory : IUnitOfWorkFactory
{
    public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default) =>
        Task.FromResult<IUnitOfWork>(new NoOpUnitOfWork());
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter UnitOfWorkTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
git commit -m "chore: scaffold Custodex.Storage.InMemory with a no-op unit of work"
```

---

### Task 2: In-memory `IRelationStore`

**Files:**
- Create: `src/Custodex.Storage.InMemory/InMemoryRelationStore.cs`
- Test: `tests/Custodex.Storage.InMemory.Tests/RelationStoreTests.cs`

**Interfaces:**
- Produces: `InMemoryRelationStore : IRelationStore`.
- Consumes: `IRelationStore`, `RelationTuple`, `EntityRef`, `SubjectRef`, `TenantContext`, `IUnitOfWork`.

Tuple identity for add/remove cannot rely on `RelationTuple` record equality: `ConditionRef.Parameters` is an `IReadOnlyDictionary` (reference equality), so two logically-identical conditioned tuples are not `Equals`. Identity is `(Object, Relation, Subject, Condition?.Name)`. `GetBySubjectAsync` matches on the full subject including `Relation` (a subject-set `group:vets#member` is distinct from `group:vets`). All reads and writes filter on `(store, tenant)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.InMemory.Tests/RelationStoreTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class RelationStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();

    private static RelationTuple Member(string user) =>
        new(new EntityRef("group", "vets"), "member", new SubjectRef("user", user));

    [Fact]
    public async Task Written_tuples_are_returned_by_object_and_relation()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith"), Member("alice")], [], Uow);

        var found = await store.GetByObjectAsync(T1, new EntityRef("group", "vets"), "member");

        found.Select(t => t.Subject.Id).OrderBy(x => x).ShouldBe(["alice", "dr-smith"]);
    }

    [Fact]
    public async Task Remove_matches_on_identity_ignoring_condition_parameter_identity()
    {
        var store = new InMemoryRelationStore();
        var add = new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        await store.WriteAsync(T1, [add], [], Uow);

        // A logically-equal tuple with a *different dictionary instance* must still remove it.
        var remove = new RelationTuple(new EntityRef("category", "drugs"), "dispenser",
            new SubjectRef("group", "vets", "member"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        await store.WriteAsync(T1, [], [remove], Uow);

        (await store.GetByObjectAsync(T1, new EntityRef("category", "drugs"), "dispenser")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_by_subject_returns_tuples_where_the_subject_appears()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        var bySubject = await store.GetBySubjectAsync(T1, new SubjectRef("user", "dr-smith"));

        bySubject.ShouldHaveSingleItem().Object.ShouldBe(new EntityRef("group", "vets"));
    }

    [Fact]
    public async Task Tuples_do_not_leak_across_tenants()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        (await store.GetByObjectAsync(T2, new EntityRef("group", "vets"), "member")).ShouldBeEmpty();
        (await store.GetBySubjectAsync(T2, new SubjectRef("user", "dr-smith"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task Writing_a_duplicate_tuple_is_idempotent()
    {
        var store = new InMemoryRelationStore();
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);
        await store.WriteAsync(T1, [Member("dr-smith")], [], Uow);

        (await store.GetByObjectAsync(T1, new EntityRef("group", "vets"), "member")).Count.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter RelationStoreTests`
Expected: FAIL — `InMemoryRelationStore` does not exist.

- [ ] **Step 3: Implement the relation store**

```csharp
// src/Custodex.Storage.InMemory/InMemoryRelationStore.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemoryRelationStore : IRelationStore
{
    private readonly record struct Key(string Store, string Tenant);

    // Identity excludes ConditionRef.Parameters (reference-equality on the dictionary).
    private readonly record struct TupleIdentity(
        string ObjType, string ObjId, string Relation,
        string SubjType, string SubjId, string? SubjRelation, string? ConditionName);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Dictionary<TupleIdentity, RelationTuple>> _data = new();

    private static Key KeyOf(TenantContext t) => new(t.Store, t.Tenant);

    private static TupleIdentity IdentityOf(RelationTuple tuple) => new(
        tuple.Object.Type, tuple.Object.Id, tuple.Relation,
        tuple.Subject.Type, tuple.Subject.Id, tuple.Subject.Relation, tuple.Condition?.Name);

    public Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(
        TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<RelationTuple> result =
                _data.TryGetValue(KeyOf(t), out var bucket)
                    ? bucket.Values.Where(x =>
                        string.Equals(x.Object.Type, obj.Type, StringComparison.Ordinal) &&
                        string.Equals(x.Object.Id, obj.Id, StringComparison.Ordinal) &&
                        string.Equals(x.Relation, relation, StringComparison.Ordinal)).ToList()
                    : [];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(
        TenantContext t, SubjectRef subject, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<RelationTuple> result =
                _data.TryGetValue(KeyOf(t), out var bucket)
                    ? bucket.Values.Where(x =>
                        string.Equals(x.Subject.Type, subject.Type, StringComparison.Ordinal) &&
                        string.Equals(x.Subject.Id, subject.Id, StringComparison.Ordinal) &&
                        string.Equals(x.Subject.Relation, subject.Relation, StringComparison.Ordinal)).ToList()
                    : [];
            return Task.FromResult(result);
        }
    }

    public Task WriteAsync(
        TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(KeyOf(t), out var bucket))
                _data[KeyOf(t)] = bucket = new Dictionary<TupleIdentity, RelationTuple>();

            foreach (var tuple in remove)
                bucket.Remove(IdentityOf(tuple));
            foreach (var tuple in add)
                bucket[IdentityOf(tuple)] = tuple;
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter RelationStoreTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
git commit -m "feat: add in-memory relation store with identity-based add/remove"
```

---

### Task 3: In-memory `ISchemaStore` and `IAttributeStore`

**Files:**
- Create: `src/Custodex.Storage.InMemory/InMemorySchemaStore.cs`
- Create: `src/Custodex.Storage.InMemory/InMemoryAttributeStore.cs`
- Test: `tests/Custodex.Storage.InMemory.Tests/SchemaStoreTests.cs`
- Test: `tests/Custodex.Storage.InMemory.Tests/AttributeStoreTests.cs`

**Interfaces:**
- Produces: `InMemorySchemaStore : ISchemaStore`, `InMemoryAttributeStore : IAttributeStore`.
- Consumes: `ISchemaStore`, `IAttributeStore`, `Schema`, `EntityRef`, `TenantContext`, `IUnitOfWork`.

`ISchemaStore` is keyed by `store` only (a schema is per-Store, not per-tenant). `IAttributeStore` is keyed by `(store, tenant, objectType, objectId)`; `SetAsync` replaces the attribute bag for an object. Both return a defensive copy so callers cannot mutate stored state.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.InMemory.Tests/SchemaStoreTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class SchemaStoreTests
{
    private static readonly NoOpUnitOfWork Uow = new();

    private static Schema SchemaV(string version) => new(version, [], []);

    [Fact]
    public async Task Missing_store_returns_null()
    {
        (await new InMemorySchemaStore().GetActiveAsync("nope")).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_active_schema()
    {
        var store = new InMemorySchemaStore();
        await store.SetActiveAsync("zoo", SchemaV("v1"), Uow);

        (await store.GetActiveAsync("zoo"))!.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Set_replaces_the_previous_active_schema()
    {
        var store = new InMemorySchemaStore();
        await store.SetActiveAsync("zoo", SchemaV("v1"), Uow);
        await store.SetActiveAsync("zoo", SchemaV("v2"), Uow);

        (await store.GetActiveAsync("zoo"))!.Version.ShouldBe("v2");
    }
}
```

```csharp
// tests/Custodex.Storage.InMemory.Tests/AttributeStoreTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class AttributeStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();
    private static readonly EntityRef Animal = new("animal", "EL-001");

    [Fact]
    public async Task Missing_object_returns_null()
    {
        (await new InMemoryAttributeStore().GetAsync(T1, Animal)).ShouldBeNull();
    }

    [Fact]
    public async Task Set_then_get_returns_the_attribute_bag()
    {
        var store = new InMemoryAttributeStore();
        await store.SetAsync(T1, Animal, new Dictionary<string, object?> { ["weight"] = 42 }, Uow);

        var attrs = await store.GetAsync(T1, Animal);

        attrs!["weight"].ShouldBe(42);
    }

    [Fact]
    public async Task Stored_bag_is_isolated_from_later_caller_mutation()
    {
        var store = new InMemoryAttributeStore();
        var input = new Dictionary<string, object?> { ["weight"] = 42 };
        await store.SetAsync(T1, Animal, input, Uow);
        input["weight"] = 99;   // mutate the caller's dictionary after the write

        (await store.GetAsync(T1, Animal))!["weight"].ShouldBe(42);
    }

    [Fact]
    public async Task Attributes_do_not_leak_across_tenants()
    {
        var store = new InMemoryAttributeStore();
        await store.SetAsync(T1, Animal, new Dictionary<string, object?> { ["weight"] = 42 }, Uow);

        (await store.GetAsync(T2, Animal)).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter "SchemaStoreTests|AttributeStoreTests"`
Expected: FAIL — the stores do not exist.

- [ ] **Step 3: Implement the schema and attribute stores**

```csharp
// src/Custodex.Storage.InMemory/InMemorySchemaStore.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemorySchemaStore : ISchemaStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Schema> _active = new(StringComparer.Ordinal);

    public Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_active.TryGetValue(store, out var schema) ? schema : null);
    }

    public Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
            _active[store] = schema;
        return Task.CompletedTask;
    }
}
```

```csharp
// src/Custodex.Storage.InMemory/InMemoryAttributeStore.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemoryAttributeStore : IAttributeStore
{
    private readonly record struct Key(string Store, string Tenant, string ObjType, string ObjId);

    private readonly object _gate = new();
    private readonly Dictionary<Key, Dictionary<string, object?>> _data = new();

    private static Key KeyOf(TenantContext t, EntityRef obj) => new(t.Store, t.Tenant, obj.Type, obj.Id);

    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        TenantContext t, EntityRef obj, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(KeyOf(t, obj), out var bag))
                return Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);
            IReadOnlyDictionary<string, object?> copy =
                new Dictionary<string, object?>(bag, StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, object?>?>(copy);
        }
    }

    public Task SetAsync(
        TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs,
        IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
            _data[KeyOf(t, obj)] = new Dictionary<string, object?>(attrs, StringComparer.Ordinal);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter "SchemaStoreTests|AttributeStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
git commit -m "feat: add in-memory schema and attribute stores"
```

---

### Task 4: In-memory `IChangeLogStore`

**Files:**
- Create: `src/Custodex.Storage.InMemory/InMemoryChangeLogStore.cs`
- Test: `tests/Custodex.Storage.InMemory.Tests/ChangeLogStoreTests.cs`

**Interfaces:**
- Produces: `InMemoryChangeLogStore : IChangeLogStore`.
- Consumes: `IChangeLogStore`, `ChangeLogEntry`, `ChangeLogFilter`, `TenantContext`, `IUnitOfWork`.

The store assigns the sequential `long Id` on append (the caller's `ChangeLogEntry.Id` is ignored on write); ids start at 1 and increase per `(store, tenant)`. `ReadAsync` returns entries newest-first (descending id), honours `ChangeLogFilter.Since`/`Actor`, and caps at `ChangeLogFilter.Limit`. All operations filter on `(store, tenant)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.InMemory.Tests/ChangeLogStoreTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class ChangeLogStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly TenantContext T2 = new("zoo", "t2");
    private static readonly NoOpUnitOfWork Uow = new();

    private static ChangeLogEntry Entry(string actor, DateTimeOffset at) =>
        new(0, actor, "write", "category:drugs#dispenser@group:vets#member", null, null, at);

    [Fact]
    public async Task Append_assigns_sequential_ids_starting_at_one()
    {
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(T1, Entry("alice", DateTimeOffset.UnixEpoch), Uow);
        await store.AppendAsync(T1, Entry("bob", DateTimeOffset.UnixEpoch.AddSeconds(1)), Uow);

        var entries = await store.ReadAsync(T1, new ChangeLogFilter());

        entries.Select(e => e.Id).ShouldBe([2, 1]);   // newest first
        entries[0].Actor.ShouldBe("bob");
    }

    [Fact]
    public async Task Read_filters_by_actor_and_since_and_limit()
    {
        var store = new InMemoryChangeLogStore();
        var t0 = DateTimeOffset.UnixEpoch;
        await store.AppendAsync(T1, Entry("alice", t0), Uow);
        await store.AppendAsync(T1, Entry("bob", t0.AddMinutes(5)), Uow);
        await store.AppendAsync(T1, Entry("bob", t0.AddMinutes(10)), Uow);

        var byActor = await store.ReadAsync(T1, new ChangeLogFilter(Actor: "bob"));
        byActor.Count.ShouldBe(2);
        byActor.ShouldAllBe(e => e.Actor == "bob");

        var since = await store.ReadAsync(T1, new ChangeLogFilter(Since: t0.AddMinutes(6)));
        since.ShouldHaveSingleItem().OccurredAt.ShouldBe(t0.AddMinutes(10));

        var capped = await store.ReadAsync(T1, new ChangeLogFilter(Limit: 1));
        capped.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Change_log_does_not_leak_across_tenants()
    {
        var store = new InMemoryChangeLogStore();
        await store.AppendAsync(T1, Entry("alice", DateTimeOffset.UnixEpoch), Uow);

        (await store.ReadAsync(T2, new ChangeLogFilter())).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter ChangeLogStoreTests`
Expected: FAIL — `InMemoryChangeLogStore` does not exist.

- [ ] **Step 3: Implement the change-log store**

```csharp
// src/Custodex.Storage.InMemory/InMemoryChangeLogStore.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemoryChangeLogStore : IChangeLogStore
{
    private readonly record struct Key(string Store, string Tenant);

    private readonly object _gate = new();
    private readonly Dictionary<Key, List<ChangeLogEntry>> _data = new();
    private readonly Dictionary<Key, long> _nextId = new();

    private static Key KeyOf(TenantContext t) => new(t.Store, t.Tenant);

    public Task AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = KeyOf(t);
            if (!_data.TryGetValue(key, out var list))
            {
                _data[key] = list = [];
                _nextId[key] = 1;
            }
            var id = _nextId[key]++;
            list.Add(entry with { Id = id });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(
        TenantContext t, ChangeLogFilter filter, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<ChangeLogEntry> query =
                _data.TryGetValue(KeyOf(t), out var list) ? list : [];

            if (filter.Since is { } since)
                query = query.Where(e => e.OccurredAt >= since);
            if (filter.Actor is { } actor)
                query = query.Where(e => string.Equals(e.Actor, actor, StringComparison.Ordinal));

            IReadOnlyList<ChangeLogEntry> result = query
                .OrderByDescending(e => e.Id)
                .Take(filter.Limit)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter ChangeLogStoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
git commit -m "feat: add in-memory change-log store with sequential ids"
```

---

### Task 5: In-memory `ICacheStore` with epoch and deterministic TTL

**Files:**
- Create: `src/Custodex.Storage.InMemory/InMemoryCacheStore.cs`
- Test: `tests/Custodex.Storage.InMemory.Tests/CacheStoreTests.cs`

**Interfaces:**
- Produces: `InMemoryCacheStore : ICacheStore`, constructed with a `TimeProvider` (default `TimeProvider.System`).
- Consumes: `ICacheStore`, `CacheEntry`, `TenantContext`, `IUnitOfWork`.

The cache stores `CacheEntry(byte[] Value, long Epoch)` by key with an expiry derived from the injected `TimeProvider` — never `DateTimeOffset.UtcNow`, so expiry is deterministic in tests via `FakeTimeProvider` semantics (here a tiny inline test double). `CacheEntry.Epoch` round-trips unchanged (the caching layer in `m0/08` compares it to the current tenant epoch). Per-`(store, tenant)` epoch starts at 0; `BumpEpochAsync` increments it; `GetEpochAsync` reads it. An expired entry reads back as `null` (lazy expiry on read).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Custodex.Storage.InMemory.Tests/CacheStoreTests.cs
using Custodex.Abstractions;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Storage.InMemory.Tests;

public class CacheStoreTests
{
    private static readonly TenantContext T1 = new("zoo", "t1");
    private static readonly NoOpUnitOfWork Uow = new();

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task Set_then_get_round_trips_value_and_epoch()
    {
        var store = new InMemoryCacheStore(new TestClock(DateTimeOffset.UnixEpoch));
        await store.SetAsync("k", new CacheEntry([1, 2, 3], Epoch: 7), TimeSpan.FromMinutes(5));

        var entry = await store.GetAsync("k");

        entry!.Value.ShouldBe([1, 2, 3]);
        entry.Epoch.ShouldBe(7);
    }

    [Fact]
    public async Task Missing_key_returns_null()
    {
        (await new InMemoryCacheStore().GetAsync("absent")).ShouldBeNull();
    }

    [Fact]
    public async Task Entry_expires_after_its_ttl()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryCacheStore(clock);
        await store.SetAsync("k", new CacheEntry([9], 1), TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(4));
        (await store.GetAsync("k")).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMinutes(2));   // now 6 minutes > 5-minute TTL
        (await store.GetAsync("k")).ShouldBeNull();
    }

    [Fact]
    public async Task Epoch_starts_at_zero_and_increments_on_bump()
    {
        var store = new InMemoryCacheStore();

        (await store.GetEpochAsync(T1)).ShouldBe(0);
        await store.BumpEpochAsync(T1, Uow);
        await store.BumpEpochAsync(T1, Uow);
        (await store.GetEpochAsync(T1)).ShouldBe(2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter CacheStoreTests`
Expected: FAIL — `InMemoryCacheStore` does not exist.

- [ ] **Step 3: Implement the cache store**

```csharp
// src/Custodex.Storage.InMemory/InMemoryCacheStore.cs
using Custodex.Abstractions;

namespace Custodex.Storage.InMemory;

public sealed class InMemoryCacheStore(TimeProvider? timeProvider = null) : ICacheStore
{
    private readonly record struct Key(string Store, string Tenant);
    private sealed record Slot(CacheEntry Entry, DateTimeOffset ExpiresAt);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Key, long> _epochs = new();

    public Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var slot))
                return Task.FromResult<CacheEntry?>(null);
            if (_clock.GetUtcNow() >= slot.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<CacheEntry?>(null);
            }
            return Task.FromResult<CacheEntry?>(slot.Entry);
        }
    }

    public Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
            _entries[key] = new Slot(entry, _clock.GetUtcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_epochs.TryGetValue(new Key(t.Store, t.Tenant), out var e) ? e : 0);
    }

    public Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = new Key(t.Store, t.Tenant);
            _epochs[key] = (_epochs.TryGetValue(key, out var e) ? e : 0) + 1;
        }
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Storage.InMemory.Tests --filter CacheStoreTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Storage.InMemory tests/Custodex.Storage.InMemory.Tests
git commit -m "feat: add in-memory cache store with epoch and deterministic ttl"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Every provider interface (`IRelationStore`, `ISchemaStore`, `IAttributeStore`, `ICacheStore`, `IChangeLogStore`) has an in-memory implementation; `NoOpUnitOfWork`/`NoOpUnitOfWorkFactory` are present.
- [ ] Every read and write filters on `(store, tenant)`; a cross-tenant no-leak test exists for the relation, attribute, and change-log stores.
- [ ] Relation add/remove identity excludes `ConditionRef.Parameters`; add-then-remove of a logically-equal conditioned tuple works.
- [ ] Cache TTL uses an injected `TimeProvider`; no `DateTimeOffset.UtcNow` in the implementation.
- [ ] Change-log ids are store-assigned, sequential per `(store, tenant)`, and reads honour the filter.

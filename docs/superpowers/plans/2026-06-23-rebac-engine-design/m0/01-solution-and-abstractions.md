# M0/01 — Solution & Abstractions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the Relkit solution, project layout, packaging, observability primitives, and the complete `Relkit.Abstractions` public contract.

**Architecture:** `Relkit.Abstractions` is contracts only — records, interfaces, enums, exceptions. No logic except trivial computed members on value types. Every other package and plan depends on these names.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

## Global Constraints

See `../README.md` → Global Constraints. Key points repeated for convenience: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata on every package; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard.

This plan is the source of truth for the type names in `../README.md` → Canonical public contract. Use those signatures verbatim.

---

### Task 1: Solution, build props, and the Abstractions project

**Files:**
- Create: `Relkit.sln`
- Create: `Directory.Build.props`
- Create: `src/Relkit.Abstractions/Relkit.Abstractions.csproj`
- Create: `tests/Relkit.Abstractions.Tests/Relkit.Abstractions.Tests.csproj`
- Test: `tests/Relkit.Abstractions.Tests/WiringTests.cs`

**Interfaces:**
- Produces: a buildable solution and the `Relkit.Abstractions` assembly that later tasks add types to.

- [ ] **Step 1: Confirm the existing projects and wire the test reference**

The Aspire solution (`Relkit.slnx`) and the `Relkit.Abstractions` + `Relkit.Abstractions.Tests` projects **already exist**. Do NOT run `dotnet new sln`/`classlib`/`xunit`. Remove any leftover template files, then add the test→project reference and Shouldly:

Run:
```bash
# remove template leftovers if present (ignore errors if already gone)
rm -f src/Relkit.Abstractions/Class1.cs tests/Relkit.Abstractions.Tests/UnitTest1.cs
dotnet add tests/Relkit.Abstractions.Tests reference src/Relkit.Abstractions
dotnet add tests/Relkit.Abstractions.Tests package Shouldly
```

- [ ] **Step 2: Write `Directory.Build.props`** (repo root)

> If `Directory.Build.props` already exists, merge these properties in. The Aspire `Relkit.AppHost`/`Relkit.ServiceDefaults` projects keep their own SDK and may override `TargetFramework`; that is expected. If `TreatWarningsAsErrors` is too aggressive against Aspire-generated code, scope it to `src/Relkit.*` engine projects via a condition rather than globally.

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Authors>Relkit contributors</Authors>
    <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write the wiring test**

```csharp
// tests/Relkit.Abstractions.Tests/WiringTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class WiringTests
{
    [Fact]
    public void Abstractions_assembly_is_referenced()
    {
        typeof(Relkit.Abstractions.EntityRef).Assembly.GetName().Name.ShouldBe("Relkit.Abstractions");
    }
}
```

- [ ] **Step 4: Run the test to verify it fails to compile**

Run: `dotnet test tests/Relkit.Abstractions.Tests`
Expected: FAIL — `EntityRef` does not exist yet.

- [ ] **Step 5: Add a placeholder `EntityRef` to make wiring compile** (replaced fully in Task 2)

```csharp
// src/Relkit.Abstractions/EntityRef.cs
namespace Relkit.Abstractions;
public readonly record struct EntityRef(string Type, string Id);
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Relkit.Abstractions.Tests`
Expected: PASS (1 test).

- [ ] **Step 7: Commit**

```bash
git add Directory.Build.props src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "chore: wire Directory.Build.props and Abstractions test reference"
```

---

### Task 2: Reference and tuple types

**Files:**
- Modify: `src/Relkit.Abstractions/EntityRef.cs`
- Create: `src/Relkit.Abstractions/SubjectRef.cs`
- Create: `src/Relkit.Abstractions/RelationTuple.cs`
- Create: `src/Relkit.Abstractions/TenantContext.cs`
- Test: `tests/Relkit.Abstractions.Tests/ReferenceTypesTests.cs`

**Interfaces:**
- Produces: `EntityRef`, `SubjectRef`, `ConditionRef`, `RelationTuple`, `TenantContext` exactly as in the contract.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Relkit.Abstractions.Tests/ReferenceTypesTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class ReferenceTypesTests
{
    [Fact]
    public void EntityRef_detects_wildcard_and_formats()
    {
        new EntityRef("animal", "EL-001").IsWildcard.ShouldBeFalse();
        new EntityRef("user", "*").IsWildcard.ShouldBeTrue();
        new EntityRef("animal", "EL-001").ToString().ShouldBe("animal:EL-001");
    }

    [Fact]
    public void SubjectRef_detects_subject_set_and_wildcard()
    {
        new SubjectRef("group", "vets", "member").IsSubjectSet.ShouldBeTrue();
        new SubjectRef("user", "alice").IsSubjectSet.ShouldBeFalse();
        new SubjectRef("user", "*").IsWildcard.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ReferenceTypesTests`
Expected: FAIL — `IsWildcard`/`IsSubjectSet` not defined.

- [ ] **Step 3: Write the types**

```csharp
// src/Relkit.Abstractions/EntityRef.cs
namespace Relkit.Abstractions;

public readonly record struct EntityRef(string Type, string Id)
{
    public bool IsWildcard => Id == "*";
    public override string ToString() => $"{Type}:{Id}";
}
```

```csharp
// src/Relkit.Abstractions/SubjectRef.cs
namespace Relkit.Abstractions;

public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    public bool IsSubjectSet => Relation is not null;
    public bool IsWildcard => Id == "*";
    public override string ToString() => Relation is null ? $"{Type}:{Id}" : $"{Type}:{Id}#{Relation}";
}
```

```csharp
// src/Relkit.Abstractions/RelationTuple.cs
namespace Relkit.Abstractions;

public sealed record ConditionRef(string Name, IReadOnlyDictionary<string, object?> Parameters);

public sealed record RelationTuple(
    EntityRef Object,
    string Relation,
    SubjectRef Subject,
    ConditionRef? Condition = null);
```

```csharp
// src/Relkit.Abstractions/TenantContext.cs
namespace Relkit.Abstractions;

public readonly record struct TenantContext(string Store, string Tenant);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ReferenceTypesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add reference and tuple types to abstractions"
```

---

### Task 3: Request, result, and context types

**Files:**
- Create: `src/Relkit.Abstractions/RequestContext.cs`
- Create: `src/Relkit.Abstractions/Requests.cs`
- Create: `src/Relkit.Abstractions/IAuthorizer.cs`
- Test: `tests/Relkit.Abstractions.Tests/RequestTypesTests.cs`

**Interfaces:**
- Produces: `RequestContext`, `CheckRequest`, `CheckResult`, `BatchCheckRequest`, `CheckItem`, `ListObjectsRequest`, `ListObjectsResult`, `ListSubjectsRequest`, `ListSubjectsResult`, `ExplainNode`, `IAuthorizer`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/RequestTypesTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class RequestTypesTests
{
    [Fact]
    public void ListObjects_request_defaults_page_size_and_null_cursor()
    {
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
            new Dictionary<string, object?>());
        var req = new ListObjectsRequest(new TenantContext("zoo", "t1"),
            new SubjectRef("user", "alice"), "animal", "edit", ctx);
        req.PageSize.ShouldBe(100);
        req.ContinuationToken.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter RequestTypesTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Write the types**

```csharp
// src/Relkit.Abstractions/RequestContext.cs
namespace Relkit.Abstractions;

public sealed record RequestContext(
    DateTimeOffset Now,
    SubjectRef Subject,
    IReadOnlyDictionary<string, object?> Attributes);
```

```csharp
// src/Relkit.Abstractions/Requests.cs
namespace Relkit.Abstractions;

public sealed record CheckRequest(
    TenantContext Tenant, EntityRef Object, string Permission, SubjectRef Subject,
    RequestContext Context, bool Explain = false);

public sealed record ExplainNode(string Description, bool Allowed, IReadOnlyList<ExplainNode> Children);
public sealed record CheckResult(bool Allowed, ExplainNode? Explain = null);

public sealed record CheckItem(EntityRef Object, string Permission, SubjectRef Subject);
public sealed record BatchCheckRequest(TenantContext Tenant, IReadOnlyList<CheckItem> Items, RequestContext Context);

public sealed record ListObjectsRequest(
    TenantContext Tenant, SubjectRef Subject, string ObjectType, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListObjectsResult(IReadOnlyList<string> ObjectIds, string? ContinuationToken);

public sealed record ListSubjectsRequest(
    TenantContext Tenant, EntityRef Object, string Permission,
    RequestContext Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListSubjectsResult(IReadOnlyList<SubjectRef> Subjects, string? ContinuationToken);
```

```csharp
// src/Relkit.Abstractions/IAuthorizer.cs
namespace Relkit.Abstractions;

public interface IAuthorizer
{
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default);
    Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default);
    Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter RequestTypesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add request/result/context types and IAuthorizer"
```

---

### Task 4: Management interfaces

**Files:**
- Create: `src/Relkit.Abstractions/Management.cs`
- Test: `tests/Relkit.Abstractions.Tests/ManagementContractTests.cs`

**Interfaces:**
- Produces: `IRelationManager`, `TupleFilter`, `ChangeLogFilter`, `ChangeLogEntry`, `ISchemaManager`, `IStoreManager`, `ITenantManager`.
- Consumes: `Schema`, `SchemaValidationResult` (defined in Task 5; declared here as forward references — Task 5 must be present for the project to compile, so implement Task 5 before running this task's build).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/ManagementContractTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class ManagementContractTests
{
    [Fact]
    public void ChangeLogFilter_defaults_limit_to_100()
    {
        new ChangeLogFilter().Limit.ShouldBe(100);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ManagementContractTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Write the interfaces**

```csharp
// src/Relkit.Abstractions/Management.cs
namespace Relkit.Abstractions;

public sealed record TupleFilter(string? ObjectType = null, string? ObjectId = null, string? Relation = null,
    string? SubjectType = null, string? SubjectId = null);
public sealed record ChangeLogFilter(DateTimeOffset? Since = null, string? Actor = null, int Limit = 100);
public sealed record ChangeLogEntry(long Id, string Actor, string Operation, string Target,
    object? Before, object? After, DateTimeOffset OccurredAt);

public interface IRelationManager
{
    Task WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default);
    Task WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default);
}

public interface ISchemaManager
{
    SchemaValidationResult ValidateSchema(Schema schema);
    Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default);
    Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default);
}

public interface IStoreManager { Task CreateStoreAsync(string store, CancellationToken ct = default); }
public interface ITenantManager { Task CreateTenantAsync(TenantContext tenant, CancellationToken ct = default); }
```

- [ ] **Step 4: Run to verify pass** (after Task 5 types exist)

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ManagementContractTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add management interfaces to abstractions"
```

---

### Task 5: Schema AST and validation result

**Files:**
- Create: `src/Relkit.Abstractions/Schema.cs`
- Test: `tests/Relkit.Abstractions.Tests/SchemaAstTests.cs`

**Interfaces:**
- Produces: `Schema`, `EntityTypeDef`, `RelationDef`, `SubjectTypeRef`, `PermissionDef`, `PermExpr` and subtypes (`RelationRef`, `Union`, `Intersect`, `Exclude`, `Arrow`, `Conditioned`), `ConditionDef`, `ConditionParam`, `ConditionType`, `SchemaValidationResult`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/SchemaAstTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class SchemaAstTests
{
    [Fact]
    public void PermExpr_subtypes_are_pattern_matchable()
    {
        PermExpr expr = new Union(new RelationRef("medicator"), new Arrow("enclosure", "edit"));
        var label = expr switch
        {
            Union => "union",
            Arrow => "arrow",
            _ => "other"
        };
        label.ShouldBe("union");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter SchemaAstTests`
Expected: FAIL — types not defined.

- [ ] **Step 3: Write the AST**

```csharp
// src/Relkit.Abstractions/Schema.cs
namespace Relkit.Abstractions;

public sealed record Schema(string Version, IReadOnlyList<EntityTypeDef> Types, IReadOnlyList<ConditionDef> Conditions);

public sealed record EntityTypeDef(string Name, IReadOnlyList<RelationDef> Relations, IReadOnlyList<PermissionDef> Permissions);

public sealed record RelationDef(string Name, IReadOnlyList<SubjectTypeRef> AllowedSubjects);
public sealed record SubjectTypeRef(string Type, string? Relation = null, bool Wildcard = false);

public sealed record PermissionDef(string Name, PermExpr Expression);

public abstract record PermExpr;
public sealed record RelationRef(string Relation) : PermExpr;
public sealed record Union(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Intersect(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Exclude(PermExpr Left, PermExpr Right) : PermExpr;
public sealed record Arrow(string Relation, string Permission) : PermExpr;
public sealed record Conditioned(PermExpr Inner, string ConditionName) : PermExpr;

public sealed record ConditionDef(string Name, IReadOnlyList<ConditionParam> Parameters, ConditionExpr Body);
public sealed record ConditionParam(string Name, ConditionType Type);
public enum ConditionType { Bool, Int, Long, Double, String, Timestamp }

public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);
```

> `ConditionExpr` is the condition body AST; it is defined in plan `m0/06`. For this task, add a placeholder marker type so the schema compiles:

```csharp
// src/Relkit.Abstractions/ConditionExpr.cs
namespace Relkit.Abstractions;

/// <summary>Marker base for the condition-body AST; concrete nodes are added in m0/06.</summary>
public abstract record ConditionExpr;
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter SchemaAstTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add schema AST records to abstractions"
```

---

### Task 6: Storage provider interfaces, unit of work, cache, exceptions

**Files:**
- Create: `src/Relkit.Abstractions/Storage.cs`
- Create: `src/Relkit.Abstractions/Exceptions.cs`
- Test: `tests/Relkit.Abstractions.Tests/ExceptionsTests.cs`

**Interfaces:**
- Produces: `IRelationStore`, `ISchemaStore`, `IAttributeStore`, `IIndexStore`, `ICacheStore`, `CacheEntry`, `IChangeLogStore`, `IUnitOfWork`, `IUnitOfWorkFactory`, and the exception hierarchy.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/ExceptionsTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class ExceptionsTests
{
    [Fact]
    public void UnknownTypeException_carries_the_type()
    {
        var ex = new UnknownTypeException("dragon");
        ex.Message.ShouldContain("dragon");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ExceptionsTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Write storage interfaces and exceptions**

```csharp
// src/Relkit.Abstractions/Storage.cs
namespace Relkit.Abstractions;

public interface IUnitOfWork : IAsyncDisposable { Task CommitAsync(CancellationToken ct = default); }
public interface IUnitOfWorkFactory { Task<IUnitOfWork> BeginAsync(CancellationToken ct = default); }

public interface IRelationStore
{
    Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default);
    Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default);
    Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default);
}

public interface ISchemaStore
{
    Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default);
    Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default);
}

public interface IAttributeStore
{
    Task<IReadOnlyDictionary<string, object?>?> GetAsync(TenantContext t, EntityRef obj, CancellationToken ct = default);
    Task SetAsync(TenantContext t, EntityRef obj, IReadOnlyDictionary<string, object?> attrs, IUnitOfWork uow, CancellationToken ct = default);
}

public interface IIndexStore { }   // populated in M2

public sealed record CacheEntry(byte[] Value, long Epoch);
public interface ICacheStore
{
    Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, CacheEntry entry, TimeSpan ttl, CancellationToken ct = default);
    Task<long> GetEpochAsync(TenantContext t, CancellationToken ct = default);
    Task BumpEpochAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default);
}

public interface IChangeLogStore
{
    Task AppendAsync(TenantContext t, ChangeLogEntry entry, IUnitOfWork uow, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeLogEntry>> ReadAsync(TenantContext t, ChangeLogFilter filter, CancellationToken ct = default);
}
```

```csharp
// src/Relkit.Abstractions/Exceptions.cs
namespace Relkit.Abstractions;

public sealed class SchemaValidationException(IReadOnlyList<string> errors)
    : Exception("Schema validation failed: " + string.Join("; ", errors))
{ public IReadOnlyList<string> Errors { get; } = errors; }

public sealed class UnknownTypeException(string type) : Exception($"Unknown entity type '{type}'.")
{ public string Type { get; } = type; }

public sealed class UnknownRelationException(string type, string relation)
    : Exception($"Unknown relation '{relation}' on type '{type}'.")
{ public string Type { get; } = type; public string Relation { get; } = relation; }

public sealed class UnknownPermissionException(string type, string permission)
    : Exception($"Unknown permission '{permission}' on type '{type}'.")
{ public string Type { get; } = type; public string Permission { get; } = permission; }

public sealed class EvaluationLimitException(string detail) : Exception(detail);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter ExceptionsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add storage interfaces, cache, unit of work, and exceptions"
```

---

### Task 7: Diagnostics primitives

**Files:**
- Create: `src/Relkit.Abstractions/RelkitDiagnostics.cs`
- Test: `tests/Relkit.Abstractions.Tests/DiagnosticsTests.cs`

**Interfaces:**
- Produces: `RelkitDiagnostics.ActivitySource` (named `"Relkit"`) and `RelkitDiagnostics.Meter` (named `"Relkit"`) plus named instruments later plans record into.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Relkit.Abstractions.Tests/DiagnosticsTests.cs
using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Diagnostics_sources_are_named_relkit()
    {
        RelkitDiagnostics.ActivitySource.Name.ShouldBe("Relkit");
        RelkitDiagnostics.Meter.Name.ShouldBe("Relkit");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter DiagnosticsTests`
Expected: FAIL — type not defined.

- [ ] **Step 3: Write the diagnostics holder**

```csharp
// src/Relkit.Abstractions/RelkitDiagnostics.cs
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Relkit.Abstractions;

public static class RelkitDiagnostics
{
    public const string Name = "Relkit";
    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> CheckDuration =
        Meter.CreateHistogram<double>("relkit.check.duration", unit: "ms");
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("relkit.cache.hits");
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("relkit.cache.misses");
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Relkit.Abstractions.Tests --filter DiagnosticsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Relkit.Abstractions tests/Relkit.Abstractions.Tests
git commit -m "feat: add Relkit diagnostics primitives"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] Every type in `../README.md` → Canonical public contract exists with the exact signature, except `ConditionExpr` concrete nodes (owned by `m0/06`) and `IIndexStore` members (owned by M2).
- [ ] No logic beyond computed members lives in `Relkit.Abstractions`.

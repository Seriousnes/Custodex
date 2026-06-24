# M3/02 — REST Surface & OpenAPI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a minimal-API REST surface to `Relkit.Service` over the **same** engine the gRPC services use (`m3/01`): decision endpoints `POST /v1/check`, `/v1/batch-check`, `/v1/list-objects`, `/v1/list-subjects`, and management endpoints for tuples, attributes, change-log, schema, stores, and tenants. Document the whole surface with OpenAPI/Swagger (spec §11.4, §10.1). Tests exercise the endpoints through `WebApplicationFactory` against Testcontainers Postgres.

**Architecture:** REST is a second thin front door beside gRPC. The endpoints depend only on the public `Relkit.Abstractions` interfaces (`IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IStoreManager`, `ITenantManager`) resolved from DI — the same instances the gRPC services use. A set of plain request/response DTOs (`record`s) models the JSON bodies; a `RestMap` static class converts DTO ⇄ contract record once, mirroring `m3/01`'s `ProtoMap`. Heterogeneous attribute/parameter values (`object?`) are carried as `System.Text.Json` `JsonElement`/`Dictionary<string, object?>` so the JSON body round-trips naturally. Schema travels as the canonical JSON the `SchemaJson` serializer (`m3/01`) produces. Endpoints are grouped under a `/v1` route group; decision and management groups are tagged separately so `m3/03` can attach distinct authorization policies per group.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, ASP.NET Core minimal APIs, `Microsoft.AspNetCore.OpenApi`, `Swashbuckle.AspNetCore` (Swagger UI), the engine packages, xUnit, Shouldly, `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), `Testcontainers.PostgreSql`.

## Global Constraints

See `../README.md` → Global Constraints. Key points: `net10.0`; `Nullable`+`ImplicitUsings` enabled; `TreatWarningsAsErrors=true`; Apache-2.0 license metadata; all I/O methods are `async` with a trailing `CancellationToken ct = default`; identifiers are non-empty ordinal strings; id `"*"` is the wildcard. **No EF Core.** Depends on `m0/01` (the `Relkit.Abstractions` contract — type names verbatim), `m1/09` (`AddRelkit().UsePostgres().UseSchema()`, the managers), and `m3/01` (the `Relkit.Service` host project, `SchemaJson`). The REST surface adds **zero** new evaluation semantics; it composes the same `Relkit.Core` + Postgres engine.

> **Aspire alignment** (see `../README.md` → Aspire integration): the REST endpoints are added to the same Aspire `Relkit.Service` host (`AddServiceDefaults()` / `MapDefaultEndpoints()`). Reuse ServiceDefaults' `/health` and `/alive`; do not add bespoke health endpoints.

## Shared decisions (locked)

- **Same engine, same DI.** REST endpoints resolve the identical `IAuthorizer` + manager interfaces the gRPC services use; both front doors share one engine instance per request scope.
- **`/v1` route group, decision vs management tags.** Decision endpoints are tagged `decision`; management endpoints `management`. `m3/03` attaches a read policy to the `decision` group and a write policy to the `management` group via `.RequireAuthorization(...)` on the group builders.
- **DTOs are plain records.** Each endpoint takes/returns a record DTO; `RestMap` is the single DTO ⇄ contract-record converter. `JsonElement`/`Dictionary<string, object?>` carry attributes and condition parameters so arbitrary JSON values round-trip.
- **Tenancy explicit in M3/02 bodies.** Request DTOs carry `Store` + `Tenant` (decision/relation endpoints) or `Store` (schema endpoints), building `TenantContext`. `m3/03` moves resolution to headers/claims; here the bodies model `TenantContext` directly so the surface is complete and testable before auth.
- **Schema as canonical JSON.** Schema endpoints accept/return the `SchemaJson` (`m3/01`) canonical serialization; `m3/05`'s DSL parser adds a text form later.
- **Deny is `200 OK` with `allowed:false`; errors are HTTP errors.** Per spec §10.3, allow/deny is a return value. A `Check` that denies returns `200` with `{"allowed":false}`. A malformed schema returns `400`; an unknown type/relation/permission surfaces as `400` via the typed exceptions; default-deny never throws.

---

### Task 1: Add OpenAPI/Swagger and the `/v1` route groups

**Files:**
- Modify: `src/Relkit.Service/Relkit.Service.csproj`
- Modify: `src/Relkit.Service/Program.cs`
- Create: `src/Relkit.Service/Rest/RestEndpoints.cs`
- Test: `tests/Relkit.Service.Tests/OpenApiTests.cs`

**Interfaces:**
- Produces: `RestEndpoints.MapRelkitRest(this WebApplication app)` mapping a `/v1` group with `decision` and `management` subgroups (endpoints added in later tasks); Swagger UI at `/swagger` and the OpenAPI document at `/swagger/v1/swagger.json`.
- Consumes: the `Relkit.Service` host (`m3/01`), `Swashbuckle.AspNetCore`.

- [ ] **Step 1: Add the OpenAPI packages**

Run:
```bash
dotnet add src/Relkit.Service package Microsoft.AspNetCore.OpenApi
dotnet add src/Relkit.Service package Swashbuckle.AspNetCore
```

- [ ] **Step 2: Register Swagger and map the route groups in `Program.cs`**

Add the service registrations (after `builder.Services.AddGrpc();`):

```csharp
// src/Relkit.Service/Program.cs — add with the other service registrations
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Relkit Authorization API",
        Version = "v1",
        Description = "ReBAC + ABAC authorization: Check, BatchCheck, ListObjects, ListSubjects, and management.",
    });
});
```

Add the middleware + endpoint mapping (after `var app = builder.Build();` and the startup migration block, before `app.Run();`):

```csharp
// src/Relkit.Service/Program.cs — add after the gRPC service mappings, before app.Run();
using Relkit.Service.Rest;   // add to the using block at the top

app.UseSwagger();
app.UseSwaggerUI();
app.MapRelkitRest();
```

- [ ] **Step 3: Create the route-group skeleton**

```csharp
// src/Relkit.Service/Rest/RestEndpoints.cs
namespace Relkit.Service.Rest;

/// <summary>
/// Minimal-API REST surface over the same engine the gRPC services use (m3/01). Decision and
/// management endpoints are grouped and tagged so m3/03 can attach distinct authorization policies.
/// </summary>
public static partial class RestEndpoints
{
    public static WebApplication MapRelkitRest(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        var decision = v1.MapGroup("").WithTags("decision");
        var management = v1.MapGroup("").WithTags("management");

        MapDecisionEndpoints(decision);
        MapManagementEndpoints(management);

        return app;
    }

    // Implemented in later tasks (partial methods filled by Task 3 and Task 5).
    static partial void MapDecisionEndpoints(RouteGroupBuilder group);
    static partial void MapManagementEndpoints(RouteGroupBuilder group);
}
```

> **Partial methods keep the skeleton green now.** Declaring `MapDecisionEndpoints`/`MapManagementEndpoints` as partial methods with no body (added in Tasks 3 and 5) lets the route-group skeleton compile and serve Swagger before the endpoints exist. C# compiles an unimplemented partial method to a no-op.

- [ ] **Step 4: Write the failing OpenAPI test**

```csharp
// tests/Relkit.Service.Tests/OpenApiTests.cs
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

[Collection("service")]
public class OpenApiTests(PostgresFixture fx)
{
    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Relkit:ConnectionString", fx.ConnectionString));

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Relkit Authorization API");
    }
}
```

- [ ] **Step 5: Run to verify**

Run: `dotnet test tests/Relkit.Service.Tests --filter OpenApiTests`
Expected: PASS. The OpenAPI document is generated and names the API. (At this point it has no `/v1` paths yet; later tasks add them and the document grows.)

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: add OpenAPI/Swagger and REST route groups to the service"
```

---

### Task 2: Decision DTOs and `RestMap`

**Files:**
- Create: `src/Relkit.Service/Rest/DecisionDtos.cs`
- Create: `src/Relkit.Service/Rest/RestMap.cs`
- Test: `tests/Relkit.Service.Tests/RestMapTests.cs`

**Interfaces:**
- Produces: decision DTOs (`EntityRefDto`, `SubjectRefDto`, `RequestContextDto`, `CheckRequestDto`, `CheckResponseDto`, `ExplainNodeDto`, `BatchCheckRequestDto`, `CheckItemDto`, `ListObjectsRequestDto`/`ListObjectsResponseDto`, `ListSubjectsRequestDto`/`ListSubjectsResponseDto`) and `RestMap` with the DTO ⇄ contract-record converters.
- Consumes: `EntityRef`, `SubjectRef`, `RequestContext`, `ExplainNode`, the request/result records (`Relkit.Abstractions`).

> **Same null-`Relation` rule as gRPC.** `SubjectRefDto.Relation` is `string?`; `null`/absent ⇒ plain subject, non-null ⇒ subject-set. `RequestContextDto.Attributes` is `Dictionary<string, object?>?` (JSON object) carrying ad-hoc context values; `null` ⇒ empty. `RestMap` centralises the mapping so it is tested in isolation like `ProtoMap`.

- [ ] **Step 1: Write the decision DTOs**

```csharp
// src/Relkit.Service/Rest/DecisionDtos.cs
namespace Relkit.Service.Rest;

public sealed record EntityRefDto(string Type, string Id);
public sealed record SubjectRefDto(string Type, string Id, string? Relation = null);
public sealed record RequestContextDto(
    DateTimeOffset? Now, SubjectRefDto Subject, Dictionary<string, object?>? Attributes = null);

public sealed record CheckRequestDto(
    string Store, string Tenant, EntityRefDto Object, string Permission, SubjectRefDto Subject,
    RequestContextDto Context, bool Explain = false);

public sealed record ExplainNodeDto(string Description, bool Allowed, IReadOnlyList<ExplainNodeDto> Children);
public sealed record CheckResponseDto(bool Allowed, ExplainNodeDto? Explain = null);

public sealed record CheckItemDto(EntityRefDto Object, string Permission, SubjectRefDto Subject);
public sealed record BatchCheckRequestDto(
    string Store, string Tenant, IReadOnlyList<CheckItemDto> Items, RequestContextDto Context);
public sealed record BatchCheckResponseDto(IReadOnlyList<CheckResponseDto> Results);

public sealed record ListObjectsRequestDto(
    string Store, string Tenant, SubjectRefDto Subject, string ObjectType, string Permission,
    RequestContextDto Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListObjectsResponseDto(IReadOnlyList<string> ObjectIds, string? ContinuationToken);

public sealed record ListSubjectsRequestDto(
    string Store, string Tenant, EntityRefDto Object, string Permission,
    RequestContextDto Context, int PageSize = 100, string? ContinuationToken = null);
public sealed record ListSubjectsResponseDto(IReadOnlyList<SubjectRefDto> Subjects, string? ContinuationToken);
```

- [ ] **Step 2: Write the failing `RestMap` tests**

```csharp
// tests/Relkit.Service.Tests/RestMapTests.cs
using Relkit.Abstractions;
using Relkit.Service.Rest;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

public class RestMapTests
{
    [Fact]
    public void EntityRef_round_trips_including_wildcard()
    {
        var dto = new EntityRefDto("user", "*");
        var domain = RestMap.ToEntityRef(dto);
        domain.IsWildcard.ShouldBeTrue();
        RestMap.FromEntityRef(domain).ShouldBe(dto);
    }

    [Fact]
    public void SubjectRef_null_relation_is_plain_subject_and_subject_set_round_trips()
    {
        RestMap.ToSubjectRef(new SubjectRefDto("user", "alice")).IsSubjectSet.ShouldBeFalse();
        var set = RestMap.ToSubjectRef(new SubjectRefDto("group", "vets", "member"));
        set.IsSubjectSet.ShouldBeTrue();
        RestMap.FromSubjectRef(set).Relation.ShouldBe("member");
    }

    [Fact]
    public void RequestContext_maps_now_subject_and_attributes_with_default_now()
    {
        var dto = new RequestContextDto(null, new SubjectRefDto("user", "alice"),
            new Dictionary<string, object?> { ["ip"] = "10.0.0.1" });
        var domain = RestMap.ToRequestContext(dto);
        domain.Subject.Id.ShouldBe("alice");
        domain.Attributes["ip"].ShouldBe("10.0.0.1");
    }

    [Fact]
    public void ExplainNode_maps_recursively()
    {
        var node = new ExplainNode("union", true,
            [new ExplainNode("relation editor", true, [])]);
        var dto = RestMap.FromExplainNode(node);
        dto.Description.ShouldBe("union");
        dto.Children.Count.ShouldBe(1);
        dto.Children[0].Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Tenant_context_is_built_from_store_and_tenant()
    {
        RestMap.Tenant("zoo", "t1").ShouldBe(new TenantContext("zoo", "t1"));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Service.Tests --filter RestMapTests`
Expected: FAIL — `RestMap` does not exist.

- [ ] **Step 4: Implement `RestMap`**

```csharp
// src/Relkit.Service/Rest/RestMap.cs
using Relkit.Abstractions;

namespace Relkit.Service.Rest;

/// <summary>DTO ⇄ contract-record conversion for the REST surface. The single home for the mapping.</summary>
public static class RestMap
{
    public static TenantContext Tenant(string store, string tenant) => new(store, tenant);

    public static EntityRef ToEntityRef(EntityRefDto e) => new(e.Type, e.Id);
    public static EntityRefDto FromEntityRef(EntityRef e) => new(e.Type, e.Id);

    public static SubjectRef ToSubjectRef(SubjectRefDto s) =>
        new(s.Type, s.Id, string.IsNullOrEmpty(s.Relation) ? null : s.Relation);

    public static SubjectRefDto FromSubjectRef(SubjectRef s) => new(s.Type, s.Id, s.Relation);

    public static RequestContext ToRequestContext(RequestContextDto c) => new(
        c.Now ?? default,
        ToSubjectRef(c.Subject),
        c.Attributes is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(c.Attributes));

    public static ExplainNodeDto FromExplainNode(ExplainNode n) =>
        new(n.Description, n.Allowed, n.Children.Select(FromExplainNode).ToList());
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Service.Tests --filter RestMapTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: add decision DTOs and RestMap conversion"
```

---

### Task 3: Decision REST endpoints

**Files:**
- Create: `src/Relkit.Service/Rest/DecisionEndpoints.cs`
- Test: `tests/Relkit.Service.Tests/DecisionRestTests.cs`

**Interfaces:**
- Produces: `RestEndpoints.MapDecisionEndpoints` (the partial method body) mapping `POST /v1/check`, `/v1/batch-check`, `/v1/list-objects`, `/v1/list-subjects`, each delegating to `IAuthorizer` via `RestMap`.
- Consumes: `IAuthorizer` (`Relkit.Abstractions`), `RestMap` + the decision DTOs (Task 2).

> **Default-deny is `200 OK`.** A denied check returns `200` with `{"allowed":false}` per spec §10.3. The endpoints inject `IAuthorizer` per call. `Explain` is included only when requested.

- [ ] **Step 1: Implement the decision endpoints**

```csharp
// src/Relkit.Service/Rest/DecisionEndpoints.cs
using Relkit.Abstractions;

namespace Relkit.Service.Rest;

public static partial class RestEndpoints
{
    static partial void MapDecisionEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/check", async (CheckRequestDto dto, IAuthorizer authorizer, CancellationToken ct) =>
        {
            var result = await authorizer.CheckAsync(new CheckRequest(
                RestMap.Tenant(dto.Store, dto.Tenant), RestMap.ToEntityRef(dto.Object), dto.Permission,
                RestMap.ToSubjectRef(dto.Subject), RestMap.ToRequestContext(dto.Context), dto.Explain), ct);
            return Results.Ok(new CheckResponseDto(result.Allowed,
                result.Explain is null ? null : RestMap.FromExplainNode(result.Explain)));
        })
        .WithName("Check").WithSummary("Point check: may a subject act on an object?");

        group.MapPost("/batch-check", async (BatchCheckRequestDto dto, IAuthorizer authorizer, CancellationToken ct) =>
        {
            var items = dto.Items
                .Select(i => new CheckItem(RestMap.ToEntityRef(i.Object), i.Permission, RestMap.ToSubjectRef(i.Subject)))
                .ToList();
            var results = await authorizer.BatchCheckAsync(new BatchCheckRequest(
                RestMap.Tenant(dto.Store, dto.Tenant), items, RestMap.ToRequestContext(dto.Context)), ct);
            return Results.Ok(new BatchCheckResponseDto(
                results.Select(r => new CheckResponseDto(r.Allowed)).ToList()));
        })
        .WithName("BatchCheck").WithSummary("Many checks sharing one request context.");

        group.MapPost("/list-objects", async (ListObjectsRequestDto dto, IAuthorizer authorizer, CancellationToken ct) =>
        {
            var result = await authorizer.ListObjectsAsync(new ListObjectsRequest(
                RestMap.Tenant(dto.Store, dto.Tenant), RestMap.ToSubjectRef(dto.Subject), dto.ObjectType,
                dto.Permission, RestMap.ToRequestContext(dto.Context),
                dto.PageSize <= 0 ? 100 : dto.PageSize, dto.ContinuationToken), ct);
            return Results.Ok(new ListObjectsResponseDto(result.ObjectIds, result.ContinuationToken));
        })
        .WithName("ListObjects").WithSummary("Objects of a type a subject may act on (for list filtering).");

        group.MapPost("/list-subjects", async (ListSubjectsRequestDto dto, IAuthorizer authorizer, CancellationToken ct) =>
        {
            var result = await authorizer.ListSubjectsAsync(new ListSubjectsRequest(
                RestMap.Tenant(dto.Store, dto.Tenant), RestMap.ToEntityRef(dto.Object), dto.Permission,
                RestMap.ToRequestContext(dto.Context), dto.PageSize <= 0 ? 100 : dto.PageSize, dto.ContinuationToken), ct);
            return Results.Ok(new ListSubjectsResponseDto(
                result.Subjects.Select(RestMap.FromSubjectRef).ToList(), result.ContinuationToken));
        })
        .WithName("ListSubjects").WithSummary("Subjects who may act on an object.");
    }
}
```

- [ ] **Step 2: Write the failing decision REST tests**

```csharp
// tests/Relkit.Service.Tests/DecisionRestTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Service.Rest;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

[Collection("service")]
public class DecisionRestTests(PostgresFixture fx)
{
    private const string Store = "rest-dec";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Relkit:ConnectionString", fx.ConnectionString));

    private static async Task SeedAsync(WebApplicationFactory<Program> factory, string tenant)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IStoreManager>().CreateStoreAsync(Store);
        await sp.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(Store, tenant));
        await sp.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(Store, Store));

        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("species", t => t
                .Relation("editor", s => s.User().SubjectSet("group", "member"))
                .Permission("edit", p => p.Relation("editor")))
            .Build();
        await sp.GetRequiredService<ISchemaManager>().SetActiveSchemaAsync(Store, schema);

        var t = new TenantContext(Store, tenant);
        await sp.GetRequiredService<IRelationManager>().WriteTuplesAsync(t, "zoo-admin",
        [
            new RelationTuple(new EntityRef("species", "kangaroo"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("species", "wallaby"), "editor", new SubjectRef("group", "macropods", "member")),
            new RelationTuple(new EntityRef("group", "macropods"), "member", new SubjectRef("user", "alice")),
        ]);
    }

    private static CheckRequestDto CheckDto(string tenant, string objectId, string subjectId) => new(
        Store, tenant, new EntityRefDto("species", objectId), "edit", new SubjectRefDto("user", subjectId),
        new RequestContextDto(null, new SubjectRefDto("user", subjectId)));

    [Fact]
    public async Task Check_returns_allowed_true_for_a_granted_subject()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-check");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/check", CheckDto("t-check", "kangaroo", "alice"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CheckResponseDto>();
        body!.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_denied_returns_200_with_allowed_false()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-deny");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/check", CheckDto("t-deny", "kangaroo", "bob"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CheckResponseDto>())!.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Check_with_explain_returns_a_trace()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-explain");
        using var client = factory.CreateClient();

        var dto = CheckDto("t-explain", "kangaroo", "alice") with { Explain = true };
        var body = await (await client.PostAsJsonAsync("/v1/check", dto)).Content.ReadFromJsonAsync<CheckResponseDto>();

        body!.Allowed.ShouldBeTrue();
        body.Explain.ShouldNotBeNull();
    }

    [Fact]
    public async Task ListObjects_returns_the_editable_species()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-list");
        using var client = factory.CreateClient();

        var dto = new ListObjectsRequestDto(Store, "t-list", new SubjectRefDto("user", "alice"),
            "species", "edit", new RequestContextDto(null, new SubjectRefDto("user", "alice")));
        var body = await (await client.PostAsJsonAsync("/v1/list-objects", dto))
            .Content.ReadFromJsonAsync<ListObjectsResponseDto>();

        body!.ObjectIds.OrderBy(x => x).ShouldBe(["kangaroo", "wallaby"]);
    }

    [Fact]
    public async Task BatchCheck_returns_aligned_results()
    {
        using var factory = CreateFactory();
        await SeedAsync(factory, "t-batch");
        using var client = factory.CreateClient();

        var dto = new BatchCheckRequestDto(Store, "t-batch",
        [
            new CheckItemDto(new EntityRefDto("species", "kangaroo"), "edit", new SubjectRefDto("user", "alice")),
            new CheckItemDto(new EntityRefDto("species", "kangaroo"), "edit", new SubjectRefDto("user", "bob")),
        ], new RequestContextDto(null, new SubjectRefDto("user", "alice")));

        var body = await (await client.PostAsJsonAsync("/v1/batch-check", dto))
            .Content.ReadFromJsonAsync<BatchCheckResponseDto>();

        body!.Results.Count.ShouldBe(2);
        body.Results[0].Allowed.ShouldBeTrue();
        body.Results[1].Allowed.ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test tests/Relkit.Service.Tests --filter DecisionRestTests`
Expected: PASS (5 tests). Check/BatchCheck/ListObjects answer correctly over REST; a denial is `200` with `allowed:false`; `Explain` is returned when requested.

- [ ] **Step 4: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: add decision REST endpoints over IAuthorizer"
```

---

### Task 4: Management DTOs

**Files:**
- Create: `src/Relkit.Service/Rest/ManagementDtos.cs`
- Test: `tests/Relkit.Service.Tests/ManagementDtoTests.cs`

**Interfaces:**
- Produces: management DTOs — `RelationTupleDto`, `ConditionRefDto`, `WriteTuplesRequestDto`, `WriteAttributesRequestDto`, `ReadTuplesRequestDto`/`ReadTuplesResponseDto`, `ChangeLogEntryDto`/`ReadChangeLogRequestDto`/`ReadChangeLogResponseDto`, `ValidateSchemaRequestDto`/`ValidateSchemaResponseDto`, `SetActiveSchemaRequestDto`, `GetActiveSchemaResponseDto`, `CreateStoreRequestDto`, `CreateTenantRequestDto` — plus the `RestMap` tuple converters.
- Consumes: `RelationTuple`, `ConditionRef`, `TupleFilter`, `ChangeLogEntry`, `ChangeLogFilter` (`Relkit.Abstractions`).

> **Tuple ⇄ DTO and condition mapping.** `RelationTupleDto` mirrors `RelationTuple(Object, Relation, Subject, Condition?)`. `ConditionRefDto(Name, Parameters)` carries `Dictionary<string, object?>` parameters. Schema bodies travel as the `SchemaJson` canonical string (`m3/01`).

- [ ] **Step 1: Write the management DTOs**

```csharp
// src/Relkit.Service/Rest/ManagementDtos.cs
namespace Relkit.Service.Rest;

public sealed record ConditionRefDto(string Name, Dictionary<string, object?> Parameters);
public sealed record RelationTupleDto(EntityRefDto Object, string Relation, SubjectRefDto Subject, ConditionRefDto? Condition = null);

public sealed record WriteTuplesRequestDto(string Store, string Tenant, string Actor, IReadOnlyList<RelationTupleDto> Tuples);
public sealed record WriteAttributesRequestDto(string Store, string Tenant, string Actor, EntityRefDto Object, Dictionary<string, object?> Attributes);

public sealed record ReadTuplesRequestDto(
    string Store, string Tenant, string? ObjectType = null, string? ObjectId = null, string? Relation = null,
    string? SubjectType = null, string? SubjectId = null);
public sealed record ReadTuplesResponseDto(IReadOnlyList<RelationTupleDto> Tuples);

public sealed record ChangeLogEntryDto(long Id, string Actor, string Operation, string Target, object? Before, object? After, DateTimeOffset OccurredAt);
public sealed record ReadChangeLogRequestDto(string Store, string Tenant, DateTimeOffset? Since = null, string? Actor = null, int Limit = 100);
public sealed record ReadChangeLogResponseDto(IReadOnlyList<ChangeLogEntryDto> Entries);

public sealed record ValidateSchemaRequestDto(string SchemaJson);
public sealed record ValidateSchemaResponseDto(bool IsValid, IReadOnlyList<string> Errors);
public sealed record SetActiveSchemaRequestDto(string Store, string SchemaJson);
public sealed record GetActiveSchemaResponseDto(bool Found, string? SchemaJson);

public sealed record CreateStoreRequestDto(string Store);
public sealed record CreateTenantRequestDto(string Store, string Tenant);
```

- [ ] **Step 2: Write the failing tuple-mapping test**

```csharp
// tests/Relkit.Service.Tests/ManagementDtoTests.cs
using Relkit.Abstractions;
using Relkit.Service.Rest;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

public class ManagementDtoTests
{
    [Fact]
    public void Tuple_dto_round_trips_with_a_condition()
    {
        var dto = new RelationTupleDto(
            new EntityRefDto("category", "drugs"), "dispenser",
            new SubjectRefDto("group", "vets", "member"),
            new ConditionRefDto("within_hours", new Dictionary<string, object?> { ["start"] = 8L, ["end"] = 18L }));

        var domain = RestMap.ToTuple(dto);
        domain.Object.Id.ShouldBe("drugs");
        domain.Subject.IsSubjectSet.ShouldBeTrue();
        domain.Condition!.Name.ShouldBe("within_hours");

        var back = RestMap.FromTuple(domain);
        back.Condition!.Parameters["end"].ShouldBe(18L);
    }

    [Fact]
    public void Tuple_dto_without_condition_maps_to_null_condition()
    {
        var dto = new RelationTupleDto(new EntityRefDto("animal", "EL-001"), "can_manage",
            new SubjectRefDto("user", "carol"));
        RestMap.ToTuple(dto).Condition.ShouldBeNull();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Relkit.Service.Tests --filter ManagementDtoTests`
Expected: FAIL — `RestMap.ToTuple`/`FromTuple` do not exist.

- [ ] **Step 4: Add the tuple converters to `RestMap`**

```csharp
// Add to src/Relkit.Service/Rest/RestMap.cs (alongside the existing members)

    public static RelationTuple ToTuple(RelationTupleDto t) => new(
        ToEntityRef(t.Object), t.Relation, ToSubjectRef(t.Subject),
        t.Condition is null ? null : new ConditionRef(t.Condition.Name,
            new Dictionary<string, object?>(t.Condition.Parameters)));

    public static RelationTupleDto FromTuple(RelationTuple t) => new(
        FromEntityRef(t.Object), t.Relation, FromSubjectRef(t.Subject),
        t.Condition is null ? null
            : new ConditionRefDto(t.Condition.Name, new Dictionary<string, object?>(t.Condition.Parameters)));
```

> Add `using Relkit.Abstractions;` is already present at the top of `RestMap.cs`.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Relkit.Service.Tests --filter ManagementDtoTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: add management DTOs and tuple converters"
```

---

### Task 5: Management REST endpoints

**Files:**
- Create: `src/Relkit.Service/Rest/ManagementEndpoints.cs`
- Test: `tests/Relkit.Service.Tests/ManagementRestTests.cs`

**Interfaces:**
- Produces: `RestEndpoints.MapManagementEndpoints` (the partial method body) mapping `POST /v1/tuples`, `DELETE /v1/tuples`, `PUT /v1/attributes`, `POST /v1/tuples/query`, `POST /v1/change-log/query`, `POST /v1/schema/validate`, `PUT /v1/schema/{store}`, `GET /v1/schema/{store}`, `POST /v1/stores`, `POST /v1/tenants`. Each delegates to a manager interface.
- Consumes: `IRelationManager`/`ISchemaManager`/`IStoreManager`/`ITenantManager` (`Relkit.Abstractions`), `RestMap` + management DTOs (Task 4), `SchemaJson` (`m3/01`).

> **Invalid schema ⇒ `400`.** `PUT /v1/schema/{store}` catches `SchemaValidationException` from `RelkitSchemaManager` and returns `Results.ValidationProblem` with the errors (spec §10.3: a malformed schema is an error, not a deny). `DELETE /v1/tuples` takes the same body as the write to identify tuples to remove.

- [ ] **Step 1: Implement the management endpoints**

```csharp
// src/Relkit.Service/Rest/ManagementEndpoints.cs
using Relkit.Abstractions;
using Relkit.Service.Mapping;

namespace Relkit.Service.Rest;

public static partial class RestEndpoints
{
    static partial void MapManagementEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/tuples", async (WriteTuplesRequestDto dto, IRelationManager relations, CancellationToken ct) =>
        {
            var tuples = dto.Tuples.Select(RestMap.ToTuple).ToList();
            await relations.WriteTuplesAsync(RestMap.Tenant(dto.Store, dto.Tenant), dto.Actor, tuples, ct);
            return Results.Ok(new { count = tuples.Count });
        }).WithName("WriteTuples").WithSummary("Write relation tuples (audited).");

        group.MapMethods("/tuples", ["DELETE"], async (WriteTuplesRequestDto dto, IRelationManager relations, CancellationToken ct) =>
        {
            var tuples = dto.Tuples.Select(RestMap.ToTuple).ToList();
            await relations.DeleteTuplesAsync(RestMap.Tenant(dto.Store, dto.Tenant), dto.Actor, tuples, ct);
            return Results.Ok(new { count = tuples.Count });
        }).WithName("DeleteTuples").WithSummary("Delete relation tuples (audited).");

        group.MapPut("/attributes", async (WriteAttributesRequestDto dto, IRelationManager relations, CancellationToken ct) =>
        {
            await relations.WriteAttributesAsync(RestMap.Tenant(dto.Store, dto.Tenant), dto.Actor,
                RestMap.ToEntityRef(dto.Object), dto.Attributes, ct);
            return Results.NoContent();
        }).WithName("WriteAttributes").WithSummary("Sync authz-relevant resource attributes.");

        group.MapPost("/tuples/query", async (ReadTuplesRequestDto dto, IRelationManager relations, CancellationToken ct) =>
        {
            var filter = new TupleFilter(dto.ObjectType, dto.ObjectId, dto.Relation, dto.SubjectType, dto.SubjectId);
            var tuples = await relations.ReadTuplesAsync(RestMap.Tenant(dto.Store, dto.Tenant), filter, ct);
            return Results.Ok(new ReadTuplesResponseDto(tuples.Select(RestMap.FromTuple).ToList()));
        }).WithName("ReadTuples").WithSummary("Admin/audit: read tuples by filter.");

        group.MapPost("/change-log/query", async (ReadChangeLogRequestDto dto, IRelationManager relations, CancellationToken ct) =>
        {
            var filter = new ChangeLogFilter(dto.Since, dto.Actor, dto.Limit <= 0 ? 100 : dto.Limit);
            var entries = await relations.ReadChangeLogAsync(RestMap.Tenant(dto.Store, dto.Tenant), filter, ct);
            return Results.Ok(new ReadChangeLogResponseDto(entries
                .Select(e => new ChangeLogEntryDto(e.Id, e.Actor, e.Operation, e.Target, e.Before, e.After, e.OccurredAt))
                .ToList()));
        }).WithName("ReadChangeLog").WithSummary("Config-change audit history.");

        group.MapPost("/schema/validate", (ValidateSchemaRequestDto dto, ISchemaManager schemas) =>
        {
            var schema = SchemaJson.Deserialize(dto.SchemaJson);
            var result = schemas.ValidateSchema(schema);
            return Results.Ok(new ValidateSchemaResponseDto(result.IsValid, result.Errors));
        }).WithName("ValidateSchema").WithSummary("Validate a schema without activating it.");

        group.MapPut("/schema/{store}", async (string store, SetActiveSchemaRequestDto dto, ISchemaManager schemas, CancellationToken ct) =>
        {
            var schema = SchemaJson.Deserialize(dto.SchemaJson);
            try
            {
                await schemas.SetActiveSchemaAsync(store, schema, ct);
            }
            catch (SchemaValidationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["schema"] = ex.Errors.ToArray() });
            }
            return Results.NoContent();
        }).WithName("SetActiveSchema").WithSummary("Validate and activate the store's schema.");

        group.MapGet("/schema/{store}", async (string store, ISchemaManager schemas, CancellationToken ct) =>
        {
            var schema = await schemas.GetActiveSchemaAsync(store, ct);
            return Results.Ok(schema is null
                ? new GetActiveSchemaResponseDto(false, null)
                : new GetActiveSchemaResponseDto(true, SchemaJson.Serialize(schema)));
        }).WithName("GetActiveSchema").WithSummary("Fetch the store's active schema.");

        group.MapPost("/stores", async (CreateStoreRequestDto dto, IStoreManager stores, CancellationToken ct) =>
        {
            await stores.CreateStoreAsync(dto.Store, ct);
            return Results.Created($"/v1/stores/{dto.Store}", new { store = dto.Store });
        }).WithName("CreateStore").WithSummary("Provision a store.");

        group.MapPost("/tenants", async (CreateTenantRequestDto dto, ITenantManager tenants, CancellationToken ct) =>
        {
            await tenants.CreateTenantAsync(new TenantContext(dto.Store, dto.Tenant), ct);
            return Results.Created($"/v1/tenants/{dto.Store}/{dto.Tenant}", new { dto.Store, dto.Tenant });
        }).WithName("CreateTenant").WithSummary("Provision a tenant in a store.");
    }
}
```

- [ ] **Step 2: Write the failing management REST tests**

```csharp
// tests/Relkit.Service.Tests/ManagementRestTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Relkit.Core;
using Relkit.Service.Mapping;
using Relkit.Service.Rest;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

[Collection("service")]
public class ManagementRestTests(PostgresFixture fx)
{
    private const string Store = "rest-mgmt";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Relkit:ConnectionString", fx.ConnectionString));

    private static string SchemaJsonText() => SchemaJson.Serialize(new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("category", t => t
            .Relation("dispenser", s => s.User().SubjectSet("group", "member"))
            .Permission("record_dispense", p => p.Relation("dispenser")))
        .Build());

    [Fact]
    public async Task Provision_set_schema_write_and_read_tuple_round_trips_over_rest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string tenant = "t1";

        (await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(Store)))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(Store, tenant)))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(Store, Store)); // audit bookkeeping tenant

        (await client.PutAsJsonAsync($"/v1/schema/{Store}", new SetActiveSchemaRequestDto(Store, SchemaJsonText())))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var fetched = await (await client.GetAsync($"/v1/schema/{Store}"))
            .Content.ReadFromJsonAsync<GetActiveSchemaResponseDto>();
        fetched!.Found.ShouldBeTrue();

        var write = new WriteTuplesRequestDto(Store, tenant, "admin",
        [
            new RelationTupleDto(new EntityRefDto("category", "drugs"), "dispenser",
                new SubjectRefDto("group", "vets", "member")),
        ]);
        (await client.PostAsJsonAsync("/v1/tuples", write)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await (await client.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(Store, tenant, ObjectType: "category")))
            .Content.ReadFromJsonAsync<ReadTuplesResponseDto>();
        read!.Tuples.ShouldContain(t => t.Object.Id == "drugs");

        var log = await (await client.PostAsJsonAsync("/v1/change-log/query",
            new ReadChangeLogRequestDto(Store, tenant)))
            .Content.ReadFromJsonAsync<ReadChangeLogResponseDto>();
        log!.Entries.ShouldContain(e => e.Actor == "admin" && e.Operation == "write");
    }

    [Fact]
    public async Task SetActiveSchema_with_invalid_schema_returns_400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string store = "rest-mgmt-bad";

        await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(store));
        await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(store, store));

        // doc.view references a relation that does not exist => validation fails.
        var bad = SchemaJson.Serialize(new Relkit.Abstractions.Schema("v1",
            [new Relkit.Abstractions.EntityTypeDef("doc", [],
                [new Relkit.Abstractions.PermissionDef("view", new Relkit.Abstractions.RelationRef("ghost"))])], []));

        var response = await client.PutAsJsonAsync($"/v1/schema/{store}", new SetActiveSchemaRequestDto(store, bad));
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTuples_removes_a_written_tuple()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string tenant = "t-del";

        await client.PostAsJsonAsync("/v1/stores", new CreateStoreRequestDto(Store));
        await client.PostAsJsonAsync("/v1/tenants", new CreateTenantRequestDto(Store, tenant));

        var tuple = new RelationTupleDto(new EntityRefDto("category", "drugs"), "dispenser",
            new SubjectRefDto("group", "vets", "member"));
        await client.PostAsJsonAsync("/v1/tuples", new WriteTuplesRequestDto(Store, tenant, "admin", [tuple]));

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/tuples")
        {
            Content = JsonContent.Create(new WriteTuplesRequestDto(Store, tenant, "admin", [tuple])),
        };
        (await client.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = await (await client.PostAsJsonAsync("/v1/tuples/query",
            new ReadTuplesRequestDto(Store, tenant, ObjectType: "category")))
            .Content.ReadFromJsonAsync<ReadTuplesResponseDto>();
        read!.Tuples.ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run to verify**

Run: `dotnet test tests/Relkit.Service.Tests --filter ManagementRestTests`
Expected: PASS (3 tests). Provisioning, schema activation, audited tuple write/read/delete round-trip over REST; an invalid schema returns `400`.

- [ ] **Step 4: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: add management REST endpoints over the manager interfaces"
```

---

### Task 6: Surface the typed engine exceptions as HTTP problems

**Files:**
- Create: `src/Relkit.Service/Rest/ExceptionHandling.cs`
- Modify: `src/Relkit.Service/Program.cs`
- Test: `tests/Relkit.Service.Tests/RestErrorHandlingTests.cs`

**Interfaces:**
- Produces: `RestEndpoints.UseRelkitProblemDetails(this WebApplication app)` registering an exception handler that maps the engine's typed exceptions to `ProblemDetails`: `UnknownTypeException`/`UnknownRelationException`/`UnknownPermissionException` → `400`, `SchemaValidationException` → `400`, `EvaluationLimitException` → `422`. Default-deny still returns `200`.
- Consumes: the engine exceptions (`m0/01`), `Microsoft.AspNetCore.Diagnostics`.

> **Caller-bug exceptions are surfaced loudly (spec §10.3).** A `Check` against a permission the schema does not define throws `UnknownPermissionException`; the handler returns a `400` problem rather than a silent deny, so a misconfigured caller learns immediately. Allow/deny remains a `200` body.

- [ ] **Step 1: Implement the exception handler**

```csharp
// src/Relkit.Service/Rest/ExceptionHandling.cs
using Microsoft.AspNetCore.Diagnostics;
using Relkit.Abstractions;

namespace Relkit.Service.Rest;

public static partial class RestEndpoints
{
    /// <summary>Maps the engine's typed exceptions (spec §10.3) to ProblemDetails responses.</summary>
    public static WebApplication UseRelkitProblemDetails(this WebApplication app)
    {
        app.UseExceptionHandler(handler => handler.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, title) = ex switch
            {
                UnknownTypeException => (StatusCodes.Status400BadRequest, "Unknown entity type"),
                UnknownRelationException => (StatusCodes.Status400BadRequest, "Unknown relation"),
                UnknownPermissionException => (StatusCodes.Status400BadRequest, "Unknown permission"),
                SchemaValidationException => (StatusCodes.Status400BadRequest, "Invalid schema"),
                EvaluationLimitException => (StatusCodes.Status422UnprocessableEntity, "Evaluation limit exceeded"),
                _ => (StatusCodes.Status500InternalServerError, "Internal error"),
            };
            ctx.Response.StatusCode = status;
            await Results.Problem(title: title, detail: ex?.Message, statusCode: status)
                .ExecuteAsync(ctx);
        }));
        return app;
    }
}
```

- [ ] **Step 2: Register the handler in `Program.cs`**

Add immediately after `var app = builder.Build();` and the migration block, **before** `app.MapRelkitRest()`:

```csharp
// src/Relkit.Service/Program.cs — add before app.MapRelkitRest();
app.UseRelkitProblemDetails();
```

- [ ] **Step 3: Write the failing error-handling test**

```csharp
// tests/Relkit.Service.Tests/RestErrorHandlingTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Service.Rest;
using Shouldly;
using Xunit;

namespace Relkit.Service.Tests;

[Collection("service")]
public class RestErrorHandlingTests(PostgresFixture fx)
{
    private const string Store = "rest-err";

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Relkit:ConnectionString", fx.ConnectionString));

    [Fact]
    public async Task Check_against_an_unknown_permission_returns_400()
    {
        using var factory = CreateFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<IStoreManager>().CreateStoreAsync(Store);
            await sp.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(Store, "t1"));
            await sp.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(Store, Store));
            var schema = new SchemaBuilder("v1")
                .Type("species", t => t.Relation("editor", s => s.User()).Permission("edit", p => p.Relation("editor")))
                .Build();
            await sp.GetRequiredService<ISchemaManager>().SetActiveSchemaAsync(Store, schema);
        }
        using var client = factory.CreateClient();

        var dto = new CheckRequestDto(Store, "t1", new EntityRefDto("species", "kangaroo"),
            "no_such_permission", new SubjectRefDto("user", "alice"),
            new RequestContextDto(null, new SubjectRefDto("user", "alice")));

        var response = await client.PostAsJsonAsync("/v1/check", dto);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 4: Run to verify**

Run: `dotnet test tests/Relkit.Service.Tests --filter RestErrorHandlingTests`
Expected: PASS. A check against an undefined permission surfaces as `400`, not a silent deny.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test tests/Relkit.Service.Tests`
Expected: PASS — gRPC (`m3/01`) and REST surfaces both green over Testcontainers Postgres.

- [ ] **Step 6: Commit**

```bash
git add src/Relkit.Service tests/Relkit.Service.Tests
git commit -m "feat: map engine exceptions to ProblemDetails on the REST surface"
```

---

## Self-review checklist (run after all tasks)

- [ ] `dotnet build` clean with `TreatWarningsAsErrors=true`.
- [ ] REST endpoints resolve the same `IAuthorizer` + manager interfaces the gRPC services use; no evaluation/persistence logic in the endpoints.
- [ ] `POST /v1/check`, `/v1/batch-check`, `/v1/list-objects`, `/v1/list-subjects` answer correctly; a denial is `200` with `allowed:false`; `Explain` returns when requested.
- [ ] Management endpoints (tuples write/delete/query, attributes, change-log, schema validate/set/get, stores, tenants) round-trip through the manager interfaces; an invalid schema returns `400`.
- [ ] `RestMap` round-trips every shared type, including the null-`Relation` rule and the tuple/condition mapping.
- [ ] OpenAPI/Swagger document is served at `/swagger/v1/swagger.json` and the UI at `/swagger`.
- [ ] Engine typed exceptions surface as `ProblemDetails`; the full `Relkit.Service.Tests` suite is green.

## Contract gaps (reported, not changed)

- **Shared canonical-schema serializer.** Same gap noted in `m3/01`: the REST surface reuses `m3/01`'s `SchemaJson` (`System.Text.Json` over the AST). A `SchemaSerializer` in `Relkit.Core`/`Relkit.Abstractions` would let the gRPC service, REST service, DSL parser (`m3/05`), and tooling share one (de)serializer. Flagged for `m3/05`.
- **`ChangeLogEntry.Before/After` are `object?` with no wire shape.** The REST surface returns them as raw JSON values (`object?` serialized by `System.Text.Json`); the gRPC surface (`m3/01`) returns them as JSON strings. A contract-defined before/after image type would let both surfaces agree. Flagged, not changed.
- **No pagination metadata type for ListSubjects in the contract beyond the cursor.** `ListSubjectsResult` carries `Subjects` + `ContinuationToken`, which the REST DTO mirrors directly; no gap in behaviour, noted only for symmetry with the over-fetch/refill contract (§7.5) that the engine already honours.

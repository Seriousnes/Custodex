# Custodex ASP.NET Core authorization adapter — design

## Summary

Two new packable libraries let any ASP.NET Core app — including server-side Blazor —
delegate authorization to the Custodex engine through the framework's own pipeline, with
no Custodex-specific authorization attribute:

- **`Custodex.AspNetCore`** wires the framework's `IAuthorizationService` to
  `IAuthorizer.CheckAsync` via a dynamic policy provider and a resource-based handler. Stock
  `[Authorize("custodex:document:edit")]`, `RequireAuthorization("custodex:document:edit")`,
  and `<AuthorizeView Policy="custodex:document:edit">` resolve against the engine unchanged.
- **`Custodex.Blazor`** adds one component, `<CustodexAuthorizeView>`, an ergonomic per-object
  wrapper over the framework's `<AuthorizeView>`.

The adapter binds to `IAuthorizer` resolved from DI, so it behaves identically whether the
engine runs in-process (`AddCustodex().UsePostgres()`) or remotely (`AddCustodexClient(...)`).
It depends only on `Custodex.Abstractions` plus the ASP.NET Core shared framework.

## Packages and projects

| Project | SDK | References | `MinVerTagPrefix` |
|---|---|---|---|
| `src/Custodex.AspNetCore` | `Microsoft.NET.Sdk` + `FrameworkReference Microsoft.AspNetCore.App` | `Custodex.Abstractions` | `aspnetcore-v` |
| `src/Custodex.Blazor` | `Microsoft.NET.Sdk.Razor` + `FrameworkReference Microsoft.AspNetCore.App` | `Custodex.AspNetCore` | `blazor-v` |
| `tests/Custodex.AspNetCore.Tests` | `Microsoft.NET.Sdk` | `Custodex.AspNetCore`, `Custodex.Storage.InMemory`, `Custodex.TestKit`, `Microsoft.AspNetCore.Mvc.Testing` | — |
| `tests/Custodex.Blazor.Tests` | `Microsoft.NET.Sdk.Razor` | `Custodex.Blazor`, `Custodex.Storage.InMemory`, `Custodex.TestKit`, `bunit` | — |

A pure Web API references only `Custodex.AspNetCore` and never pulls Razor tooling. A
server-side Blazor app that uses only stock `<AuthorizeView Policy="…">` also needs only
`Custodex.AspNetCore`, because `<AuthorizeView>` is the framework's own component calling
`IAuthorizationService`; `Custodex.Blazor` is required only for `<CustodexAuthorizeView>`.

Each shipping project sets `IsPackable=true`, its distinct `MinVerTagPrefix`,
`GenerateDocumentationFile=true`, `PackageReadmeFile=README.md` with a packed `README.md`,
and a `Description`. All four projects are added to `Custodex.slnx`. Every public type and
member carries an XML doc comment, since `TreatWarningsAsErrors=true` makes CS1591 fatal.

## The core mechanism

```
[Authorize("custodex:document:edit")]   ┐
<AuthorizeView Policy="custodex:…:…">    ├─→ IAuthorizationService.AuthorizeAsync(user, resource, policyName)
RequireAuthorization("custodex:…:…")     ┘
            │
            ▼  policyName starts with the configured prefix?
   CustodexPolicyProvider ── no ─→ DefaultAuthorizationPolicyProvider (existing named policies untouched)
            │ yes
            ▼
   AuthorizationPolicy = RequireAuthenticatedUser() + CustodexRequirement(type, permission)
            │
            ▼
   CustodexAuthorizationHandler : AuthorizationHandler<CustodexRequirement>
            ├─ subject  = ICustodexSubjectResolver(user)
            ├─ tenant   = ICustodexTenantResolver(user, httpContext?)
            ├─ object   = first matching ICustodexObjectResolver(resolutionContext)
            ├─ context  = IRequestContextFactory(subject, attributes, now)
            └─ decision = IAuthorizer.CheckAsync(new CheckRequest(tenant, object, permission, subject, context))
            │
            ▼
   Allowed → context.Succeed(requirement)      (200)
   Denied  → requirement left unmet            (403)
   Unauthenticated → challenge                 (401, from RequireAuthenticatedUser)
```

### Policy name format

`"{prefix}{separator}{type}{separator}{permission}"`, default `custodex:document:edit`. The
prefix (`custodex`) and separator (`:`) are configurable. The provider splits a matching name
into `(type, permission)` and produces a `CustodexRequirement`. Parsed policies are cached in
the provider so repeated requests do not rebuild them.

### Types

```csharp
namespace Custodex.AspNetCore;

public sealed class CustodexRequirement(string objectType, string permission) : IAuthorizationRequirement
{
    public string ObjectType { get; } = objectType;
    public string Permission { get; } = permission;
}

public sealed class CustodexPolicyProvider : IAuthorizationPolicyProvider
{
    // Decorates DefaultAuthorizationPolicyProvider.
    // GetPolicyAsync(name): prefix match → cached policy with RequireAuthenticatedUser() + CustodexRequirement; otherwise delegate.
    // GetDefaultPolicyAsync / GetFallbackPolicyAsync: delegate to the inner default provider.
}

public sealed class CustodexAuthorizationHandler : AuthorizationHandler<CustodexRequirement>
{
    // Resolves subject, tenant, object, request context; calls IAuthorizer.CheckAsync; Succeed on allow.
}
```

## Resolution seams

Every input the engine needs is produced by a replaceable seam, registered with `TryAdd` so a
consumer can substitute any one. The handler assembles them into a `CheckRequest`.

```csharp
public sealed class CustodexResolutionContext
{
    public CustodexRequirement Requirement { get; }
    public ClaimsPrincipal User { get; }
    public object? Resource { get; }          // AuthorizationHandlerContext.Resource
    public HttpContext? HttpContext { get; }  // from IHttpContextAccessor; null in a Blazor circuit
    public TenantContext Tenant { get; }
}
```

### Object — `ICustodexObjectResolver` (ordered chain)

```csharp
public interface ICustodexObjectResolver
{
    bool TryResolve(CustodexResolutionContext context, out EntityRef entity);
}
```

The handler injects `IHttpContextAccessor` (rather than testing `context.Resource is HttpContext`,
which is version-dependent) and tries the registered resolvers in order:

1. **`ResourceEntityRefResolver`** — `context.Resource is EntityRef` → use it. This is what
   `<CustodexAuthorizeView>` passes.
2. **`ResourceIdResolver`** — `context.Resource is string id` → `new EntityRef(Requirement.ObjectType, id)`.
   This is stock `<AuthorizeView Resource="@doc.Id" Policy="custodex:document:edit">`.
3. **`RouteValueResolver`** — from `HttpContext.Request.RouteValues`, the first present of the
   candidate keys `"{type}Id"`, `"{type}"`, `"id"`. So `/documents/{documentId}`,
   `/documents/{document}`, and `/documents/{id}` all bind with no configuration. An endpoint may
   override the key and type with additive metadata via `.WithCustodexObject(routeKey, type)`,
   which configures object binding only and never triggers authorization on its own.
4. **`RootObjectResolver`** — invokes `options.RootObject(context)`; used for page-level
   `[Authorize(Policy="…")]` and object-less `<AuthorizeView>` where no specific object exists.

If no resolver yields an object, the handler leaves the requirement unmet (deny) and emits a
diagnostic naming the policy.

### Subject — `ICustodexSubjectResolver`

```csharp
public interface ICustodexSubjectResolver
{
    bool TryResolve(ClaimsPrincipal user, out SubjectRef subject);
}
```

Default `ClaimsCustodexSubjectResolver`: id from `options.SubjectIdClaim`
(default `ClaimTypes.NameIdentifier`, matching the Service's JWT `NameClaimType` and the API-key
handler), type from `options.SubjectType` (default `"user"`). When the claim is absent, no subject
resolves and the request is denied. (Documentation notes the JWT `sub`→`NameIdentifier` remap:
apps that disable inbound claim mapping set `options.SubjectIdClaim = "sub"`.)

### Tenant — `ICustodexTenantResolver`

```csharp
public interface ICustodexTenantResolver
{
    bool TryResolve(ClaimsPrincipal user, HttpContext? httpContext, out TenantContext tenant);
}
```

Default `ClaimsHeaderCustodexTenantResolver`: store from the `options.StoreClaim`
(`Custodex:store`) claim; tenant from the `options.TenantHeader` (`X-Custodex-Tenant`) header,
falling back to the `options.TenantClaim` (`Custodex:tenant`) claim. All names configurable; the
whole resolver is replaceable. In a Blazor circuit there is no live request header, so the claim
path (or a custom resolver mirroring `StudioConnectionState`) supplies the tenant.

### Request context — `IRequestContextFactory`

```csharp
public interface IRequestContextFactory
{
    RequestContext Create(SubjectRef subject, CustodexResolutionContext context);
}
```

Default builds `RequestContext(Now, subject, attributes)` with `Now` from the registered
`TimeProvider` and `attributes` aggregated from the attribute sources. The
`CancellationToken` flows from `HttpContext.RequestAborted` into `CheckAsync`, separate from the
`RequestContext`.

### ABAC attributes — `ICustodexAttributeSource`

```csharp
public interface ICustodexAttributeSource
{
    void Contribute(IDictionary<string, object?> attributes, CustodexResolutionContext context);
}
```

Zero sources register by default, yielding an empty attribute set. Apps add sources to project
claims, route values, or headers into `RequestContext.Attributes` for condition (ABAC)
evaluation. Multiple sources contribute in registration order.

## Blazor integration

`Custodex.Blazor` ships `<CustodexAuthorizeView>` (namespace `Custodex.Blazor`):

```razor
<CustodexAuthorizeView ObjectType="document" ObjectId="@doc.Id" Permission="edit">
    <Authorized>    <button>Edit</button> </Authorized>
    <NotAuthorized> <span>read-only</span> </NotAuthorized>
</CustodexAuthorizeView>
```

Parameters: either `Object` (an `EntityRef`) or `ObjectType` + `ObjectId`; `Permission`; and the
render fragments `Authorized`, `NotAuthorized`, `Authorizing`, and `ChildContent`, mirroring
`<AuthorizeView>`. The component injects `IOptions<CustodexAuthorizationOptions>`, computes the
policy name (`{prefix}{sep}{type}{sep}{permission}`), and renders an inner
`<AuthorizeView Policy="@policyName" Resource="@entity">`, forwarding the fragments — so it flows
through the identical handler and resolver path as the policy string, with one evaluation path
and no divergence.

Stock `<AuthorizeView Resource="@doc.Id" Policy="custodex:document:edit">` works without the
component, resolved by `ResourceIdResolver`. A page-level `@attribute [Authorize(Policy="…")]`
carries no object and so resolves at the root-object level configured for the app.

## DI surface and options

```csharp
services.AddCustodexAuthorization(options =>
{
    options.SubjectType   = "user";
    options.SubjectIdClaim = ClaimTypes.NameIdentifier;
    options.StoreClaim    = "Custodex:store";
    options.TenantClaim   = "Custodex:tenant";
    options.TenantHeader  = "X-Custodex-Tenant";
    options.PolicyPrefix  = "custodex";
    options.PolicySeparator = ':';
    options.RootObject    = ctx => new EntityRef("tenant", ctx.Tenant.Tenant);
    options.ThrowOnEvaluationError = false;
});
```

`AddCustodexAuthorization(Action<CustodexAuthorizationOptions>? configure = null)`:

- calls `AddHttpContextAccessor()`;
- binds `CustodexAuthorizationOptions`;
- registers `IAuthorizationPolicyProvider → CustodexPolicyProvider` (singleton);
- registers `IAuthorizationHandler → CustodexAuthorizationHandler` (scoped, so it can resolve a
  scoped `IAuthorizer`);
- `TryAdd`s the default subject resolver, tenant resolver, request-context factory, and the
  ordered built-in object resolvers (resource-entity, resource-id, route-value, root-object);
- registers a startup check that resolves `IAuthorizer` once and throws a clear
  `InvalidOperationException` (naming `AddCustodex(...)`/`AddCustodexClient(...)`) when the engine
  is not registered.

`RootObject` defaults to `null`; when null and no other resolver matches, an object-less policy is
denied with a diagnostic.

## Decision and error semantics

- **Allow / deny** is the `CheckResult.Allowed` value: allow → `Succeed`; deny → requirement left
  unmet → 403.
- **Unauthenticated** → 401 challenge, because every Custodex policy includes
  `RequireAuthenticatedUser()`.
- **Unresolvable subject, tenant, or object** → deny with a diagnostic; never a 500.
- **Engine exceptions** (unknown type/permission, invalid schema, depth/cycle guard) are caught by
  the handler, logged at error level with the policy name and exception, and treated as deny, so a
  schema typo cannot crash the request pipeline. `options.ThrowOnEvaluationError = true` rethrows
  instead, surfacing the misconfiguration during development.

## Shared constants

`Custodex.AspNetCore` defines `CustodexClaimTypes` (`Store = "Custodex:store"`,
`Tenant = "Custodex:tenant"`) and `CustodexHeaders` (`Tenant = "X-Custodex-Tenant"`) with the same
string values the Service uses, so tokens and headers issued for the Service authorize the adapter
without translation.

## Testing

- **Unit** (`Custodex.AspNetCore.Tests`): the policy provider (prefix match, separator, unknown
  prefix delegating to the default, parsed-policy caching); the handler (each object-resolver path,
  root fallback, subject/tenant absence → deny, engine exception → deny vs rethrow); the default
  subject and tenant resolvers; the request-context factory using a fake `TimeProvider`.
- **HTTP integration** (`Custodex.AspNetCore.Tests`): a `WebApplicationFactory` host exposing a
  minimal-API endpoint and an MVC controller action guarded by `[Authorize("custodex:…")]`, backed
  by `Custodex.Storage.InMemory` with a seeded schema and tuples. Assertions cover 200 (granted),
  403 (denied), 401 (anonymous), and the route-key binding variants.
- **Blazor** (`Custodex.Blazor.Tests`): `bunit` renders `<CustodexAuthorizeView>` and stock
  `<AuthorizeView Policy="custodex:…">` against an in-memory authorizer, asserting the `Authorized`
  and `NotAuthorized` fragments render per the decision.
- All schema/tuple/policy identifiers in tests come from `Custodex.TestKit.TestWorld`; policy-name
  strings are built from generated type and permission values so the domain-vocabulary guard stays
  green.

## Build and packaging requirements

- `net10.0`, C# `latest`, nullable enabled, `TreatWarningsAsErrors=true`.
- The two shipping projects set `IsPackable=true` and a distinct `MinVerTagPrefix`
  (`aspnetcore-v`, `blazor-v`), required by the per-package release run.
- All four projects are listed in `Custodex.slnx`.
- `bunit`'s license is verified permissive before it is added.

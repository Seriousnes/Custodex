# Custodex.AspNetCore

ASP.NET Core authorization adapter for the Custodex ReBAC + ABAC engine.

Register the engine (`AddCustodex().Use<provider>()` or `AddCustodexClient(...)`) and then:

```csharp
builder.Services.AddCustodexAuthorization(options =>
{
    options.SubjectType = "user";
    options.RootObject = ctx => new EntityRef("tenant", ctx.Tenant.Tenant);
});
```

Stock framework authorization resolves against Custodex with no custom attribute:

```csharp
app.MapGet("/documents/{id}", GetDocument)
   .RequireAuthorization("custodex:document:view");

[Authorize("custodex:document:edit")]
public IActionResult Edit(string id) => ...;
```

The policy name is `custodex:{type}:{permission}`. The object id is taken from the route
(`{type}Id`, `{type}`, or `id`) or from the `AuthorizeView` `Resource`. Unknown policy names
fall through to the framework's default provider, so existing named policies keep working.

## Any-object (exists) gate

The four-segment form `custodex:any:{type}:{permission}` authorizes when the subject holds the
permission on **at least one** object of the type, without naming a specific object:

```csharp
app.MapGet("/documents", ListDocuments)
   .RequireAuthorization("custodex:any:document:view");
```

It binds no object and ignores the resource and route; it resolves the subject and tenant, then
asks the engine `ListObjectsAsync(..., PageSize: 1)` and grants when the page has any id. The `any`
segment is configurable via `AnyObjectSegment` (default `any`). A three-segment policy whose type is
literally `any` (`custodex:any:view`) is unchanged: it still means type `any`, permission `view`.

Cost note: the any-gate is a candidate-set scan, not an O(1) membership test. `PageSize: 1` stops
the enumeration at the first confirmed object, but confirming that first object still walks the
subject's reachable set for the type, so it is more expensive than a single `Check` on a known
object. Prefer a concrete-object policy when you already have the id.

## Security and operational notes

- **Tenant comes from the `X-Custodex-Tenant` header first, then the `Custodex:tenant` claim.** This mirrors the Custodex service so the same tokens and headers work unchanged. Because the header is client-supplied, a principal can request evaluation against a different tenant's grant graph within the same store; access is still granted only where that subject actually holds the permission, and the `Custodex:store` claim (never the header) bounds the scope. To pin the tenant to the token instead, call `.AddCustodexAuthorization().UseClaimOnlyTenantResolver()`: it swaps in the built-in `ClaimsCustodexTenantResolver`, which reads the store and tenant from claims only and ignores the header, closing the same-store tenant spoof for apps whose tenant is fixed by the login token.
- **Route object binding tries `{type}Id`, then `{type}`, then `id`.** On a route whose `{id}` is not the policy object's id (for example `/users/{id}/documents` under a `custodex:document:...` policy), pin the key explicitly with `.WithCustodexObject(routeKey: "...", type: "...")` so the wrong route value is never bound.
- **Keep ids out of policy names.** A policy name is `custodex:{type}:{permission}` only; the object id comes from the route or the `AuthorizeView` resource. Policy names are cached, so templating ids into them would grow the cache unbounded.
- `AddCustodexAuthorization` installs the Custodex dynamic policy provider as the application's `IAuthorizationPolicyProvider`; non-`custodex:` policy names are delegated to the framework default.
- **Custom resolvers implement `ValueTask<T?> ResolveAsync(..., CancellationToken cancellationToken)`** (`ICustodexSubjectResolver`, `ICustodexTenantResolver`, `ICustodexObjectResolver`), so they can `await` I/O such as a database lookup; returning `null` denies (or, for an object resolver, defers to the next resolver).

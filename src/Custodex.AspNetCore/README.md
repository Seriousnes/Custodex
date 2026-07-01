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

## Security and operational notes

- **Tenant comes from the `X-Custodex-Tenant` header first, then the `Custodex:tenant` claim.** This mirrors the Custodex service so the same tokens and headers work unchanged. Because the header is client-supplied, a principal can request evaluation against a different tenant's grant graph within the same store; access is still granted only where that subject actually holds the permission, and the `Custodex:store` claim (never the header) bounds the scope. To pin the tenant to the token instead, replace `ICustodexTenantResolver` with one that reads the tenant from a trusted claim only.
- **Route object binding tries `{type}Id`, then `{type}`, then `id`.** On a route whose `{id}` is not the policy object's id (for example `/users/{id}/documents` under a `custodex:document:...` policy), pin the key explicitly with `.WithCustodexObject(routeKey: "...", type: "...")` so the wrong route value is never bound.
- **Keep ids out of policy names.** A policy name is `custodex:{type}:{permission}` only; the object id comes from the route or the `AuthorizeView` resource. Policy names are cached, so templating ids into them would grow the cache unbounded.
- `AddCustodexAuthorization` installs the Custodex dynamic policy provider as the application's `IAuthorizationPolicyProvider`; non-`custodex:` policy names are delegated to the framework default.
- **Custom resolvers implement `ValueTask<T?> ResolveAsync(..., CancellationToken cancellationToken)`** (`ICustodexSubjectResolver`, `ICustodexTenantResolver`, `ICustodexObjectResolver`), so they can `await` I/O such as a database lookup; returning `null` denies (or, for an object resolver, defers to the next resolver).

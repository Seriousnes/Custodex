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

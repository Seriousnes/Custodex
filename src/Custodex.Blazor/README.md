# Custodex.Blazor

Blazor components for Custodex authorization in server-side Blazor (interactive server).

`<CustodexAuthorizeView>` is a per-object wrapper over the framework's `<AuthorizeView>`:

```razor
<CustodexAuthorizeView ObjectType="document" ObjectId="@doc.Id" Permission="edit">
    <Authorized><button>Edit</button></Authorized>
    <NotAuthorized><span>read-only</span></NotAuthorized>
</CustodexAuthorizeView>
```

Register the adapter with `services.AddCustodexAuthorization(...)` (from `Custodex.AspNetCore`).
Stock `<AuthorizeView Resource="@doc.Id" Policy="custodex:document:edit">` also works.

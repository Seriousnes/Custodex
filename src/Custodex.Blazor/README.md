# Custodex.Blazor

Blazor components for Custodex authorization in server-side Blazor (interactive server).

`<CustodexAuthorizeView>` is a per-object wrapper over the framework's `<AuthorizeView>`:

```razor
<CustodexAuthorizeView ObjectType="document" ObjectId="@doc.Id" Permission="edit">
    <Authorized><button>Edit</button></Authorized>
    <NotAuthorized><span>read-only</span></NotAuthorized>
</CustodexAuthorizeView>
```

Pass a pre-built `EntityRef` via the `Object` parameter instead of the type+id split:

```razor
<CustodexAuthorizeView Object="@(new EntityRef("document", doc.Id))" Permission="edit">
    <Authorized><button>Edit</button></Authorized>
    <NotAuthorized><span>read-only</span></NotAuthorized>
</CustodexAuthorizeView>
```

Like the framework's `<AuthorizeView>`, the app must supply a cascading `AuthenticationState` — typically via `<CascadingAuthenticationState>` or a registered `AuthenticationStateProvider`.

Register the adapter with `services.AddCustodexAuthorization(...)` (from `Custodex.AspNetCore`).
Stock `<AuthorizeView Resource="@doc.Id" Policy="custodex:document:edit">` also works.

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

Like the framework's `<AuthorizeView>`, the app must supply a cascading `AuthenticationState`, typically via `<CascadingAuthenticationState>` or a registered `AuthenticationStateProvider`.

## Any-object (exists) gate

Set `Any` to authorize on the four-segment `{prefix}:{any}:{type}:{permission}` form: the content shows when the subject holds the permission on at least one object of the type. Only `ObjectType` is read; `Object` and `ObjectId` are ignored and no resource is passed:

```razor
<CustodexAuthorizeView Any ObjectType="document" Permission="view">
    <Authorized><a href="/documents">Documents</a></Authorized>
    <NotAuthorized><span>no documents</span></NotAuthorized>
</CustodexAuthorizeView>
```

The `any` segment is configurable via `AnyObjectSegment` (default `any`). Backed by `ListObjectsAsync(..., PageSize: 1)`, the any-gate is a candidate-set scan, not an O(1) membership test, so prefer a per-object view when you already have the id.

Register the adapter with `services.AddCustodexAuthorization(...)` (from `Custodex.AspNetCore`).
Stock `<AuthorizeView Resource="@doc.Id" Policy="custodex:document:edit">` also works.

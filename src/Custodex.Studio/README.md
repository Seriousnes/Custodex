# Custodex.Studio

The Custodex Studio console as a Razor class library: schema browsing, relation and tuple exploration, a check playground, change log viewing, and live metrics, ready to mount into any Blazor Server host.

## Hosting modes

Studio talks to the engine through the same `Custodex.Abstractions` interfaces (`IAuthorizer`, `IRelationManager`, `ISchemaManager`, `IMetricsSnapshotProvider`) regardless of where the engine runs, so the host chooses one of two wiring paths:

**In-process**, mounted alongside a host that already runs the Custodex engine directly (`Custodex.Core` plus a storage provider registered in the same container):

```csharp
builder.Services.AddCustodexStudio();
// ... existing AddCustodex* engine and storage provider registrations ...

app.MapCustodexStudio(authorizationPolicy: "custodex-admin");
```

**Out-of-process**, over the Custodex gRPC client, when the host does not run the engine itself and instead talks to a separate Custodex service:

```csharp
builder.Services.AddCustodexClient("https://custodex.example.internal");
builder.Services.AddCustodexStudio();

app.MapCustodexStudio(authorizationPolicy: "custodex-admin");
```

`AddCustodexStudio` registers Razor component rendering, cascading authentication state, MudBlazor services, and an in-memory `IStudioViewStore` for saved views. Call `AddCustodexStudioPostgresViewStore(connectionString)` before or after it to persist saved views instead. `MapCustodexStudio` requires an authenticated, authorized caller; the console reads and mutates tenant relations, attributes, and schema, so it must never be mapped anonymously.

## Transitive dependencies

This package carries `MudBlazor` (the console's component library), `Npgsql` (for the optional Postgres-backed view store), and `Z.Blazor.Diagrams` (for the schema graph view) as transitive dependencies.

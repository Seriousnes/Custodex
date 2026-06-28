# Final Fix Wave Report

## Fix 1 — Remove dead cross-assembly internals coupling (MUST)

**Changes:**
- `src/Custodex.Service/Views/PostgresStudioViewStore.cs`: Removed `using Custodex.Storage.Postgres;`. Replaced `CustodexSchema.Apply(connectionString)` with `new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = "custodex" }.ToString()`. The store already fully-qualifies every identifier (`custodex.studio_view`, `CREATE SCHEMA IF NOT EXISTS custodex`) so the SearchPath was a no-op.
- `src/Custodex.Storage.Postgres/Custodex.Storage.Postgres.csproj`: Removed the entire `<ItemGroup>` containing `<InternalsVisibleTo Include="Custodex.Service" />`. Verified via grep that no other code in `Custodex.Service` accesses a Storage.Postgres internal — `CustodexSchema` was the sole consumer.

## Fix 2 — Make metrics aggregator unit test truly isolated (MUST)

**Changes:**
- `src/Custodex.Service/Metrics/CustodexMeterAggregator.cs`: Added `private readonly string _meterName;` field. Added optional constructor parameter `string meterName = CustodexDiagnostics.Name` with XML doc. Used `_meterName` (captured in a local) in the `MeterListener.InstrumentPublished` filter instead of the hardcoded `CustodexDiagnostics.Name`. Removed `static` from the lambda since it now captures.
- `tests/Custodex.Service.Tests/Metrics/CustodexMeterAggregatorTests.cs`: Removed `[Collection("service")]`. Changed `MeterRig` to accept a `meterName` string and create `new Meter(meterName)` from it. Added `UniqueMeterName()` helper using `Guid.NewGuid()`. Each test now creates its own unique meter name, passes it to both `MeterRig` and `CustodexMeterAggregator`, so the aggregator never observes the global `"Custodex"` meter. Production `Program.cs` registration unchanged.

## Fix 3 — Conditioned precedence rendering bug + test (fold-in)

**Changes:**
- `src/Custodex.Studio/Components/Schema/PermExprFormatter.cs`: Changed `$"{Format(c.Inner)} with {c.ConditionName}"` to `$"{Operand(c.Inner)} with {c.ConditionName}"`. `Operand` parenthesizes any binary inner expression, so `Conditioned(Union(a,b), cond)` now correctly renders as `(a + b) with cond`.
- `tests/Custodex.Studio.Tests/PermExprFormatterTests.cs`: Added `Conditioned_with_binary_inner_parenthesizes_the_inner` test asserting the parenthesized form.

## Fix 4 — Remove dead injection (fold-in)

**Changes:**
- `src/Custodex.Studio/Components/Pages/MetricsDashboard.razor`: Removed `@inject StudioConnectionState State`. Metrics are process-global; `State` was never referenced in the page body. `MetricsDashboardTests` still passes (it registers `StudioConnectionState` as a service but the component no longer requires it).

## Fix 5 — Collection initializer style (fold-in)

**Not applied.** `ConditionRef` takes `IReadOnlyDictionary<string, object?>` (an interface), which is not constructible from a `[]` collection expression. The compiler rejects the change with CS9174. The condition "where `[]` resolves" in the instructions is not met, so this change is correctly omitted.

## Build and test results

```
dotnet build Custodex.slnx
Build succeeded.  0 Warning(s)  0 Error(s)

dotnet test Custodex.slnx
Passed!  Custodex.Storage.InMemory.Tests  — 20/20
Passed!  Custodex.Abstractions.Tests      — 10/10
Passed!  Custodex.Core.Tests             — 195/195
Passed!  Custodex.Studio.Tests           — 53/53
Passed!  Custodex.Client.Tests           — 20/20
Passed!  Custodex.Service.Tests          — 82/82
Passed!  Custodex.Storage.Postgres.Tests — 160/160
Total: 540/540 passed, 0 failed, 0 skipped
```

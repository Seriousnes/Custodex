# M1/06 Task 2 — CTE ListObjects Implementation Report

## Files

- Created: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.ListObjects.cs`
- Created: `tests/Custodex.Storage.Postgres.Tests/Cte/CteListObjectsTests.cs`
- Modified: `src/Custodex.Storage.Postgres/NpgsqlCteAuthorizer.Expr.cs` — removed `ListObjectsAsync` throwing stub (both the `/// <inheritdoc />` line and the method); `ListSubjectsAsync` stub left in place for Task 3

## Neutral vocabulary used

- type `species` → `asset`; group `macropods` → `herd`; ids `kangaroo` → `ka`, `wallaby` → `wa`, `emu` → `em`
- Expected values re-derived in ordinal order: exclusion test → `["ka"]`; wildcard test → `["em","ka","wa"]`
- Pagination tests use single-letter ids `a,b,c,d,e` (unchanged from plan)

## RED evidence

```
dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteListObjectsTests
Failed! - Failed: 4, Passed: 0, Skipped: 0, Total: 4
All 4 failed with System.NotImplementedException from NpgsqlCteAuthorizer.Expr.cs line 118
```

## GREEN evidence

```
dotnet test tests/Custodex.Storage.Postgres.Tests --filter CteListObjectsTests
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 325 ms
```

## Stub removal confirmation

`NpgsqlCteAuthorizer.Expr.cs` no longer contains `ListObjectsAsync`. The `ListSubjectsAsync` stub remains for Task 3.

## Commit

See git log for SHA — commit message: `feat: implement CTE list objects with over-fetch/refill pagination`

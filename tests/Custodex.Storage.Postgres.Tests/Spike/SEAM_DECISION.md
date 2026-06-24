<!-- tests/Custodex.Storage.Postgres.Tests/Spike/SEAM_DECISION.md -->
# CTE/engine seam — decided by the M1/02 spike

## Decision
- **Recursive CTE = reachability only.** A recursive CTE expands nested subject-set membership
  (`group:G#member` transitively) and follows arrow edges, returning the distinct leaf subjects
  reachable through one relation or one arrow hop. Proven cycle-safe (`UNION`-dedup frontier).
- **Algebra = C#.** `Union`/`Intersect`/`Exclude`/`Conditioned` and arrow-into-sub-permission are
  composed in `NpgsqlCteAuthorizer`, walking the `PermExpr` tree exactly like `EngineDrivenAuthorizer`.
  Arrow recurses into the related object's FULL expression, so inner exclusions/intersections are seen.
- **`Custodex.Storage.Postgres` references `Custodex.Core`** (for `SchemaIndex`, `EvaluationOptions`,
  `ContinuationCursor`, `IConditionEvaluator`). Core never references Postgres.

## Proven by this spike
1. **Reachability is CTE-expressible and cycle-safe** (Task 2): a subject-set expands to its leaf
   users; the wildcard `user:*` surfaces as a leaf; nested-group cycles terminate.
2. **All-in-SQL post-filtering is WRONG** (Task 3, Case A): `animal.edit = enclosure->edit`,
   `enclosure.edit = editor - blocked`, carol editor+blocked on the enclosure. The naive top-level
   post-filter returns carol→true (no `animal#blocked` tuple exists); the decided seam returns
   carol→false, matching the hand-computed truth and the `m0/05` oracle.
3. **The seam handles intersection-through-arrow with a wildcard gate** (Task 4, Case B / §12.5):
   quarantine-trained vet allowed, untrained vet denied, non-vet denied.

## Consequence for the build
`NpgsqlCteAuthorizer` is a Postgres-native reimplementation of the oracle's traversal whose only
divergence is *where the recursion runs* (SQL vs C# loops). `NpgsqlCteAuthorizer ≡ EngineDrivenAuthorizer`
is the correctness claim; the **M1/08 differential harness is its only proof**. The candidate SQL in
m1/05 and m1/06 is "validated by this spike and the m1/08 harness," not guaranteed-correct copy-paste.

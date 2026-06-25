# M0/03 — Schema Validation

**Goal:** Validate a `Schema` before it can become active: every `RelationRef`/`Arrow` resolves, every arrow target permission exists on each related type, permission recursion terminates (no infinite arrow/permission cycle), every `Conditioned` branch references a declared `ConditionDef`, and condition **parameter values** type-check against the declared `ConditionParam` types. Produce `SchemaValidator` returning `SchemaValidationResult`, wire `SchemaManager.ValidateSchema` to it, and make `SetActiveSchemaAsync` throw `SchemaValidationException` on an invalid schema.

**For implementers:** drive this with `superpowers:subagent-driven-development` or `superpowers:executing-plans`; follow TDD (Red → Green → Commit) per task; tasks are tracked with `- [ ]`; one conventional-commit per green task (co-author trailer per `../README.md` → Global Constraints).

**Architecture/approach:** `SchemaValidator` is a pure static function `Schema → SchemaValidationResult` in `Custodex.Core`. It indexes types/conditions (catching duplicates), resolves names against the schema, derives arrow targets from each relation's `AllowedSubjects`, and proves recursion termination with a static cycle check over a permission-dependency graph. This is a **schema-time** check, distinct from the runtime data-cycle guard in m0/05 (which guards tuple graphs). `SchemaManager` is the `ISchemaManager` implementation; it delegates validation to `SchemaValidator` and persists through `ISchemaStore`.

The condition **body** AST does not exist yet (m0/01 left an abstract `ConditionExpr` marker; m0/02 attaches `EmptyConditionBody`). This plan checks only what is possible without the body AST: that a `Conditioned` branch names a declared `ConditionDef`, and that a `ConditionRef`'s parameter **values** match the declared `ConditionParam` types. The body type-check is owned by m0/06, which extends `SchemaValidator`.

**Tech stack:** .NET 10 (`net10.0`), C# 14, xUnit, Shouldly.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies:** builds on m0/01 (`Custodex.Abstractions`) and m0/02 (`Custodex.Core`, `SchemaBuilder`) — see README.

---

### Task 1: Relation and permission resolution

- [ ] **Files:** create `src/Custodex.Core/Validation/SchemaValidator.cs`. Test: `tests/Custodex.Core.Tests/Validation/RelationResolutionTests.cs`.

**Produces:** `SchemaValidator.Validate(Schema) → SchemaValidationResult`; type/condition indexing with duplicate detection; per-permission expression resolution.
**Consumes (see README):** `Schema`, `EntityTypeDef`, `RelationDef`, `PermissionDef`, the `PermExpr` hierarchy, `ConditionDef`, `SchemaValidationResult`.

**Behavior:**
- A `RelationRef(name)` resolves when `name` is **either** a declared relation **or** a declared permission on the *same* type — there is no separate `PermissionRef` node, so a `RelationRef` naming a permission (nesting, e.g. `manage ⊃ edit`) is valid, not an error.
- Indexing reports duplicate entity types, duplicate relations on a type, duplicate permissions on a type, and duplicate conditions. A `Conditioned` branch whose name is not a declared condition is an error.

**Cases to pin:**

| Setup | Expect |
|---|---|
| permission references a relation, and another permission references that permission (nesting) | valid |
| permission references an undeclared name | invalid; error names the type and the bad name |
| a type declares the same relation twice | invalid; error names the relation as a duplicate |

**Done when:** build clean under TreatWarningsAsErrors; resolution and duplicate cases hold.

---

### Task 2: Arrow target resolution

- [ ] **Files:** modify `src/Custodex.Core/Validation/SchemaValidator.cs`. Test: `tests/Custodex.Core.Tests/Validation/ArrowResolutionTests.cs`.

**Produces:** arrow-target checking inside `Validate`.
**Consumes (see README):** `Arrow`, `RelationDef`, `SubjectTypeRef`.

**Behavior:**
- An `Arrow(relation, permission)` is valid when, for **every** non-subject-set object type that may fill `relation` (the `SubjectTypeRef.Type` values where `Relation is null`), that type declares the named `permission`. A subject-set filler names a relation on the related type, not an arrow target, and is skipped. An arrow through an undeclared relation, or into an undeclared type, is an error.

**Cases to pin:**

| Setup | Expect |
|---|---|
| arrow to a permission present on the single related type | valid |
| arrow to a permission absent on the related type | invalid; error names the target type and permission |
| relation has two target types, one missing the permission | invalid; error names the missing-side type |
| arrow into a type not declared at all | invalid |

**Done when:** build clean; multi-target case is covered.

---

### Task 3: Permission recursion termination

- [ ] **Files:** modify `src/Custodex.Core/Validation/SchemaValidator.cs`. Test: `tests/Custodex.Core.Tests/Validation/RecursionTerminationTests.cs`.

**Produces:** a static permission-cycle check inside `Validate`.
**Consumes (see README):** the `PermExpr` hierarchy, `RelationDef`, `SubjectTypeRef`.

**Behavior** (spec §10.3, schema-time termination):
- Termination is proved by a DFS over a permission-dependency graph whose nodes are `(type, permission)` pairs. A `RelationRef(name)` that names a permission on the same type contributes an edge to `(thisType, name)`; a `RelationRef` naming a *relation* contributes none. An `Arrow(relation, perm)` contributes an edge to `(U, perm)` for every non-subject-set target type `U`. A grey-node revisit (back-edge) is a non-terminating cycle and is invalid.
- Run cycle detection **only** when name/arrow resolution already succeeded, so an unresolved name does not masquerade as a cycle. A subject-set self-reference (`member: group#member`) is a relation filler, not a permission edge, and must not be flagged.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `edit → edit` (self-referential permission) | invalid; error mentions a cycle on `type.edit` |
| `animal.edit → enclosure.edit → animal.edit` through arrows | invalid; error mentions a cycle |
| `manage → edit → medicator` (relation terminates) | valid |
| `group.member` filled by `group#member` | valid (not a permission cycle) |

**Done when:** build clean; cycles rejected, subject-set self-references not flagged.

---

### Task 4: Condition parameter-value type-checking

- [ ] **Files:** create `src/Custodex.Core/Validation/ConditionParamChecker.cs`. Test: `tests/Custodex.Core.Tests/Validation/ConditionParamCheckTests.cs`.

**Produces:** `ConditionParamChecker.Check(ConditionDef, IReadOnlyDictionary<string, object?> parameters) → IReadOnlyList<string>` — per-parameter errors, empty when the supplied values satisfy the declared types.
**Consumes (see README):** `ConditionDef`, `ConditionParam`, `ConditionType`.

**Behavior:**
- Checks the **parameter values** a tuple's `ConditionRef` carries against the declared `ConditionParam` types — the only condition check possible before the body AST exists. Coercion: `Int`/`Long` accept `int`/`long`; `Double` accepts `int`/`long`/`double`; `Bool` accepts `bool`; `String` accepts `string`; `Timestamp` accepts `DateTimeOffset`/`DateTime`. Every declared parameter must be present; an undeclared parameter name is an error.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `Int` params supplied with `int` values | no errors |
| a `string` supplied where `Int` declared | error names the parameter and the expected type |
| a declared parameter omitted | error names the missing parameter |
| an extra undeclared parameter supplied | error names it as not declared |
| `Double` param supplied an integer value | no errors (widening) |

**Done when:** build clean; coercion and missing/extra cases hold.

---

### Task 5: `SchemaManager` delegating to the validator

- [ ] **Files:** create `src/Custodex.Core/SchemaManager.cs`. Test: `tests/Custodex.Core.Tests/SchemaManagerTests.cs`.

**Produces:** `SchemaManager : ISchemaManager` — `ValidateSchema` delegating to `SchemaValidator`; `SetActiveSchemaAsync` throwing `SchemaValidationException` on an invalid schema and otherwise persisting through `ISchemaStore` inside a unit of work; `GetActiveSchemaAsync` reading it back.
**Consumes (see README):** `ISchemaManager`, `ISchemaStore`, `IUnitOfWorkFactory`, `IUnitOfWork`, `Schema`, `SchemaValidationResult`, `SchemaValidationException`.

**Behavior:**
- Constructor takes `ISchemaStore` and `IUnitOfWorkFactory`; it depends only on abstractions (tests use tiny inline fakes, so this plan does not depend on m0/04). `SetActiveSchemaAsync` validates first, then begins a unit of work, persists, and commits.

**Cases to pin:**

| Setup | Expect |
|---|---|
| `ValidateSchema` on valid / invalid schemas | matches the validator result |
| `SetActiveSchemaAsync` on a valid schema | persisted and committed; readable via `GetActiveSchemaAsync` |
| `SetActiveSchemaAsync` on an invalid schema | throws `SchemaValidationException` carrying the errors |

**Done when:** build clean; valid schema commits, invalid throws.

---

## Self-review checklist (after all tasks)

- [ ] `dotnet build` clean under TreatWarningsAsErrors.
- [ ] Every `RelationRef`/`Arrow` resolves; a `RelationRef` may name a same-type permission (nesting) without error.
- [ ] Arrow targets are drawn from each relation's `AllowedSubjects` and checked against every target type's permissions, including the multi-target case.
- [ ] Static permission cycles (direct, through arrows) are rejected; subject-set self-references are not flagged.
- [ ] `Conditioned` branches resolve to a declared `ConditionDef`; `ConditionParamChecker` type-checks tuple parameter values.
- [ ] `SchemaManager.ValidateSchema` delegates to `SchemaValidator`; `SetActiveSchemaAsync` throws on invalid input and commits a valid schema.
- [ ] No condition **body** AST is introduced here; the body type-check is left to m0/06.

# M3/03 — Authentication & Multi-Store Resolution

**Goal:** Authenticate callers to `Custodex.Service` (API key or OIDC bearer), resolve the target store/tenant from the request into a request-scoped `TenantContext` the handlers consume, and gate management endpoints separately from decision endpoints so a read-only credential cannot mutate data.

**For implementers:** drive this plan with `superpowers:subagent-driven-development` (or `superpowers:executing-plans`). Each task is TDD — Red → Green → Commit — tracked by its `- [ ]` checkbox. One Conventional Commit per green task with the co-author trailer (see `../README.md` → Global Constraints).

**Architecture:** authentication uses ASP.NET authentication schemes; store/tenant resolution is middleware producing a request-scoped `TenantContext`. The full request flow is: **authenticate** (API key or bearer) → emit `Custodex:store`/`Custodex:role` claims → **resolve** the `TenantContext` (store from the claim, tenant from the `X-Custodex-Tenant` header or `Custodex:tenant` claim) → **authorize** against a policy (decision endpoints accept `reader` or `admin`; management endpoints require `admin`). The `m3/01` gRPC services and `m3/02` REST handlers read the resolved `TenantContext` instead of trusting a client-supplied store/tenant, so a credential scoped to store A cannot touch store B.

**Tech stack:** .NET 10, ASP.NET Core authentication (`AddJwtBearer`, custom `AuthenticationHandler`), authorization policies, xUnit + Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`.

**Global Constraints:** see `../README.md` → Global Constraints.

**Dependencies (see README):** the `Custodex.Service` host and gRPC services (`m3/01`) and the REST surface (`m3/02`) — their handlers must read the request-scoped `TenantContext` produced here rather than a client-supplied value.

---

### Task 1: API-key authentication scheme

- [ ] **Files:** add `Auth/ApiKeyOptions.cs`, `Auth/ApiKeyAuthenticationHandler.cs`; register the scheme in `Program.cs`; test `…Tests/Auth/ApiKeyAuthTests.cs`.

**Produces:** an `"ApiKey"` authentication scheme validating the `X-Custodex-Key` header against configured keys, each mapped to a store and a role (`reader` or `admin`).
**Consumes (see README):** ASP.NET `AuthenticationHandler<TOptions>`; bound from `Custodex:ApiKeys` configuration.

**Behavior:** `ApiKeyOptions` carries the header name (`X-Custodex-Key`), the scheme name, and a `key → (store, role)` map bound from configuration. The handler returns `NoResult` when the header is absent (so other schemes can run), `Fail` for an unknown key, and on success emits claims `Custodex:store`, `Custodex:role`, and a `NameIdentifier` of `apikey:{store}`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| POST `/check` with no key | 401 |
| POST `/check` with a valid reader key | 200 |

**Done when:** build clean; cases pass; the scheme rejects missing/unknown keys and accepts a configured key with its claims.

---

### Task 2: OIDC bearer scheme alongside API keys

- [ ] **Files:** modify `Program.cs`; test `…Tests/Auth/OidcAuthTests.cs` (uses a test JWT signed with a known dev key).

**Produces:** a `"Bearer"` JWT scheme and a policy scheme (`"Custodex-any"`) that accepts EITHER `ApiKey` or `Bearer`; the bearer's `store`/`role` come from configured claim names (default `Custodex:store`/`Custodex:role`).
**Consumes (see README):** `Microsoft.AspNetCore.Authentication.JwtBearer`; the API-key scheme (Task 1).

**Behavior:** the policy scheme's forward selector routes by header presence — the request goes to `ApiKey` when the `X-Custodex-Key` header is present, otherwise to `Bearer`. Both paths converge on the same `Custodex:store`/`Custodex:role` claims, so downstream resolution and policies are scheme-agnostic. JWT `Authority`/`Audience` (or a test signing key) bind from `Custodex:Jwt`.

**Cases to pin:**

| Setup | Expect |
|---|---|
| POST `/check` with a valid bearer token (store=zoo) | 200 |

**Done when:** build clean; the case passes; a valid bearer token reaches the same authorized state an API key does.

---

### Task 3: Store/tenant resolution into a request-scoped `TenantContext`

- [ ] **Files:** add `Tenancy/TenantContextAccessor.cs` (`ITenantContextAccessor`), `Tenancy/TenantResolutionMiddleware.cs`; wire both in `Program.cs`; update the `m3/01` gRPC services and `m3/02` REST handlers to read `accessor.Current`; test `…Tests/Tenancy/TenantResolutionTests.cs`.

**Produces:** `ITenantContextAccessor { TenantContext Current { get; } }` (request-scoped) and the middleware that populates it.
**Consumes (see README):** `TenantContext`; the authenticated `ClaimsPrincipal`.

**Behavior:** the store comes from the `Custodex:store` claim; the tenant comes from the `X-Custodex-Tenant` header, falling back to a `Custodex:tenant` claim. A missing store or tenant short-circuits with `400` before the handler runs. The handlers ignore any client-supplied store/tenant in the body/message and use `accessor.Current`, so a caller cannot act on a store other than the one their credential authorizes. (The body/message `store`/`tenant` fields from `m3/01`/`m3/02` become inert once resolution is authoritative.)

**Cases to pin:**

| Setup | Expect |
|---|---|
| reader key, no `X-Custodex-Tenant` header | 400 |
| reader key (store=zoo) + `X-Custodex-Tenant: sydney-zoo` | resolved context = store `zoo`, tenant `sydney-zoo` |

**Done when:** build clean; cases pass; handlers use the resolved `TenantContext`, never a client-supplied one.

---

### Task 4: Authorization policies — readers vs admins

- [ ] **Files:** modify `Program.cs` (define + apply the policies); test `…Tests/Auth/PolicyTests.cs`.

**Produces:** two policies — `"Custodex:decide"` (role `reader` or `admin`) on the decision endpoints, `"Custodex:manage"` (role `admin`) on the management endpoints — applied to both the REST groups and the gRPC methods.
**Consumes (see README):** the `Custodex:role` claim from Tasks 1–2.

**Behavior:** apply `RequireAuthorization("Custodex:decide")` to the decision endpoints/methods and `RequireAuthorization("Custodex:manage")` to the management ones, so a reader key is rejected on every mutation while an admin key passes. Decision and management are gated independently.

**Cases to pin:**

| Setup | Expect |
|---|---|
| reader key, POST `/tuples` | 403 |
| admin key, POST `/tuples` | 200 |

**Done when:** build clean; cases pass; a reader is forbidden on mutations; an admin is allowed.

---

## Self-review checklist

- [ ] Handlers use the resolved `TenantContext`, never a client-supplied store/tenant field.
- [ ] A credential scoped to store A cannot read or write store B's data (claim-derived store).
- [ ] Decision vs management endpoints enforce distinct policies; a reader key is rejected on every mutation.
- [ ] Both the API-key and bearer paths reach the same resolved-tenant, policy-gated behaviour.

# M3/03 — Authentication & Multi-Store Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Authenticate callers to `Custodex.Service` (API key or OIDC bearer), resolve the target store/tenant from the request into a `TenantContext`, and gate management endpoints separately from decision endpoints.

**Architecture:** Authentication is ASP.NET authentication schemes; store/tenant resolution is middleware producing a request-scoped `TenantContext` the gRPC/REST handlers consume. Decision endpoints (Check/List) and management endpoints (write tuples/schema, create stores) carry different authorization policies so a read-only key cannot mutate data.

**Tech Stack:** .NET 10, ASP.NET Core authentication, xUnit, Shouldly, `Microsoft.AspNetCore.Mvc.Testing`, Testcontainers.PostgreSql.

## Global Constraints

See `../README.md` → Global Constraints. Depends on: `m3/01` (the `Custodex.Service` host and gRPC services) and `m3/02` (REST surface). The handlers from those plans must read the request-scoped `TenantContext` produced here instead of trusting a client-supplied value.

---

### Task 1: API-key authentication scheme

**Files:**
- Create: `src/Custodex.Service/Auth/ApiKeyAuthenticationHandler.cs`
- Create: `src/Custodex.Service/Auth/ApiKeyOptions.cs`
- Test: `tests/Custodex.Service.Tests/Auth/ApiKeyAuthTests.cs`

**Interfaces:**
- Produces: an `"ApiKey"` authentication scheme validating the `X-Custodex-Key` header against configured keys, each mapped to a store and a role (`reader` or `admin`) emitted as claims (`Custodex:store`, `Custodex:role`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Service.Tests/Auth/ApiKeyAuthTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Auth;

public class ApiKeyAuthTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Missing_key_is_rejected()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsync("/check", JsonContent.For(SampleCheck.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_reader_key_is_accepted_for_check()
    {
        var client = factory.WithKey("reader-key");
        var resp = await client.PostAsync("/check", JsonContent.For(SampleCheck.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter ApiKeyAuthTests`
Expected: FAIL — scheme not registered; requests are anonymous.

- [ ] **Step 3: Implement the options and handler**

```csharp
// src/Custodex.Service/Auth/ApiKeyOptions.cs
using Microsoft.AspNetCore.Authentication;

namespace Custodex.Service.Auth;

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Custodex-Key";
    // key -> (store, role)
    public Dictionary<string, (string Store, string Role)> Keys { get; } = new();
}
```

```csharp
// src/Custodex.Service/Auth/ApiKeyAuthenticationHandler.cs
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.Service.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyOptions.HeaderName, out var provided) || provided.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!Options.Keys.TryGetValue(provided.ToString(), out var mapping))
            return Task.FromResult(AuthenticateResult.Fail("Unknown API key."));

        var claims = new[]
        {
            new Claim("Custodex:store", mapping.Store),
            new Claim("Custodex:role", mapping.Role),
            new Claim(ClaimTypes.NameIdentifier, $"apikey:{mapping.Store}")
        };
        var identity = new ClaimsIdentity(claims, ApiKeyOptions.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyOptions.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 4: Register the scheme in the host** (`Program.cs`, add to the existing builder from m3/01)

```csharp
builder.Services.AddAuthentication(ApiKeyOptions.Scheme)
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(ApiKeyOptions.Scheme, opts =>
    {
        // bound from configuration in real deployments; seeded here for tests
        builder.Configuration.GetSection("Custodex:ApiKeys").Bind(opts.Keys);
    });
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter ApiKeyAuthTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Service/Auth tests/Custodex.Service.Tests/Auth
git commit -m "feat: add API-key authentication to the service"
```

---

### Task 2: OIDC bearer scheme alongside API keys

**Files:**
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/Auth/OidcAuthTests.cs`

**Interfaces:**
- Produces: a `"Bearer"` JWT scheme; a policy scheme that accepts EITHER `ApiKey` or `Bearer`. The bearer's `store`/`role` come from configured claim names (default `Custodex:store`, `Custodex:role`).

- [ ] **Step 1: Write the failing test** (uses a test JWT signed with a known dev key)

```csharp
// tests/Custodex.Service.Tests/Auth/OidcAuthTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Auth;

public class OidcAuthTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Valid_bearer_token_is_accepted()
    {
        var client = factory.WithBearer(TestTokens.Reader(store: "zoo"));
        var resp = await client.PostAsync("/check", JsonContent.For(SampleCheck.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter OidcAuthTests`
Expected: FAIL — no bearer scheme.

- [ ] **Step 3: Add the bearer + composite policy scheme**

```csharp
// add to Program.cs
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "Custodex-any";
    })
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(ApiKeyOptions.Scheme, opts =>
        builder.Configuration.GetSection("Custodex:ApiKeys").Bind(opts.Keys))
    .AddJwtBearer("Bearer", opts =>
    {
        builder.Configuration.GetSection("Custodex:Jwt").Bind(opts);   // Authority/Audience or test signing key
    })
    .AddPolicyScheme("Custodex-any", "ApiKey or Bearer", opts =>
    {
        opts.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey(ApiKeyOptions.HeaderName) ? ApiKeyOptions.Scheme : "Bearer";
    });
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter OidcAuthTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests/Auth
git commit -m "feat: add OIDC bearer auth alongside API keys"
```

---

### Task 3: Store/tenant resolution into a request-scoped TenantContext

**Files:**
- Create: `src/Custodex.Service/Tenancy/TenantContextAccessor.cs`
- Create: `src/Custodex.Service/Tenancy/TenantResolutionMiddleware.cs`
- Test: `tests/Custodex.Service.Tests/Tenancy/TenantResolutionTests.cs`

**Interfaces:**
- Produces: `ITenantContextAccessor { TenantContext Current { get; } }` (request-scoped). Store comes from the `Custodex:store` claim; tenant comes from the `X-Custodex-Tenant` header (or `Custodex:tenant` claim). A caller cannot act on a store other than the one their credential authorizes.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Service.Tests/Tenancy/TenantResolutionTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Tenancy;

public class TenantResolutionTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Request_without_tenant_header_is_bad_request()
    {
        var client = factory.WithKey("reader-key");   // store=zoo
        var resp = await client.PostAsync("/check", JsonContent.For(SampleCheck.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resolved_context_uses_claim_store_and_header_tenant()
    {
        var client = factory.WithKey("reader-key");
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "sydney-zoo");
        var resp = await client.PostAsync("/echo-tenant", null);   // test-only endpoint echoing TenantContext
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("zoo");          // store from claim
        body.ShouldContain("sydney-zoo");   // tenant from header
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter TenantResolutionTests`
Expected: FAIL — middleware/accessor not present.

- [ ] **Step 3: Implement the accessor and middleware**

```csharp
// src/Custodex.Service/Tenancy/TenantContextAccessor.cs
using Custodex.Abstractions;

namespace Custodex.Service.Tenancy;

public interface ITenantContextAccessor { TenantContext Current { get; } }

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    public TenantContext Current { get; internal set; }
    internal bool Resolved { get; set; }
}
```

```csharp
// src/Custodex.Service/Tenancy/TenantResolutionMiddleware.cs
using Custodex.Abstractions;

namespace Custodex.Service.Tenancy;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, ITenantContextAccessor accessor)
    {
        var store = ctx.User.FindFirst("Custodex:store")?.Value;
        var tenant = ctx.Request.Headers.TryGetValue("X-Custodex-Tenant", out var h) && h.Count > 0
            ? h.ToString()
            : ctx.User.FindFirst("Custodex:tenant")?.Value;

        if (store is null || string.IsNullOrEmpty(tenant))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Missing store (claim) or tenant (X-Custodex-Tenant header).");
            return;
        }

        ((TenantContextAccessor)accessor).Current = new TenantContext(store, tenant);
        ((TenantContextAccessor)accessor).Resolved = true;
        await next(ctx);
    }
}
```

- [ ] **Step 4: Wire it up** (`Program.cs`)

```csharp
builder.Services.AddScoped<ITenantContextAccessor, TenantContextAccessor>();
// after UseAuthentication/UseAuthorization:
app.UseMiddleware<TenantResolutionMiddleware>();
```

> Update the m3/01 gRPC services and m3/02 REST handlers to take `ITenantContextAccessor` and use `accessor.Current` for the `TenantContext` argument, ignoring any client-supplied store/tenant. Show that change for one Check handler as the representative edit.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter TenantResolutionTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.Service/Tenancy tests/Custodex.Service.Tests/Tenancy
git commit -m "feat: resolve store/tenant into request-scoped TenantContext"
```

---

### Task 4: Authorization policies — readers vs admins

**Files:**
- Modify: `src/Custodex.Service/Program.cs`
- Test: `tests/Custodex.Service.Tests/Auth/PolicyTests.cs`

**Interfaces:**
- Produces: two policies — `"Custodex:decide"` (role `reader` or `admin`) on decision endpoints, `"Custodex:manage"` (role `admin`) on management endpoints.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Custodex.Service.Tests/Auth/PolicyTests.cs
using System.Net;
using Shouldly;
using Xunit;

namespace Custodex.Service.Tests.Auth;

public class PolicyTests(CustodexServiceFactory factory) : IClassFixture<CustodexServiceFactory>
{
    [Fact]
    public async Task Reader_cannot_write_tuples()
    {
        var client = factory.WithKey("reader-key");
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "sydney-zoo");
        var resp = await client.PostAsync("/tuples", JsonContent.For(SampleWrite.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_can_write_tuples()
    {
        var client = factory.WithKey("admin-key");
        client.DefaultRequestHeaders.Add("X-Custodex-Tenant", "sydney-zoo");
        var resp = await client.PostAsync("/tuples", JsonContent.For(SampleWrite.Request));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Custodex.Service.Tests --filter PolicyTests`
Expected: FAIL — policies not defined; management endpoint allows readers.

- [ ] **Step 3: Define and apply the policies**

```csharp
// Program.cs
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Custodex:decide", p => p.RequireClaim("Custodex:role", "reader", "admin"))
    .AddPolicy("Custodex:manage", p => p.RequireClaim("Custodex:role", "admin"));
```

Apply `RequireAuthorization("Custodex:decide")` to `/check`, `/batch-check`, `/list-objects`, `/list-subjects` and the gRPC decision methods; apply `"Custodex:manage"` to `/tuples`, `/schema`, `/stores`, `/tenants` and the gRPC management methods. Show the mapping for `/check` and `/tuples` explicitly.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Custodex.Service.Tests --filter PolicyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.Service tests/Custodex.Service.Tests/Auth
git commit -m "feat: gate management endpoints behind admin policy"
```

---

## Self-review checklist (run after all tasks)

- [ ] Handlers use the resolved `TenantContext`, never a client-supplied store/tenant body field.
- [ ] A credential scoped to store A cannot read or write store B's data (claim-derived store).
- [ ] Decision vs management endpoints enforce distinct policies; a reader key is rejected on every mutation.
- [ ] Both API-key and bearer paths reach the same resolved-tenant behaviour.

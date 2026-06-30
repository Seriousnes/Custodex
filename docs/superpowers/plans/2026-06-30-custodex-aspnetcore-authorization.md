# Custodex ASP.NET Core authorization adapter — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Custodex.AspNetCore` and `Custodex.Blazor` so any ASP.NET Core app — including server-side Blazor — authorizes through stock `[Authorize("custodex:type:perm")]`, `RequireAuthorization("custodex:type:perm")`, `<AuthorizeView Policy="custodex:type:perm">`, and a `<CustodexAuthorizeView>` component, all backed by `IAuthorizer.CheckAsync`, with no Custodex-specific authorization attribute.

**Architecture:** A dynamic `IAuthorizationPolicyProvider` turns a `custodex:`-prefixed policy name into a `CustodexRequirement(type, permission)`; an `AuthorizationHandler<CustodexRequirement>` resolves subject, tenant, object, and request context through replaceable seams and calls the DI-registered `IAuthorizer`. The Blazor component is a thin wrapper over `<AuthorizeView>`, so there is one evaluation path. The adapter binds only to `IAuthorizer`, so it is identical for the in-process and remote-client engines.

**Tech Stack:** .NET 10 / C# latest, ASP.NET Core shared framework (`Microsoft.AspNetCore.App`), `Microsoft.NET.Sdk.Razor` for the Blazor package, xUnit + Shouldly + bUnit + `Microsoft.AspNetCore.Mvc.Testing` (TestHost) for tests, `Custodex.TestKit` (`TestWorld`) for domain-neutral identifiers.

## Global Constraints

- Target framework `net10.0`; `LangVersion=latest`; `Nullable=enable`; `ImplicitUsings=enable` (inherited from `Directory.Build.props`).
- `TreatWarningsAsErrors=true` — a warning fails the build. With `GenerateDocumentationFile=true`, **CS1591 is fatal**: every `public` type and member needs an XML `///` doc comment.
- Comment rule: only `///` on `public`/`protected` members of public types. No `//`, `/* */`, or `///` on any `internal`/`private`/file-local declaration, lambda, or method body.
- Assertions use **Shouldly** (never FluentAssertions). Tests use **xUnit**.
- Test identifiers come from `Custodex.TestKit.TestWorld`; no literal domain vocabulary in `tests/` (a guard scans the tree). Policy-name strings in tests are built from generated `TestWorld` type/permission values. The only allowed literal is the wildcard `"*"`.
- A packable project sets `IsPackable=true`, a **distinct** `MinVerTagPrefix`, `GenerateDocumentationFile=true`, `PackageReadmeFile=README.md` with `<None Update="README.md" Pack="true" PackagePath="\" />`, and a `<Description>`. The per-package release run hard-fails a packable project missing `IsPackable` or `MinVerTagPrefix`.
- All four new projects are added to `Custodex.slnx`.
- New NuGet dependencies are added centrally in `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`); project files reference packages without a `Version`.
- Determinism: the evaluation path reads time only from `RequestContext.Now`; the adapter sources `Now` from the registered `TimeProvider`, never `DateTimeOffset.UtcNow`.
- Internals are tested via `<InternalsVisibleTo>` (the repo convention), keeping concrete implementations `internal sealed`.

---

### Task 1: Scaffold `Custodex.AspNetCore` + foundational types + test project

**Files:**
- Create: `src/Custodex.AspNetCore/Custodex.AspNetCore.csproj`
- Create: `src/Custodex.AspNetCore/README.md`
- Create: `src/Custodex.AspNetCore/CustodexClaimTypes.cs`
- Create: `src/Custodex.AspNetCore/CustodexHeaders.cs`
- Create: `src/Custodex.AspNetCore/CustodexAuthorizationOptions.cs`
- Create: `src/Custodex.AspNetCore/CustodexResolutionContext.cs`
- Create: `src/Custodex.AspNetCore/CustodexRequirement.cs`
- Create: `tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj`
- Create: `tests/Custodex.AspNetCore.Tests/OptionDefaultsTests.cs`
- Modify: `Custodex.slnx`

**Interfaces:**
- Produces: `CustodexClaimTypes.Store` = `"Custodex:store"`, `CustodexClaimTypes.Tenant` = `"Custodex:tenant"`; `CustodexHeaders.Tenant` = `"X-Custodex-Tenant"`. `CustodexAuthorizationOptions` (mutable class, defaults below). `CustodexResolutionContext` (public, getters `ObjectType`, `Permission`, `User`, `Resource`, `HttpContext`, `Tenant`; internal constructor). `CustodexRequirement(string objectType, string permission)` (`internal sealed : IAuthorizationRequirement`, props `ObjectType`, `Permission`).

- [ ] **Step 1: Create the package project file**

`src/Custodex.AspNetCore/Custodex.AspNetCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<IsPackable>true</IsPackable>
		<MinVerTagPrefix>aspnetcore-v</MinVerTagPrefix>
		<PackageReadmeFile>README.md</PackageReadmeFile>
		<GenerateDocumentationFile>true</GenerateDocumentationFile>
		<Description>ASP.NET Core authorization adapter for Custodex: stock [Authorize], RequireAuthorization, and AuthorizeView policies resolve against the Custodex engine through a dynamic policy provider and resource-based handler.</Description>
	</PropertyGroup>

	<ItemGroup>
		<FrameworkReference Include="Microsoft.AspNetCore.App" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Custodex.Abstractions\Custodex.Abstractions.csproj" />
	</ItemGroup>

	<ItemGroup>
		<None Update="README.md" Pack="true" PackagePath="\" />
	</ItemGroup>

	<ItemGroup>
		<InternalsVisibleTo Include="Custodex.AspNetCore.Tests" />
	</ItemGroup>

</Project>
```

- [ ] **Step 2: Create the package README**

`src/Custodex.AspNetCore/README.md`:

```markdown
# Custodex.AspNetCore

ASP.NET Core authorization adapter for the Custodex ReBAC + ABAC engine.

Register the engine (`AddCustodex().Use<provider>()` or `AddCustodexClient(...)`) and then:

```csharp
builder.Services.AddCustodexAuthorization(options =>
{
    options.SubjectType = "user";
    options.RootObject = ctx => new EntityRef("tenant", ctx.Tenant.Tenant);
});
```

Stock framework authorization resolves against Custodex with no custom attribute:

```csharp
app.MapGet("/documents/{id}", GetDocument)
   .RequireAuthorization("custodex:document:view");

[Authorize("custodex:document:edit")]
public IActionResult Edit(string id) => ...;
```

The policy name is `custodex:{type}:{permission}`. The object id is taken from the route
(`{type}Id`, `{type}`, or `id`) or from the `AuthorizeView` `Resource`. Unknown policy names
fall through to the framework's default provider, so existing named policies keep working.
```

- [ ] **Step 3: Create the claim-type and header constants**

`src/Custodex.AspNetCore/CustodexClaimTypes.cs`:

```csharp
namespace Custodex.AspNetCore;

/// <summary>The claim types the default Custodex resolvers read from the authenticated principal.</summary>
public static class CustodexClaimTypes
{
    /// <summary>The claim carrying the Custodex store id (<c>Custodex:store</c>).</summary>
    public const string Store = "Custodex:store";

    /// <summary>The claim carrying the Custodex tenant id (<c>Custodex:tenant</c>), used when no tenant header is present.</summary>
    public const string Tenant = "Custodex:tenant";
}
```

`src/Custodex.AspNetCore/CustodexHeaders.cs`:

```csharp
namespace Custodex.AspNetCore;

/// <summary>The request headers the default Custodex resolvers read.</summary>
public static class CustodexHeaders
{
    /// <summary>The header carrying the Custodex tenant id (<c>X-Custodex-Tenant</c>).</summary>
    public const string Tenant = "X-Custodex-Tenant";
}
```

- [ ] **Step 4: Create the options class**

`src/Custodex.AspNetCore/CustodexAuthorizationOptions.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// Configures how the ASP.NET Core adapter maps a request to a Custodex check: the policy-name
/// shape, the subject and tenant claim names, the fallback object for object-less policies, and
/// how engine errors are surfaced.
/// </summary>
public sealed class CustodexAuthorizationOptions
{
    /// <summary>The leading segment that marks a policy name as a Custodex policy. Defaults to <c>custodex</c>.</summary>
    public string PolicyPrefix { get; set; } = "custodex";

    /// <summary>The separator between policy-name segments. Defaults to <c>:</c>.</summary>
    public string PolicySeparator { get; set; } = ":";

    /// <summary>The subject entity type used for every resolved subject. Defaults to <c>user</c>.</summary>
    public string SubjectType { get; set; } = "user";

    /// <summary>The claim whose value is the subject id. Defaults to <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public string SubjectIdClaim { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>The claim whose value is the store id. Defaults to <see cref="CustodexClaimTypes.Store"/>.</summary>
    public string StoreClaim { get; set; } = CustodexClaimTypes.Store;

    /// <summary>The claim whose value is the tenant id when no tenant header is present. Defaults to <see cref="CustodexClaimTypes.Tenant"/>.</summary>
    public string TenantClaim { get; set; } = CustodexClaimTypes.Tenant;

    /// <summary>The header whose value is the tenant id. Defaults to <see cref="CustodexHeaders.Tenant"/>.</summary>
    public string TenantHeader { get; set; } = CustodexHeaders.Tenant;

    /// <summary>
    /// Produces the object to evaluate when no specific object can be resolved from the resource or
    /// route (for example a page-level policy). Returns <see langword="null"/> to deny. Defaults to
    /// <see langword="null"/>.
    /// </summary>
    public Func<CustodexResolutionContext, EntityRef?>? RootObject { get; set; }

    /// <summary>
    /// When <see langword="true"/>, an exception from the engine is rethrown; when
    /// <see langword="false"/> (the default) it is logged and treated as a denial.
    /// </summary>
    public bool ThrowOnEvaluationError { get; set; }
}
```

- [ ] **Step 5: Create the resolution context**

`src/Custodex.AspNetCore/CustodexResolutionContext.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

/// <summary>
/// The per-request inputs a Custodex resolver sees: the policy's object type and permission, the
/// authenticated principal, the authorization resource, the ambient <see cref="HttpContext"/> (when
/// one exists), and the resolved tenant.
/// </summary>
public sealed class CustodexResolutionContext
{
    internal CustodexResolutionContext(
        string objectType, string permission, ClaimsPrincipal user,
        object? resource, HttpContext? httpContext, TenantContext tenant)
    {
        ObjectType = objectType;
        Permission = permission;
        User = user;
        Resource = resource;
        HttpContext = httpContext;
        Tenant = tenant;
    }

    /// <summary>The object entity type parsed from the policy name.</summary>
    public string ObjectType { get; }

    /// <summary>The permission parsed from the policy name.</summary>
    public string Permission { get; }

    /// <summary>The authenticated principal the decision concerns.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary>The authorization resource passed by the caller (for example an <see cref="AuthorizeView"/> resource), or <see langword="null"/>.</summary>
    public object? Resource { get; }

    /// <summary>The ambient request context, or <see langword="null"/> outside an HTTP request (for example in a Blazor circuit).</summary>
    public HttpContext? HttpContext { get; }

    /// <summary>The store and tenant the decision is evaluated within.</summary>
    public TenantContext Tenant { get; }
}
```

- [ ] **Step 6: Create the requirement**

`src/Custodex.AspNetCore/CustodexRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace Custodex.AspNetCore;

internal sealed class CustodexRequirement(string objectType, string permission) : IAuthorizationRequirement
{
    public string ObjectType { get; } = objectType;

    public string Permission { get; } = permission;
}
```

- [ ] **Step 7: Create the test project file**

`tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Custodex.AspNetCore\Custodex.AspNetCore.csproj" />
    <ProjectReference Include="..\Custodex.TestKit\Custodex.TestKit.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 8: Write the failing defaults test**

`tests/Custodex.AspNetCore.Tests/OptionDefaultsTests.cs`:

```csharp
using System.Security.Claims;

using Custodex.AspNetCore;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class OptionDefaultsTests
{
    [Fact]
    public void Options_carry_the_documented_defaults()
    {
        var options = new CustodexAuthorizationOptions();

        options.PolicyPrefix.ShouldBe("custodex");
        options.PolicySeparator.ShouldBe(":");
        options.SubjectType.ShouldBe("user");
        options.SubjectIdClaim.ShouldBe(ClaimTypes.NameIdentifier);
        options.StoreClaim.ShouldBe("Custodex:store");
        options.TenantClaim.ShouldBe("Custodex:tenant");
        options.TenantHeader.ShouldBe("X-Custodex-Tenant");
        options.RootObject.ShouldBeNull();
        options.ThrowOnEvaluationError.ShouldBeFalse();
    }

    [Fact]
    public void Constants_match_the_service_wire_values()
    {
        CustodexClaimTypes.Store.ShouldBe("Custodex:store");
        CustodexClaimTypes.Tenant.ShouldBe("Custodex:tenant");
        CustodexHeaders.Tenant.ShouldBe("X-Custodex-Tenant");
    }
}
```

- [ ] **Step 9: Add both projects to the solution**

Edit `Custodex.slnx`. In the `/src/` folder add:

```xml
    <Project Path="src/Custodex.AspNetCore/Custodex.AspNetCore.csproj" />
```

In the `/tests/` folder add:

```xml
    <Project Path="tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj" />
```

- [ ] **Step 10: Build and run the test**

Run: `dotnet test tests/Custodex.AspNetCore.Tests`
Expected: PASS (both tests green; the project builds and packs cleanly).

- [ ] **Step 11: Commit**

```bash
git add src/Custodex.AspNetCore tests/Custodex.AspNetCore.Tests Custodex.slnx
git commit -m "feat(aspnetcore): scaffold Custodex.AspNetCore package and options"
```

---

### Task 2: `CustodexPolicyProvider`

**Files:**
- Create: `src/Custodex.AspNetCore/Policy/CustodexPolicyProvider.cs`
- Test: `tests/Custodex.AspNetCore.Tests/PolicyProviderTests.cs`

**Interfaces:**
- Consumes: `CustodexAuthorizationOptions` (`PolicyPrefix`, `PolicySeparator`); `CustodexRequirement(objectType, permission)`.
- Produces: `internal sealed class CustodexPolicyProvider : IAuthorizationPolicyProvider` with constructor `(IOptions<AuthorizationOptions>, IOptions<CustodexAuthorizationOptions>)`. A matching policy has `RequireAuthenticatedUser()` plus one `CustodexRequirement`.

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/PolicyProviderTests.cs`:

```csharp
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class PolicyProviderTests
{
    private static CustodexPolicyProvider Provider(CustodexAuthorizationOptions? options = null) =>
        new(Options.Create(new AuthorizationOptions()),
            Options.Create(options ?? new CustodexAuthorizationOptions()));

    [Fact]
    public async Task Custodex_policy_name_yields_authenticated_user_plus_requirement()
    {
        var policy = await Provider().GetPolicyAsync("custodex:thing:view");

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
        var requirement = policy.Requirements.OfType<CustodexRequirement>().ShouldHaveSingleItem();
        requirement.ObjectType.ShouldBe("thing");
        requirement.Permission.ShouldBe("view");
    }

    [Fact]
    public async Task Non_custodex_policy_name_is_not_handled()
    {
        var policy = await Provider().GetPolicyAsync("SomeExistingPolicy");

        policy.ShouldBeNull();
    }

    [Fact]
    public async Task Wrong_segment_count_is_not_handled()
    {
        (await Provider().GetPolicyAsync("custodex:thing")).ShouldBeNull();
        (await Provider().GetPolicyAsync("custodex:thing:view:extra")).ShouldBeNull();
        (await Provider().GetPolicyAsync("custodex::view")).ShouldBeNull();
    }

    [Fact]
    public async Task Same_name_returns_the_cached_policy_instance()
    {
        var provider = Provider();

        var first = await provider.GetPolicyAsync("custodex:thing:view");
        var second = await provider.GetPolicyAsync("custodex:thing:view");

        first.ShouldBeSameAs(second);
    }

    [Fact]
    public async Task Custom_prefix_and_separator_are_honoured()
    {
        var provider = Provider(new CustodexAuthorizationOptions { PolicyPrefix = "cdx", PolicySeparator = "/" });

        var policy = await provider.GetPolicyAsync("cdx/thing/view");

        policy.ShouldNotBeNull();
        policy.Requirements.OfType<CustodexRequirement>().ShouldHaveSingleItem().Permission.ShouldBe("view");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~PolicyProviderTests"`
Expected: FAIL — `CustodexPolicyProvider` does not exist.

- [ ] **Step 3: Implement the policy provider**

`src/Custodex.AspNetCore/Policy/CustodexPolicyProvider.cs`:

```csharp
using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _inner;
    private readonly CustodexAuthorizationOptions _options;
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public CustodexPolicyProvider(
        IOptions<AuthorizationOptions> authorizationOptions,
        IOptions<CustodexAuthorizationOptions> options)
    {
        _inner = new DefaultAuthorizationPolicyProvider(authorizationOptions);
        _options = options.Value;
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _inner.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _inner.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (TryParse(policyName, out var type, out var permission))
        {
            var policy = _cache.GetOrAdd(policyName, _ => new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new CustodexRequirement(type, permission))
                .Build());
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _inner.GetPolicyAsync(policyName);
    }

    private bool TryParse(string name, out string type, out string permission)
    {
        type = string.Empty;
        permission = string.Empty;

        var prefix = _options.PolicyPrefix + _options.PolicySeparator;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var parts = name.Split(_options.PolicySeparator);
        if (parts.Length != 3 || parts[0] != _options.PolicyPrefix || parts[1].Length == 0 || parts[2].Length == 0)
            return false;

        type = parts[1];
        permission = parts[2];
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~PolicyProviderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.AspNetCore/Policy tests/Custodex.AspNetCore.Tests/PolicyProviderTests.cs
git commit -m "feat(aspnetcore): dynamic policy provider for custodex policy names"
```

---

### Task 3: Subject resolver

**Files:**
- Create: `src/Custodex.AspNetCore/Resolution/ICustodexSubjectResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/ClaimsCustodexSubjectResolver.cs`
- Test: `tests/Custodex.AspNetCore.Tests/SubjectResolverTests.cs`

**Interfaces:**
- Consumes: `CustodexAuthorizationOptions` (`SubjectIdClaim`, `SubjectType`); `SubjectRef` from `Custodex.Abstractions`.
- Produces: `public interface ICustodexSubjectResolver { bool TryResolve(ClaimsPrincipal user, out SubjectRef subject); }`; default `internal sealed ClaimsCustodexSubjectResolver(IOptions<CustodexAuthorizationOptions>)`.

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/SubjectResolverTests.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class SubjectResolverTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void Resolves_subject_from_name_identifier_claim()
    {
        var world = TestWorld.New();
        var id = world.SubjectId();
        var resolver = new ClaimsCustodexSubjectResolver(
            Options.Create(new CustodexAuthorizationOptions { SubjectType = world.UserType }));

        resolver.TryResolve(Principal(new Claim(ClaimTypes.NameIdentifier, id)), out var subject).ShouldBeTrue();

        subject.ShouldBe(new SubjectRef(world.UserType, id));
    }

    [Fact]
    public void Missing_subject_claim_does_not_resolve()
    {
        var resolver = new ClaimsCustodexSubjectResolver(Options.Create(new CustodexAuthorizationOptions()));

        resolver.TryResolve(Principal(), out _).ShouldBeFalse();
    }

    [Fact]
    public void Honours_a_custom_subject_id_claim()
    {
        var world = TestWorld.New();
        var id = world.SubjectId();
        var resolver = new ClaimsCustodexSubjectResolver(Options.Create(
            new CustodexAuthorizationOptions { SubjectIdClaim = "sub", SubjectType = world.UserType }));

        resolver.TryResolve(Principal(new Claim("sub", id)), out var subject).ShouldBeTrue();

        subject.ShouldBe(new SubjectRef(world.UserType, id));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~SubjectResolverTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interface and default resolver**

`src/Custodex.AspNetCore/Resolution/ICustodexSubjectResolver.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>Maps the authenticated principal to the Custodex subject a decision is evaluated for.</summary>
public interface ICustodexSubjectResolver
{
    /// <summary>Attempts to resolve a subject from <paramref name="user"/>.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="subject">The resolved subject when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a subject was resolved; otherwise <see langword="false"/>, which denies the request.</returns>
    bool TryResolve(ClaimsPrincipal user, out SubjectRef subject);
}
```

`src/Custodex.AspNetCore/Resolution/ClaimsCustodexSubjectResolver.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class ClaimsCustodexSubjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexSubjectResolver
{
    public bool TryResolve(ClaimsPrincipal user, out SubjectRef subject)
    {
        subject = default;
        var id = user.FindFirstValue(options.Value.SubjectIdClaim);
        if (string.IsNullOrEmpty(id))
            return false;

        subject = new SubjectRef(options.Value.SubjectType, id);
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~SubjectResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.AspNetCore/Resolution tests/Custodex.AspNetCore.Tests/SubjectResolverTests.cs
git commit -m "feat(aspnetcore): claims-based subject resolver"
```

---

### Task 4: Tenant resolver

**Files:**
- Create: `src/Custodex.AspNetCore/Resolution/ICustodexTenantResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/ClaimsHeaderCustodexTenantResolver.cs`
- Test: `tests/Custodex.AspNetCore.Tests/TenantResolverTests.cs`

**Interfaces:**
- Consumes: `CustodexAuthorizationOptions` (`StoreClaim`, `TenantClaim`, `TenantHeader`); `TenantContext` from `Custodex.Abstractions`.
- Produces: `public interface ICustodexTenantResolver { bool TryResolve(ClaimsPrincipal user, HttpContext? httpContext, out TenantContext tenant); }`; default `internal sealed ClaimsHeaderCustodexTenantResolver(IOptions<CustodexAuthorizationOptions>)`.

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/TenantResolverTests.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class TenantResolverTests
{
    private static readonly ClaimsHeaderCustodexTenantResolver Resolver =
        new(Options.Create(new CustodexAuthorizationOptions()));

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void Store_from_claim_tenant_from_header()
    {
        var world = TestWorld.New();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Custodex-Tenant"] = world.Tenant.Tenant;

        Resolver.TryResolve(Principal(new Claim("Custodex:store", world.Tenant.Store)), http, out var tenant).ShouldBeTrue();

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public void Tenant_falls_back_to_claim_when_header_absent()
    {
        var world = TestWorld.New();

        Resolver.TryResolve(
            Principal(
                new Claim("Custodex:store", world.Tenant.Store),
                new Claim("Custodex:tenant", world.Tenant.Tenant)),
            httpContext: null, out var tenant).ShouldBeTrue();

        tenant.ShouldBe(world.Tenant);
    }

    [Fact]
    public void Missing_store_does_not_resolve()
    {
        var world = TestWorld.New();

        Resolver.TryResolve(Principal(new Claim("Custodex:tenant", world.Tenant.Tenant)), null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Missing_tenant_does_not_resolve()
    {
        var world = TestWorld.New();

        Resolver.TryResolve(Principal(new Claim("Custodex:store", world.Tenant.Store)), null, out _).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~TenantResolverTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interface and default resolver**

`src/Custodex.AspNetCore/Resolution/ICustodexTenantResolver.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

/// <summary>Resolves the store and tenant a decision is evaluated within.</summary>
public interface ICustodexTenantResolver
{
    /// <summary>Attempts to resolve the tenant scope for the current request.</summary>
    /// <param name="user">The authenticated principal.</param>
    /// <param name="httpContext">The ambient request, or <see langword="null"/> outside an HTTP request.</param>
    /// <param name="tenant">The resolved store and tenant when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a tenant was resolved; otherwise <see langword="false"/>, which denies the request.</returns>
    bool TryResolve(ClaimsPrincipal user, HttpContext? httpContext, out TenantContext tenant);
}
```

`src/Custodex.AspNetCore/Resolution/ClaimsHeaderCustodexTenantResolver.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class ClaimsHeaderCustodexTenantResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexTenantResolver
{
    public bool TryResolve(ClaimsPrincipal user, HttpContext? httpContext, out TenantContext tenant)
    {
        tenant = default;
        var o = options.Value;

        var store = user.FindFirstValue(o.StoreClaim);
        if (string.IsNullOrEmpty(store))
            return false;

        string? tenantId = null;
        if (httpContext is not null && httpContext.Request.Headers.TryGetValue(o.TenantHeader, out var header))
            tenantId = header.ToString();
        if (string.IsNullOrEmpty(tenantId))
            tenantId = user.FindFirstValue(o.TenantClaim);
        if (string.IsNullOrEmpty(tenantId))
            return false;

        tenant = new TenantContext(store, tenantId);
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~TenantResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.AspNetCore/Resolution/ICustodexTenantResolver.cs src/Custodex.AspNetCore/Resolution/ClaimsHeaderCustodexTenantResolver.cs tests/Custodex.AspNetCore.Tests/TenantResolverTests.cs
git commit -m "feat(aspnetcore): claims/header tenant resolver"
```

---

### Task 5: Object resolvers + binding metadata + endpoint extension

**Files:**
- Create: `src/Custodex.AspNetCore/Resolution/ICustodexObjectResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/ResourceEntityRefResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/ResourceIdResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/RouteValueResolver.cs`
- Create: `src/Custodex.AspNetCore/Resolution/RootObjectResolver.cs`
- Create: `src/Custodex.AspNetCore/Routing/CustodexObjectBindingMetadata.cs`
- Create: `src/Custodex.AspNetCore/Routing/CustodexEndpointConventionBuilderExtensions.cs`
- Test: `tests/Custodex.AspNetCore.Tests/ObjectResolverTests.cs`

**Interfaces:**
- Consumes: `CustodexResolutionContext` (`ObjectType`, `Resource`, `HttpContext`); `CustodexAuthorizationOptions.RootObject`; `EntityRef`.
- Produces: `public interface ICustodexObjectResolver { bool TryResolve(CustodexResolutionContext context, out EntityRef entity); }`; four `internal sealed` resolvers; `public sealed class CustodexObjectBindingMetadata(string? routeKey = null, string? objectType = null)`; `public static TBuilder WithCustodexObject<TBuilder>(this TBuilder, string? routeKey = null, string? type = null) where TBuilder : IEndpointConventionBuilder`. The DI registration order is resource-entity → resource-id → route-value → root-object (established in Task 8).

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/ObjectResolverTests.cs`:

```csharp
using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class ObjectResolverTests
{
    private static CustodexResolutionContext Context(string objectType, object? resource = null, HttpContext? http = null) =>
        ResolutionContextFactory.Create(objectType, "view", resource, http, new TenantContext("s", "t"));

    [Fact]
    public void Resource_entity_ref_is_used_directly()
    {
        var world = TestWorld.New();
        var entity = new EntityRef(world.EntityType(), world.ObjectId());

        new ResourceEntityRefResolver().TryResolve(Context(world.EntityType(), resource: entity), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(entity);
    }

    [Fact]
    public void Resource_string_combines_with_policy_type()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();

        new ResourceIdResolver().TryResolve(Context(type, resource: id), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Empty_resource_string_does_not_resolve()
    {
        new ResourceIdResolver().TryResolve(Context("thing", resource: ""), out _).ShouldBeFalse();
    }

    [Fact]
    public void Route_value_binds_by_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues["id"] = id;

        new RouteValueResolver().TryResolve(Context(type, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Route_value_binds_by_type_id_key()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues[type + "Id"] = id;

        new RouteValueResolver().TryResolve(Context(type, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(type, id));
    }

    [Fact]
    public void Route_value_binding_metadata_overrides_key_and_type()
    {
        var world = TestWorld.New();
        var policyType = world.EntityType();
        var boundType = world.EntityType();
        var id = world.ObjectId();
        var http = new DefaultHttpContext();
        http.Request.RouteValues["slug"] = id;
        http.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new CustodexObjectBindingMetadata("slug", boundType)),
            "test"));

        new RouteValueResolver().TryResolve(Context(policyType, http: http), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(new EntityRef(boundType, id));
    }

    [Fact]
    public void No_http_context_does_not_resolve_from_route()
    {
        new RouteValueResolver().TryResolve(Context("thing"), out _).ShouldBeFalse();
    }

    [Fact]
    public void Root_object_resolves_from_options_delegate()
    {
        var world = TestWorld.New();
        var root = new EntityRef(world.EntityType(), world.ObjectId());
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions { RootObject = _ => root }));

        resolver.TryResolve(Context("thing"), out var resolved).ShouldBeTrue();

        resolved.ShouldBe(root);
    }

    [Fact]
    public void Root_object_absent_does_not_resolve()
    {
        var resolver = new RootObjectResolver(Options.Create(new CustodexAuthorizationOptions()));

        resolver.TryResolve(Context("thing"), out _).ShouldBeFalse();
    }
}
```

This test uses a small internal helper to build a `CustodexResolutionContext` (its constructor is internal). Create it in the same step:

`tests/Custodex.AspNetCore.Tests/ResolutionContextFactory.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore.Tests;

internal static class ResolutionContextFactory
{
    public static CustodexResolutionContext Create(
        string objectType, string permission, object? resource, HttpContext? http, TenantContext tenant) =>
        new(objectType, permission, new ClaimsPrincipal(new ClaimsIdentity()), resource, http, tenant);
}
```

(The `internal` constructor of `CustodexResolutionContext` is reachable because the package grants `InternalsVisibleTo` to the test assembly.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~ObjectResolverTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interface, metadata, extension, and four resolvers**

`src/Custodex.AspNetCore/Resolution/ICustodexObjectResolver.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>
/// Resolves the object a decision is evaluated against. Registered resolvers are tried in order;
/// the first that returns <see langword="true"/> wins. Register a custom resolver to map an
/// application's domain object (passed as an authorization resource) to an <see cref="EntityRef"/>.
/// </summary>
public interface ICustodexObjectResolver
{
    /// <summary>Attempts to resolve the object for the current decision.</summary>
    /// <param name="context">The resolution inputs.</param>
    /// <param name="entity">The resolved object when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an object was resolved; otherwise <see langword="false"/>.</returns>
    bool TryResolve(CustodexResolutionContext context, out EntityRef entity);
}
```

`src/Custodex.AspNetCore/Routing/CustodexObjectBindingMetadata.cs`:

```csharp
namespace Custodex.AspNetCore;

/// <summary>
/// Endpoint metadata that overrides how the route-value object resolver binds an object: which route
/// key holds the id, and the object type to use. Attach it with
/// <see cref="CustodexEndpointConventionBuilderExtensions.WithCustodexObject{TBuilder}"/>.
/// </summary>
public sealed class CustodexObjectBindingMetadata
{
    /// <summary>Creates the metadata.</summary>
    /// <param name="routeKey">The route value key holding the object id, or <see langword="null"/> to use the conventional keys.</param>
    /// <param name="objectType">The object type to bind, or <see langword="null"/> to use the policy's type.</param>
    public CustodexObjectBindingMetadata(string? routeKey = null, string? objectType = null)
    {
        RouteKey = routeKey;
        ObjectType = objectType;
    }

    /// <summary>The route value key holding the object id, or <see langword="null"/>.</summary>
    public string? RouteKey { get; }

    /// <summary>The object type to bind, or <see langword="null"/>.</summary>
    public string? ObjectType { get; }
}
```

`src/Custodex.AspNetCore/Routing/CustodexEndpointConventionBuilderExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;

namespace Custodex.AspNetCore;

/// <summary>Endpoint conventions for Custodex object binding.</summary>
public static class CustodexEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Configures how a Custodex policy on this endpoint binds its object from the route. This only
    /// configures object binding; authorization is still triggered by <c>[Authorize]</c> or
    /// <c>RequireAuthorization</c>.
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="routeKey">The route value key holding the object id, or <see langword="null"/> for the conventional keys.</param>
    /// <param name="type">The object type to bind, or <see langword="null"/> to use the policy's type.</param>
    /// <returns>The builder, for chaining.</returns>
    public static TBuilder WithCustodexObject<TBuilder>(this TBuilder builder, string? routeKey = null, string? type = null)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint => endpoint.Metadata.Add(new CustodexObjectBindingMetadata(routeKey, type)));
        return builder;
    }
}
```

`src/Custodex.AspNetCore/Resolution/ResourceEntityRefResolver.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceEntityRefResolver : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (context.Resource is EntityRef e)
        {
            entity = e;
            return true;
        }

        entity = default;
        return false;
    }
}
```

`src/Custodex.AspNetCore/Resolution/ResourceIdResolver.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceIdResolver : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (context.Resource is string { Length: > 0 } id)
        {
            entity = new EntityRef(context.ObjectType, id);
            return true;
        }

        entity = default;
        return false;
    }
}
```

`src/Custodex.AspNetCore/Resolution/RouteValueResolver.cs`:

```csharp
using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

internal sealed class RouteValueResolver : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        entity = default;

        var http = context.HttpContext;
        if (http is null)
            return false;

        var binding = http.GetEndpoint()?.Metadata.GetMetadata<CustodexObjectBindingMetadata>();
        var type = binding?.ObjectType ?? context.ObjectType;

        foreach (var key in CandidateKeys(binding?.RouteKey, context.ObjectType))
        {
            if (http.Request.RouteValues.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } id)
            {
                entity = new EntityRef(type, id);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateKeys(string? overrideKey, string type)
    {
        if (overrideKey is { Length: > 0 })
            yield return overrideKey;
        yield return type + "Id";
        yield return type;
        yield return "id";
    }
}
```

`src/Custodex.AspNetCore/Resolution/RootObjectResolver.cs`:

```csharp
using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class RootObjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (options.Value.RootObject?.Invoke(context) is { } resolved)
        {
            entity = resolved;
            return true;
        }

        entity = default;
        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~ObjectResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.AspNetCore/Resolution src/Custodex.AspNetCore/Routing tests/Custodex.AspNetCore.Tests/ObjectResolverTests.cs tests/Custodex.AspNetCore.Tests/ResolutionContextFactory.cs
git commit -m "feat(aspnetcore): object resolver chain with route binding and root fallback"
```

---

### Task 6: Request context factory + attribute source

**Files:**
- Create: `src/Custodex.AspNetCore/Resolution/ICustodexAttributeSource.cs`
- Create: `src/Custodex.AspNetCore/Resolution/IRequestContextFactory.cs`
- Create: `src/Custodex.AspNetCore/Resolution/DefaultRequestContextFactory.cs`
- Test: `tests/Custodex.AspNetCore.Tests/RequestContextFactoryTests.cs`

**Interfaces:**
- Consumes: `CustodexResolutionContext`; `SubjectRef`, `RequestContext` from `Custodex.Abstractions`; `TimeProvider`.
- Produces: `public interface ICustodexAttributeSource { void Contribute(IDictionary<string, object?> attributes, CustodexResolutionContext context); }`; `public interface IRequestContextFactory { RequestContext Create(SubjectRef subject, CustodexResolutionContext context); }`; default `internal sealed DefaultRequestContextFactory(TimeProvider, IEnumerable<ICustodexAttributeSource>)`.

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/RequestContextFactoryTests.cs`:

```csharp
using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class RequestContextFactoryTests
{
    private sealed class StaticAttributeSource(string key, object? value) : ICustodexAttributeSource
    {
        public void Contribute(IDictionary<string, object?> attributes, CustodexResolutionContext context) =>
            attributes[key] = value;
    }

    [Fact]
    public void Uses_time_provider_for_now_and_carries_subject()
    {
        var world = TestWorld.New();
        var instant = DateTimeOffset.UnixEpoch.AddDays(7);
        var time = new FakeTimeProvider(instant);
        var subject = new SubjectRef(world.UserType, world.SubjectId());
        var factory = new DefaultRequestContextFactory(time, []);

        var requestContext = factory.Create(subject, ResolutionContextFactory.Create("thing", "view", null, null, world.Tenant));

        requestContext.Now.ShouldBe(instant);
        requestContext.Subject.ShouldBe(subject);
        requestContext.Attributes.ShouldBeEmpty();
    }

    [Fact]
    public void Aggregates_attributes_from_sources_in_order()
    {
        var world = TestWorld.New();
        var key = world.ParamName();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var factory = new DefaultRequestContextFactory(time,
            [new StaticAttributeSource(key, 1), new StaticAttributeSource(key, 2)]);

        var requestContext = factory.Create(
            new SubjectRef(world.UserType, world.SubjectId()),
            ResolutionContextFactory.Create("thing", "view", null, null, world.Tenant));

        requestContext.Attributes[key].ShouldBe(2);
    }
}
```

`FakeTimeProvider` lives in `Microsoft.Extensions.TimeProvider.Testing`. Add it to central package management and the test project in this step.

In `Directory.Packages.props`, add:

```xml
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.7.0" />
```

In `tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj`, add to the package `ItemGroup`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~RequestContextFactoryTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interfaces and default factory**

`src/Custodex.AspNetCore/Resolution/ICustodexAttributeSource.cs`:

```csharp
namespace Custodex.AspNetCore;

/// <summary>
/// Contributes request-scoped attributes for condition (ABAC) evaluation. Implement and register one
/// to project claims, route values, or headers into the attribute set a decision sees.
/// </summary>
public interface ICustodexAttributeSource
{
    /// <summary>Adds this source's attributes to <paramref name="attributes"/>.</summary>
    /// <param name="attributes">The accumulating attribute set; later sources overwrite earlier keys.</param>
    /// <param name="context">The resolution inputs.</param>
    void Contribute(IDictionary<string, object?> attributes, CustodexResolutionContext context);
}
```

`src/Custodex.AspNetCore/Resolution/IRequestContextFactory.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore;

/// <summary>Builds the <see cref="RequestContext"/> (time and attributes) for a decision.</summary>
public interface IRequestContextFactory
{
    /// <summary>Creates a request context for <paramref name="subject"/>.</summary>
    /// <param name="subject">The resolved subject.</param>
    /// <param name="context">The resolution inputs.</param>
    /// <returns>The request context to evaluate with.</returns>
    RequestContext Create(SubjectRef subject, CustodexResolutionContext context);
}
```

`src/Custodex.AspNetCore/Resolution/DefaultRequestContextFactory.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class DefaultRequestContextFactory(TimeProvider timeProvider, IEnumerable<ICustodexAttributeSource> sources) : IRequestContextFactory
{
    public RequestContext Create(SubjectRef subject, CustodexResolutionContext context)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in sources)
            source.Contribute(attributes, context);

        return new RequestContext(timeProvider.GetUtcNow(), subject, attributes);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~RequestContextFactoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Custodex.AspNetCore/Resolution Directory.Packages.props tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj tests/Custodex.AspNetCore.Tests/RequestContextFactoryTests.cs
git commit -m "feat(aspnetcore): request-context factory and attribute source seam"
```

---

### Task 7: `CustodexAuthorizationHandler`

**Files:**
- Create: `src/Custodex.AspNetCore/CustodexAuthorizationHandler.cs`
- Create: `tests/Custodex.AspNetCore.Tests/FakeAuthorizer.cs`
- Test: `tests/Custodex.AspNetCore.Tests/AuthorizationHandlerTests.cs`

**Interfaces:**
- Consumes: `IAuthorizer.CheckAsync(CheckRequest, ct)`; `ICustodexSubjectResolver`, `ICustodexTenantResolver`, `IEnumerable<ICustodexObjectResolver>`, `IRequestContextFactory`, `IHttpContextAccessor`, `IOptions<CustodexAuthorizationOptions>`, `ILogger<CustodexAuthorizationHandler>`; `CustodexRequirement`.
- Produces: `internal sealed class CustodexAuthorizationHandler : AuthorizationHandler<CustodexRequirement>`. On allow, calls `context.Succeed(requirement)`; on deny it leaves the requirement unmet; on engine error it denies unless `ThrowOnEvaluationError`.

- [ ] **Step 1: Write the shared fake authorizer**

`tests/Custodex.AspNetCore.Tests/FakeAuthorizer.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.AspNetCore.Tests;

internal sealed class FakeAuthorizer : IAuthorizer
{
    private readonly CheckResult? _result;
    private readonly Exception? _exception;

    public FakeAuthorizer(CheckResult result) => _result = result;

    public FakeAuthorizer(Exception exception) => _exception = exception;

    public CheckRequest? LastRequest { get; private set; }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return _exception is not null
            ? Task.FromException<CheckResult>(_exception)
            : Task.FromResult(_result ?? new CheckResult(false));
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 2: Write the failing handler tests**

`tests/Custodex.AspNetCore.Tests/AuthorizationHandlerTests.cs`:

```csharp
using System.Security.Claims;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class AuthorizationHandlerTests
{
    private sealed record Harness(
        CustodexAuthorizationHandler Handler,
        ClaimsPrincipal User,
        CustodexRequirement Requirement,
        FakeAuthorizer Authorizer);

    private static Harness Build(
        IAuthorizer authorizer,
        TestWorld world,
        HttpContext? http = null,
        CustodexAuthorizationOptions? options = null)
    {
        var opts = Options.Create(options ?? new CustodexAuthorizationOptions { SubjectType = world.UserType });
        var fake = authorizer as FakeAuthorizer;

        var subjectResolver = new ClaimsCustodexSubjectResolver(opts);
        var tenantResolver = new ClaimsHeaderCustodexTenantResolver(opts);
        ICustodexObjectResolver[] objectResolvers =
        [
            new ResourceEntityRefResolver(),
            new ResourceIdResolver(),
            new RouteValueResolver(),
            new RootObjectResolver(opts),
        ];
        var factory = new DefaultRequestContextFactory(new FakeTimeProvider(DateTimeOffset.UnixEpoch), []);
        var accessor = new HttpContextAccessor { HttpContext = http };

        var handler = new CustodexAuthorizationHandler(
            authorizer, subjectResolver, tenantResolver, objectResolvers, factory, accessor, opts,
            NullLogger<CustodexAuthorizationHandler>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, world.SubjectId()),
            new Claim(CustodexClaimTypes.Store, world.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, world.Tenant.Tenant),
        ], "Test"));

        var objType = world.EntityType();
        var requirement = new CustodexRequirement(objType, world.Permission());
        return new Harness(handler, user, requirement, fake!);
    }

    private static AuthorizationHandlerContext ContextFor(Harness h, object? resource = null) =>
        new([h.Requirement], h.User, resource);

    [Fact]
    public async Task Allows_when_engine_allows_and_object_comes_from_resource_entity()
    {
        var world = TestWorld.New();
        var fake = new FakeAuthorizer(new CheckResult(true));
        var h = Build(fake, world);
        var entity = new EntityRef(h.Requirement.ObjectType, world.ObjectId());

        var ctx = ContextFor(h, entity);
        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeTrue();
        h.Authorizer.LastRequest!.Object.ShouldBe(entity);
        h.Authorizer.LastRequest.Permission.ShouldBe(h.Requirement.Permission);
        h.Authorizer.LastRequest.Tenant.ShouldBe(world.Tenant);
        h.Authorizer.LastRequest.Subject.Type.ShouldBe(world.UserType);
    }

    [Fact]
    public async Task Denies_when_engine_denies()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(false)), world);
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Denies_when_no_subject_claim()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = new AuthorizationHandlerContext([h.Requirement], anonymous, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
        h.Authorizer.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Denies_when_no_object_can_be_resolved()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new CheckResult(true)), world);
        var ctx = ContextFor(h, resource: null);

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
        h.Authorizer.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Engine_exception_denies_by_default()
    {
        var world = TestWorld.New();
        var h = Build(new FakeAuthorizer(new InvalidOperationException("boom")), world);
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await h.Handler.HandleAsync(ctx);

        ctx.HasSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Engine_exception_rethrows_when_configured()
    {
        var world = TestWorld.New();
        var h = Build(
            new FakeAuthorizer(new InvalidOperationException("boom")),
            world,
            options: new CustodexAuthorizationOptions { SubjectType = world.UserType, ThrowOnEvaluationError = true });
        var ctx = ContextFor(h, new EntityRef(h.Requirement.ObjectType, world.ObjectId()));

        await Should.ThrowAsync<InvalidOperationException>(() => h.Handler.HandleAsync(ctx));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~AuthorizationHandlerTests"`
Expected: FAIL — `CustodexAuthorizationHandler` does not exist.

- [ ] **Step 4: Implement the handler**

`src/Custodex.AspNetCore/CustodexAuthorizationHandler.cs`:

```csharp
using Custodex.Abstractions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexAuthorizationHandler(
    IAuthorizer authorizer,
    ICustodexSubjectResolver subjectResolver,
    ICustodexTenantResolver tenantResolver,
    IEnumerable<ICustodexObjectResolver> objectResolvers,
    IRequestContextFactory requestContextFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<CustodexAuthorizationOptions> options,
    ILogger<CustodexAuthorizationHandler> logger) : AuthorizationHandler<CustodexRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CustodexRequirement requirement)
    {
        var http = httpContextAccessor.HttpContext;

        if (!subjectResolver.TryResolve(context.User, out var subject))
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no subject", requirement.ObjectType, requirement.Permission);
            return;
        }

        if (!tenantResolver.TryResolve(context.User, http, out var tenant))
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no tenant", requirement.ObjectType, requirement.Permission);
            return;
        }

        var resolution = new CustodexResolutionContext(
            requirement.ObjectType, requirement.Permission, context.User, context.Resource, http, tenant);

        EntityRef entity = default;
        var found = false;
        foreach (var resolver in objectResolvers)
        {
            if (resolver.TryResolve(resolution, out entity))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no object resolved", requirement.ObjectType, requirement.Permission);
            return;
        }

        var requestContext = requestContextFactory.Create(subject, resolution);
        var ct = http?.RequestAborted ?? CancellationToken.None;

        try
        {
            var result = await authorizer.CheckAsync(
                new CheckRequest(tenant, entity, requirement.Permission, subject, requestContext), ct);
            if (result.Allowed)
                context.Succeed(requirement);
        }
        catch (Exception ex) when (!options.Value.ThrowOnEvaluationError)
        {
            logger.LogError(ex, "Custodex evaluation failed for {Type}:{Permission}; denying", requirement.ObjectType, requirement.Permission);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~AuthorizationHandlerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.AspNetCore/CustodexAuthorizationHandler.cs tests/Custodex.AspNetCore.Tests/FakeAuthorizer.cs tests/Custodex.AspNetCore.Tests/AuthorizationHandlerTests.cs
git commit -m "feat(aspnetcore): resource-based authorization handler"
```

---

### Task 8: DI extension + startup check

**Files:**
- Create: `src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizationServiceCollectionExtensions.cs`
- Create: `src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizationValidation.cs`
- Create: `src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizerRegistrationCheck.cs`
- Test: `tests/Custodex.AspNetCore.Tests/ServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: every seam from Tasks 2–7; `IAuthorizer`.
- Produces: `public static IServiceCollection AddCustodexAuthorization(this IServiceCollection services, Action<CustodexAuthorizationOptions>? configure = null)`. Registers the policy provider (singleton), the handler (scoped, via `TryAddEnumerable`), the four object resolvers in order, the default subject/tenant/request-context seams (`TryAdd`), `TimeProvider.System` (`TryAdd`), `AddHttpContextAccessor()`, `AddAuthorizationCore()`, and a hosted registration check. `internal static CustodexAuthorizationValidation.EnsureAuthorizerRegistered(IServiceProvider)` throws when `IAuthorizer` is absent.

- [ ] **Step 1: Write the failing tests**

`tests/Custodex.AspNetCore.Tests/ServiceCollectionExtensionsTests.cs`:

```csharp
using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_the_policy_provider_handler_and_default_seams()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthorizer>(new FakeAuthorizer(new CheckResult(true)));

        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAuthorizationPolicyProvider>().ShouldBeOfType<CustodexPolicyProvider>();
        provider.GetServices<IAuthorizationHandler>().OfType<CustodexAuthorizationHandler>().ShouldHaveSingleItem();
        provider.GetRequiredService<ICustodexSubjectResolver>().ShouldBeOfType<ClaimsCustodexSubjectResolver>();
        provider.GetRequiredService<ICustodexTenantResolver>().ShouldBeOfType<ClaimsHeaderCustodexTenantResolver>();
        provider.GetRequiredService<IRequestContextFactory>().ShouldBeOfType<DefaultRequestContextFactory>();
    }

    [Fact]
    public void Object_resolvers_register_in_priority_order()
    {
        var services = new ServiceCollection();
        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        var resolvers = provider.GetServices<ICustodexObjectResolver>().ToArray();

        resolvers.Select(r => r.GetType()).ShouldBe(
        [
            typeof(ResourceEntityRefResolver),
            typeof(ResourceIdResolver),
            typeof(RouteValueResolver),
            typeof(RootObjectResolver),
        ]);
    }

    [Fact]
    public void Applies_the_configure_callback()
    {
        var services = new ServiceCollection();

        services.AddCustodexAuthorization(o => o.SubjectType = "principal");

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustodexAuthorizationOptions>>()
            .Value.SubjectType.ShouldBe("principal");
    }

    [Fact]
    public void A_consumer_resolver_takes_priority_over_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICustodexSubjectResolver, OverrideSubjectResolver>();

        services.AddCustodexAuthorization();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICustodexSubjectResolver>().ShouldBeOfType<OverrideSubjectResolver>();
    }

    [Fact]
    public void Validation_throws_when_no_authorizer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddCustodexAuthorization();
        var provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => CustodexAuthorizationValidation.EnsureAuthorizerRegistered(provider));
    }

    [Fact]
    public void Validation_passes_when_an_authorizer_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizer>(new FakeAuthorizer(new CheckResult(true)));
        services.AddCustodexAuthorization();
        var provider = services.BuildServiceProvider();

        Should.NotThrow(() => CustodexAuthorizationValidation.EnsureAuthorizerRegistered(provider));
    }

    private sealed class OverrideSubjectResolver : ICustodexSubjectResolver
    {
        public bool TryResolve(System.Security.Claims.ClaimsPrincipal user, out SubjectRef subject)
        {
            subject = default;
            return false;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~ServiceCollectionExtensionsTests"`
Expected: FAIL — `AddCustodexAuthorization` does not exist.

- [ ] **Step 3: Implement the validation helper and hosted check**

`src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizationValidation.cs`:

```csharp
using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace Custodex.AspNetCore;

internal static class CustodexAuthorizationValidation
{
    public static void EnsureAuthorizerRegistered(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<IAuthorizer>() is null)
            throw new InvalidOperationException(
                "Custodex authorization requires an IAuthorizer in DI. Register the engine with " +
                "AddCustodex().Use<provider>() or the remote client with AddCustodexClient(...) before AddCustodexAuthorization().");
    }
}
```

`src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizerRegistrationCheck.cs`:

```csharp
using Microsoft.Extensions.Hosting;

namespace Custodex.AspNetCore;

internal sealed class CustodexAuthorizerRegistrationCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CustodexAuthorizationValidation.EnsureAuthorizerRegistered(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 4: Implement the DI extension**

`src/Custodex.AspNetCore/DependencyInjection/CustodexAuthorizationServiceCollectionExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Custodex.AspNetCore;

/// <summary>Registers the Custodex ASP.NET Core authorization adapter.</summary>
public static class CustodexAuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Wires stock ASP.NET Core authorization to the Custodex engine: a dynamic policy provider for
    /// <c>{prefix}:{type}:{permission}</c> policy names, a resource-based handler, and the default
    /// subject, tenant, object, and request-context seams (each replaceable via <c>TryAdd</c>).
    /// Requires an <c>IAuthorizer</c> already registered in DI.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">An optional callback to configure <see cref="CustodexAuthorizationOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddCustodexAuthorization(
        this IServiceCollection services, Action<CustodexAuthorizationOptions>? configure = null)
    {
        services.AddAuthorizationCore();
        services.AddHttpContextAccessor();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthorizationPolicyProvider, CustodexPolicyProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, CustodexAuthorizationHandler>());

        services.TryAddSingleton<ICustodexSubjectResolver, ClaimsCustodexSubjectResolver>();
        services.TryAddSingleton<ICustodexTenantResolver, ClaimsHeaderCustodexTenantResolver>();
        services.TryAddSingleton<IRequestContextFactory, DefaultRequestContextFactory>();

        services.TryAddEnumerable(
        [
            ServiceDescriptor.Singleton<ICustodexObjectResolver, ResourceEntityRefResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, ResourceIdResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, RouteValueResolver>(),
            ServiceDescriptor.Singleton<ICustodexObjectResolver, RootObjectResolver>(),
        ]);

        services.AddHostedService<CustodexAuthorizerRegistrationCheck>();
        return services;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~ServiceCollectionExtensionsTests"`
Expected: PASS.

Note: the priority-order test relies on `TryAddEnumerable` preserving registration order; a consumer-registered resolver added before `AddCustodexAuthorization` is enumerated first, so it is tried first.

- [ ] **Step 6: Commit**

```bash
git add src/Custodex.AspNetCore/DependencyInjection tests/Custodex.AspNetCore.Tests/ServiceCollectionExtensionsTests.cs
git commit -m "feat(aspnetcore): AddCustodexAuthorization DI extension and startup check"
```

---

### Task 9: HTTP integration tests (TestHost)

**Files:**
- Create: `tests/Custodex.AspNetCore.Tests/Integration/TestAuthHandler.cs`
- Create: `tests/Custodex.AspNetCore.Tests/Integration/CustodexAuthorizationEndpointTests.cs`

**Interfaces:**
- Consumes: `AddCustodexAuthorization`; `RequireAuthorization`; `WithCustodexObject`; `TestWorld.BuildAsync` → `IAuthorizer`; `SchemaBuilder`.

`Microsoft.AspNetCore.Mvc.Testing` (already referenced in the test project) brings `Microsoft.AspNetCore.TestHost`, providing `UseTestServer()` and `GetTestClient()`.

- [ ] **Step 1: Write the test authentication handler**

`tests/Custodex.AspNetCore.Tests/Integration/TestAuthHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;

using Custodex.AspNetCore;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore.Tests.Integration;

internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SubjectHeader = "X-Test-Subject";
    public const string StoreHeader = "X-Test-Store";
    public const string TenantHeader = "X-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subject) || subject.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject.ToString()) };
        if (Request.Headers.TryGetValue(StoreHeader, out var store))
            claims.Add(new Claim(CustodexClaimTypes.Store, store.ToString()));
        if (Request.Headers.TryGetValue(TenantHeader, out var tenant))
            claims.Add(new Claim(CustodexClaimTypes.Tenant, tenant.ToString()));

        var identity = new ClaimsIdentity(claims, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

- [ ] **Step 2: Write the failing integration tests**

`tests/Custodex.AspNetCore.Tests/Integration/CustodexAuthorizationEndpointTests.cs`:

```csharp
using System.Net;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Core;
using Custodex.TestKit;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

namespace Custodex.AspNetCore.Tests.Integration;

public class CustodexAuthorizationEndpointTests
{
    private sealed class Fixture(IHost host, TestWorld world, string objType, string permission, string grantedObjectId, string grantedSubjectId) : IDisposable
    {
        public IHost Host { get; } = host;
        public TestWorld World { get; } = world;
        public string ObjType { get; } = objType;
        public string Permission { get; } = permission;
        public string GrantedObjectId { get; } = grantedObjectId;
        public string GrantedSubjectId { get; } = grantedSubjectId;
        public void Dispose() => Host.Dispose();
    }

    private static async Task<Fixture> StartAsync()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var view = world.Permission();
        var grantedObjectId = world.ObjectId();
        var grantedSubjectId = world.SubjectId();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(view, s => s.Type(world.UserType))
                .Permission(view, p => p.Relation(view)))
            .Build();

        IAuthorizer authorizer = await world.BuildAsync(schema,
            TestWorld.Tuple(objType, grantedObjectId, view, world.User(grantedSubjectId)));

        var policy = $"custodex:{objType}:{view}";

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton(authorizer);
                    services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/things/{id}", () => Results.Ok()).RequireAuthorization(policy);
                        endpoints.MapGet("/widgets/{slug}", () => Results.Ok())
                            .RequireAuthorization(policy)
                            .WithCustodexObject(routeKey: "slug", type: objType);
                    });
                }))
            .StartAsync();

        return new Fixture(host, world, objType, view, grantedObjectId, grantedSubjectId);
    }

    private static HttpRequestMessage Request(string path, string? subject, TestWorld world)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (subject is not null)
        {
            request.Headers.Add(TestAuthHandler.SubjectHeader, subject);
            request.Headers.Add(TestAuthHandler.StoreHeader, world.Tenant.Store);
            request.Headers.Add(TestAuthHandler.TenantHeader, world.Tenant.Tenant);
        }
        return request;
    }

    [Fact]
    public async Task Granted_subject_on_granted_object_gets_200()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", f.GrantedSubjectId, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Granted_subject_on_other_object_gets_403()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.World.ObjectId()}", f.GrantedSubjectId, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Other_subject_gets_403()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", f.World.SubjectId(), f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_request_gets_401()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var response = await client.SendAsync(Request($"/things/{f.GrantedObjectId}", subject: null, f.World));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Custom_route_key_via_metadata_binds_the_object()
    {
        using var f = await StartAsync();
        var client = f.Host.GetTestClient();

        var ok = await client.SendAsync(Request($"/widgets/{f.GrantedObjectId}", f.GrantedSubjectId, f.World));
        var denied = await client.SendAsync(Request($"/widgets/{f.World.ObjectId()}", f.GrantedSubjectId, f.World));

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
```

This test references `Custodex.Core` (`SchemaBuilder`). It is reachable transitively through `Custodex.TestKit`, but add an explicit project reference for clarity. In `tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <ProjectReference Include="..\..\src\Custodex.Core\Custodex.Core.csproj" />
```

- [ ] **Step 3: Run the tests to verify they fail, then pass**

Run: `dotnet test tests/Custodex.AspNetCore.Tests --filter "FullyQualifiedName~CustodexAuthorizationEndpointTests"`
Expected: PASS (all endpoint cases green). If a build error appears for `SchemaBuilder`, confirm the `Custodex.Core` project reference was added.

- [ ] **Step 4: Run the full adapter test project**

Run: `dotnet test tests/Custodex.AspNetCore.Tests`
Expected: PASS (every test across Tasks 1–9).

- [ ] **Step 5: Commit**

```bash
git add tests/Custodex.AspNetCore.Tests/Integration tests/Custodex.AspNetCore.Tests/Custodex.AspNetCore.Tests.csproj
git commit -m "test(aspnetcore): end-to-end endpoint authorization over the in-memory engine"
```

---

### Task 10: `Custodex.Blazor` + `<CustodexAuthorizeView>` + bUnit tests

**Files:**
- Create: `src/Custodex.Blazor/Custodex.Blazor.csproj`
- Create: `src/Custodex.Blazor/README.md`
- Create: `src/Custodex.Blazor/_Imports.razor`
- Create: `src/Custodex.Blazor/CustodexAuthorizeView.razor`
- Create: `src/Custodex.Blazor/CustodexAuthorizeView.razor.cs`
- Create: `tests/Custodex.Blazor.Tests/Custodex.Blazor.Tests.csproj`
- Create: `tests/Custodex.Blazor.Tests/RecordingAuthorizer.cs`
- Create: `tests/Custodex.Blazor.Tests/CustodexAuthorizeViewTests.cs`
- Modify: `Custodex.slnx`

**Interfaces:**
- Consumes: `IOptions<CustodexAuthorizationOptions>` (`PolicyPrefix`, `PolicySeparator`); `EntityRef`; `<AuthorizeView>` from `Microsoft.AspNetCore.Components.Authorization`; `AddCustodexAuthorization`.
- Produces: `public partial class CustodexAuthorizeView` with parameters `Object` (`EntityRef?`), `ObjectType` (`string?`), `ObjectId` (`string?`), `Permission` (`string`), and render fragments `Authorized`/`NotAuthorized`/`ChildContent` (`RenderFragment<AuthenticationState>?`) and `Authorizing` (`RenderFragment?`). `Object` takes precedence over `ObjectType`/`ObjectId`.

- [ ] **Step 1: Create the Blazor package project**

`src/Custodex.Blazor/Custodex.Blazor.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<IsPackable>true</IsPackable>
		<MinVerTagPrefix>blazor-v</MinVerTagPrefix>
		<PackageReadmeFile>README.md</PackageReadmeFile>
		<GenerateDocumentationFile>true</GenerateDocumentationFile>
		<Description>Blazor components for Custodex authorization: CustodexAuthorizeView, an ergonomic per-object wrapper over AuthorizeView backed by the Custodex engine.</Description>
	</PropertyGroup>

	<ItemGroup>
		<FrameworkReference Include="Microsoft.AspNetCore.App" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Custodex.AspNetCore\Custodex.AspNetCore.csproj" />
	</ItemGroup>

	<ItemGroup>
		<None Update="README.md" Pack="true" PackagePath="\" />
	</ItemGroup>

</Project>
```

- [ ] **Step 2: Create the README and imports**

`src/Custodex.Blazor/README.md`:

```markdown
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
```

`src/Custodex.Blazor/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Authorization
```

- [ ] **Step 3: Create the component markup and code-behind**

`src/Custodex.Blazor/CustodexAuthorizeView.razor`:

```razor
@namespace Custodex.Blazor

<AuthorizeView Policy="@PolicyName"
               Resource="@ResolvedObjectBox"
               Authorized="@Authorized"
               NotAuthorized="@NotAuthorized"
               Authorizing="@Authorizing"
               ChildContent="@ChildContent" />
```

`src/Custodex.Blazor/CustodexAuthorizeView.razor.cs`:

```csharp
using Custodex.Abstractions;
using Custodex.AspNetCore;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Custodex.Blazor;

/// <summary>
/// Authorizes its content against a Custodex permission on a specific object. A per-object wrapper
/// over <see cref="AuthorizeView"/>: it computes the <c>{prefix}:{type}:{permission}</c> policy and
/// passes the object as the authorization resource, so it shares the engine evaluation path.
/// </summary>
public partial class CustodexAuthorizeView
{
    [Inject]
    private IOptions<CustodexAuthorizationOptions> Options { get; set; } = default!;

    /// <summary>The object to authorize, as an <see cref="EntityRef"/>. Takes precedence over <see cref="ObjectType"/> and <see cref="ObjectId"/>.</summary>
    [Parameter]
    public EntityRef? Object { get; set; }

    /// <summary>The object's entity type, used with <see cref="ObjectId"/> when <see cref="Object"/> is not set.</summary>
    [Parameter]
    public string? ObjectType { get; set; }

    /// <summary>The object's id, used with <see cref="ObjectType"/> when <see cref="Object"/> is not set.</summary>
    [Parameter]
    public string? ObjectId { get; set; }

    /// <summary>The permission to check on the object.</summary>
    [Parameter]
    public string Permission { get; set; } = default!;

    /// <summary>Content shown when the subject is authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? Authorized { get; set; }

    /// <summary>Content shown when the subject is not authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? NotAuthorized { get; set; }

    /// <summary>Content shown while authorization is in progress.</summary>
    [Parameter]
    public RenderFragment? Authorizing { get; set; }

    /// <summary>Default content, shown when the subject is authorized.</summary>
    [Parameter]
    public RenderFragment<AuthenticationState>? ChildContent { get; set; }

    private EntityRef ResolvedObject => Object ?? new EntityRef(
        ObjectType ?? throw new InvalidOperationException("CustodexAuthorizeView requires Object or both ObjectType and ObjectId."),
        ObjectId ?? throw new InvalidOperationException("CustodexAuthorizeView requires Object or both ObjectType and ObjectId."));

    private object ResolvedObjectBox => ResolvedObject;

    private string PolicyName
    {
        get
        {
            var o = Options.Value;
            var entity = ResolvedObject;
            return $"{o.PolicyPrefix}{o.PolicySeparator}{entity.Type}{o.PolicySeparator}{Permission}";
        }
    }
}
```

- [ ] **Step 4: Create the Blazor test project**

`tests/Custodex.Blazor.Tests/Custodex.Blazor.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="bunit" />
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Custodex.Blazor\Custodex.Blazor.csproj" />
    <ProjectReference Include="..\..\src\Custodex.AspNetCore\Custodex.AspNetCore.csproj" />
    <ProjectReference Include="..\Custodex.TestKit\Custodex.TestKit.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Create the recording authorizer**

`tests/Custodex.Blazor.Tests/RecordingAuthorizer.cs`:

```csharp
using Custodex.Abstractions;

namespace Custodex.Blazor.Tests;

internal sealed class RecordingAuthorizer(CheckResult result) : IAuthorizer
{
    public CheckRequest? Last { get; private set; }

    public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default)
    {
        Last = request;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 6: Write the failing component tests**

`tests/Custodex.Blazor.Tests/CustodexAuthorizeViewTests.cs`:

```csharp
using System.Security.Claims;

using Bunit;

using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Blazor;
using Custodex.TestKit;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Blazor.Tests;

public class CustodexAuthorizeViewTests
{
    private sealed class Scenario(
        BunitContext context, TestWorld world, RecordingAuthorizer authorizer,
        string objType, string objId, string permission, string subjectId) : IDisposable
    {
        public BunitContext Context { get; } = context;
        public TestWorld World { get; } = world;
        public RecordingAuthorizer Authorizer { get; } = authorizer;
        public string ObjType { get; } = objType;
        public string ObjId { get; } = objId;
        public string Permission { get; } = permission;
        public string SubjectId { get; } = subjectId;
        public void Dispose() => Context.Dispose();
    }

    private static Scenario Arrange(TestWorld world, CheckResult result)
    {
        var objType = world.EntityType();
        var objId = world.ObjectId();
        var permission = world.Permission();
        var subjectId = world.SubjectId();

        var ctx = new BunitContext();
        var authorizer = new RecordingAuthorizer(result);
        ctx.Services.AddSingleton<IAuthorizer>(authorizer);
        ctx.Services.AddLogging();
        ctx.Services.AddCustodexAuthorization(o => o.SubjectType = world.UserType);

        return new Scenario(ctx, world, authorizer, objType, objId, permission, subjectId);
    }

    private static Task<AuthenticationState> AuthState(Scenario s) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, s.SubjectId),
            new Claim(CustodexClaimTypes.Store, s.World.Tenant.Store),
            new Claim(CustodexClaimTypes.Tenant, s.World.Tenant.Tenant),
        ], "Test"))));

    private static IRenderedFragment RenderView(Scenario s, Task<AuthenticationState> authState) =>
        s.Context.Render(builder =>
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            builder.AddComponentParameter(1, nameof(CascadingValue<Task<AuthenticationState>>.Value), authState);
            builder.AddComponentParameter(2, nameof(CascadingValue<Task<AuthenticationState>>.IsFixed), true);
            builder.AddComponentParameter(3, nameof(CascadingValue<Task<AuthenticationState>>.ChildContent), (RenderFragment)(inner =>
            {
                inner.OpenComponent<CustodexAuthorizeView>(0);
                inner.AddComponentParameter(1, nameof(CustodexAuthorizeView.ObjectType), s.ObjType);
                inner.AddComponentParameter(2, nameof(CustodexAuthorizeView.ObjectId), s.ObjId);
                inner.AddComponentParameter(3, nameof(CustodexAuthorizeView.Permission), s.Permission);
                inner.AddComponentParameter(4, nameof(CustodexAuthorizeView.Authorized),
                    (RenderFragment<AuthenticationState>)(_ => mb => mb.AddMarkupContent(0, "<span id=\"ok\">granted</span>")));
                inner.AddComponentParameter(5, nameof(CustodexAuthorizeView.NotAuthorized),
                    (RenderFragment<AuthenticationState>)(_ => mb => mb.AddMarkupContent(0, "<span id=\"no\">denied</span>")));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        });

    [Fact]
    public void Allowed_renders_authorized_and_checks_the_resolved_object()
    {
        using var s = Arrange(TestWorld.New(), new CheckResult(true));

        var cut = RenderView(s, AuthState(s));

        cut.WaitForAssertion(() => cut.FindAll("#ok").ShouldNotBeEmpty());
        s.Authorizer.Last.ShouldNotBeNull();
        s.Authorizer.Last!.Object.ShouldBe(new EntityRef(s.ObjType, s.ObjId));
        s.Authorizer.Last.Permission.ShouldBe(s.Permission);
        s.Authorizer.Last.Subject.Id.ShouldBe(s.SubjectId);
    }

    [Fact]
    public void Denied_renders_not_authorized()
    {
        using var s = Arrange(TestWorld.New(), new CheckResult(false));

        var cut = RenderView(s, AuthState(s));

        cut.WaitForAssertion(() => cut.FindAll("#no").ShouldNotBeEmpty());
    }
}
```

Note: `TestWorld.New()` is deterministic per calling method, so the `world` built inside `Arrange` and the `world` in the test method seed identically — `world.Tenant`, `world.UserType`, and the vended ids match across both calls within one test method. Keep the `Arrange`/`AuthState` split exactly as shown so both observe the same seed.

- [ ] **Step 7: Add both Blazor projects to the solution**

Edit `Custodex.slnx`. In `/src/` add:

```xml
    <Project Path="src/Custodex.Blazor/Custodex.Blazor.csproj" />
```

In `/tests/` add:

```xml
    <Project Path="tests/Custodex.Blazor.Tests/Custodex.Blazor.Tests.csproj" />
```

- [ ] **Step 8: Run the Blazor tests**

Run: `dotnet test tests/Custodex.Blazor.Tests`
Expected: PASS (both render assertions green and the recorded check carries the resolved `EntityRef`, permission, and subject).

- [ ] **Step 9: Commit**

```bash
git add src/Custodex.Blazor tests/Custodex.Blazor.Tests Custodex.slnx
git commit -m "feat(blazor): CustodexAuthorizeView component over AuthorizeView"
```

---

### Task 11: Full-solution verification and package smoke

**Files:**
- No source changes expected; this task verifies the whole build, the packages, and the public-doc surface.

- [ ] **Step 1: Build the whole solution with warnings as errors**

Run: `dotnet build Custodex.slnx`
Expected: Build succeeded, 0 warnings (CS1591 would fail the build if any public member lacks a doc comment).

- [ ] **Step 2: Run the two new test projects**

Run: `dotnet test tests/Custodex.AspNetCore.Tests tests/Custodex.Blazor.Tests`
Expected: PASS for both.

- [ ] **Step 3: Pack both libraries**

Run: `dotnet pack src/Custodex.AspNetCore -c Release -o ./artifacts`
Run: `dotnet pack src/Custodex.Blazor -c Release -o ./artifacts`
Expected: each produces a `.nupkg` in `./artifacts`. Confirm `Custodex.AspNetCore.<version>.nupkg` and `Custodex.Blazor.<version>.nupkg` exist (the per-package `MinVerTagPrefix` controls versioning; absence of a matching tag yields a `0.0.0-alpha`-style prerelease, which is expected before the first release tag).

- [ ] **Step 4: Confirm the README is packed**

Run: `dotnet nuget locals all --list` is not needed; instead inspect the package contains the README. Run:

```bash
unzip -l ./artifacts/Custodex.AspNetCore.*.nupkg | grep README.md
unzip -l ./artifacts/Custodex.Blazor.*.nupkg | grep README.md
```

Expected: each lists `README.md` at the package root. (On Windows without `unzip`, run `tar -tf ./artifacts/Custodex.AspNetCore.*.nupkg` and confirm `README.md` is listed.)

- [ ] **Step 5: Clean the artifacts directory**

Run: `rm -rf ./artifacts`
(The artifacts are a local smoke check, not committed.)

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "chore(aspnetcore): verify full build, tests, and package smoke" --allow-empty
```

---

## Self-Review

**1. Spec coverage:**

- Two packable projects, correct SDKs, `MinVerTagPrefix`, slnx entries — Tasks 1, 10, 11.
- Policy provider (prefix match, defer, caching) — Task 2.
- Resource-based handler, deny/error semantics, 401/403 — Tasks 7, 9.
- Object resolver chain (resource-entity, resource-id, route candidate keys, root fallback) + `.WithCustodexObject` — Tasks 5, 9.
- Subject resolver (A4) — Task 3. Tenant resolver (A7) with `Custodex:store`/`X-Custodex-Tenant`/`Custodex:tenant` — Task 4. Request-context factory + attribute source (A6) — Task 6.
- `AddCustodexAuthorization` + startup check — Task 8.
- `CustodexClaimTypes`/`CustodexHeaders` constants — Task 1.
- `<CustodexAuthorizeView>` over `<AuthorizeView>` (single path), `Object` precedence — Task 10.
- Testing via `WebApplicationFactory`/TestHost + bUnit + `TestWorld` (domain-neutral) — Tasks 9, 10.

**2. Placeholder scan:** No `TBD`/`TODO`/"add error handling" placeholders; every code step shows complete code and every run step shows the command and expected outcome.

**3. Type consistency:** `CustodexRequirement(ObjectType, Permission)`, `ICustodexObjectResolver.TryResolve(CustodexResolutionContext, out EntityRef)`, `ICustodexSubjectResolver.TryResolve(ClaimsPrincipal, out SubjectRef)`, `ICustodexTenantResolver.TryResolve(ClaimsPrincipal, HttpContext?, out TenantContext)`, `IRequestContextFactory.Create(SubjectRef, CustodexResolutionContext)`, and `CustodexResolutionContext`'s six members are used identically across Tasks 1–10. The handler constructs `CheckRequest(tenant, entity, permission, subject, requestContext)` matching the `Custodex.Abstractions` record. `AddCustodexAuthorization` registers exactly the implementations the resolvers/handler/provider tests assert.

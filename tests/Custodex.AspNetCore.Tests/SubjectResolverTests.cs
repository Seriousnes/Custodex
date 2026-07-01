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
    public async Task Resolves_subject_from_name_identifier_claim()
    {
        var world = TestWorld.New();
        var id = world.SubjectId();
        var resolver = new ClaimsCustodexSubjectResolver(
            Options.Create(new CustodexAuthorizationOptions { SubjectType = world.UserType }));

        var subject = await resolver.ResolveAsync(Principal(new Claim(ClaimTypes.NameIdentifier, id)), CancellationToken.None);

        subject.ShouldBe(new SubjectRef(world.UserType, id));
    }

    [Fact]
    public async Task Missing_subject_claim_does_not_resolve()
    {
        var resolver = new ClaimsCustodexSubjectResolver(Options.Create(new CustodexAuthorizationOptions()));

        (await resolver.ResolveAsync(Principal(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Honours_a_custom_subject_id_claim()
    {
        var world = TestWorld.New();
        var id = world.SubjectId();
        var resolver = new ClaimsCustodexSubjectResolver(Options.Create(
            new CustodexAuthorizationOptions { SubjectIdClaim = "sub", SubjectType = world.UserType }));

        var subject = await resolver.ResolveAsync(Principal(new Claim("sub", id)), CancellationToken.None);

        subject.ShouldBe(new SubjectRef(world.UserType, id));
    }
}

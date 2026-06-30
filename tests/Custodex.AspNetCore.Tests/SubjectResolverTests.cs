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

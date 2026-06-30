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

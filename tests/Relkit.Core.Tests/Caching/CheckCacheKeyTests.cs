using Relkit.Abstractions;
using Relkit.Core.Caching;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Caching;

public class CheckCacheKeyTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    [Fact]
    public void Same_inputs_produce_the_same_key()
    {
        var a = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        var b = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        a.ShouldBe(b);
    }

    [Fact]
    public void Different_components_produce_different_keys()
    {
        var baseKey = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"));
        CheckCacheKey.Build(T, "v2", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(new TenantContext("zoo", "t2"), "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D2"), "view", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "edit", new SubjectRef("user", "alice")).ShouldNotBe(baseKey);
        CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")).ShouldNotBe(baseKey);
    }

    [Fact]
    public void Subject_set_relation_is_part_of_the_key()
    {
        var plain = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("group", "vets"));
        var set = CheckCacheKey.Build(T, "v1", new EntityRef("doc", "D1"), "view", new SubjectRef("group", "vets", "member"));
        plain.ShouldNotBe(set);
    }
}

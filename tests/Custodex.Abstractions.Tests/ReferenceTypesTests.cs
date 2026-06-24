// tests/Custodex.Abstractions.Tests/ReferenceTypesTests.cs
using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ReferenceTypesTests
{
    [Fact]
    public void EntityRef_detects_wildcard_and_formats()
    {
        new EntityRef("animal", "EL-001").IsWildcard.ShouldBeFalse();
        new EntityRef("user", "*").IsWildcard.ShouldBeTrue();
        new EntityRef("animal", "EL-001").ToString().ShouldBe("animal:EL-001");
    }

    [Fact]
    public void SubjectRef_detects_subject_set_and_wildcard()
    {
        new SubjectRef("group", "vets", "member").IsSubjectSet.ShouldBeTrue();
        new SubjectRef("user", "alice").IsSubjectSet.ShouldBeFalse();
        new SubjectRef("user", "*").IsWildcard.ShouldBeTrue();
    }
}

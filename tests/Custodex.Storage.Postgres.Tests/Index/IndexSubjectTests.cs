using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Index;

public class IndexSubjectTests
{
    [Fact]
    public void Plain_user_encodes_as_type_colon_id()
    {
        IndexSubject.Of(new SubjectRef("user", "alice")).ShouldBe("user:alice");
    }

    [Fact]
    public void Subject_set_includes_the_relation_suffix()
    {
        IndexSubject.Of(new SubjectRef("group", "team", "member")).ShouldBe("group:team#member");
    }

    [Fact]
    public void Wildcard_encodes_as_type_colon_star()
    {
        IndexSubject.Of(new SubjectRef("user", "*")).ShouldBe("user:*");
    }
}

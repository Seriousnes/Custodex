using Shouldly;

namespace Custodex.Abstractions.Tests;

public class RequestTypesTests
{
    [Fact]
    public void ListObjects_request_defaults_page_size_and_null_cursor()
    {
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
            new Dictionary<string, object?>());
        var req = new ListObjectsRequest(new TenantContext("zoo", "t1"),
            new SubjectRef("user", "alice"), "animal", "edit", ctx);
        req.PageSize.ShouldBe(100);
        req.ContinuationToken.ShouldBeNull();
    }
}

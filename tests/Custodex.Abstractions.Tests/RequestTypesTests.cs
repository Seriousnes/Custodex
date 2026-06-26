using Custodex.TestKit;

using Shouldly;

namespace Custodex.Abstractions.Tests;

public class RequestTypesTests
{
    [Fact]
    public void ListObjects_request_defaults_page_size_and_null_cursor()
    {
        var world = TestWorld.New();
        var subject = world.User(world.SubjectId());
        var objType = world.EntityType();
        var permission = world.Permission();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, subject,
            new Dictionary<string, object?>());
        var req = new ListObjectsRequest(world.Tenant, subject, objType, permission, ctx);
        req.PageSize.ShouldBe(100);
        req.ContinuationToken.ShouldBeNull();
    }
}

using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class CheckCacheKeyTests
{
    private readonly TestWorld _world = TestWorld.New();

    [Fact]
    public void Same_inputs_produce_the_same_key()
    {
        var t = _world.Tenant;
        var objType = _world.EntityType();
        var objId = _world.ObjectId();
        var perm = _world.Permission();
        var subjectId = _world.SubjectId();

        var a = CheckCacheKey.Build(t, TestWorld.Version, new EntityRef(objType, objId), perm, _world.User(subjectId));
        var b = CheckCacheKey.Build(t, TestWorld.Version, new EntityRef(objType, objId), perm, _world.User(subjectId));
        a.ShouldBe(b);
    }

    [Fact]
    public void Different_components_produce_different_keys()
    {
        var t = _world.Tenant;
        var objType = _world.EntityType();
        var objId = _world.ObjectId();
        var perm = _world.Permission();
        var subjectId = _world.SubjectId();
        var obj = new EntityRef(objType, objId);
        var subject = _world.User(subjectId);

        var otherVersion = "v2";
        var otherTenant = new TenantContext(t.Store, _world.SubjectId());
        var otherObjId = _world.ObjectId();
        var otherPerm = _world.Permission();
        var otherSubjectId = _world.SubjectId();

        var baseKey = CheckCacheKey.Build(t, TestWorld.Version, obj, perm, subject);
        CheckCacheKey.Build(t, otherVersion, obj, perm, subject).ShouldNotBe(baseKey);
        CheckCacheKey.Build(otherTenant, TestWorld.Version, obj, perm, subject).ShouldNotBe(baseKey);
        CheckCacheKey.Build(t, TestWorld.Version, new EntityRef(objType, otherObjId), perm, subject).ShouldNotBe(baseKey);
        CheckCacheKey.Build(t, TestWorld.Version, obj, otherPerm, subject).ShouldNotBe(baseKey);
        CheckCacheKey.Build(t, TestWorld.Version, obj, perm, _world.User(otherSubjectId)).ShouldNotBe(baseKey);
    }

    [Fact]
    public void Subject_set_relation_is_part_of_the_key()
    {
        var t = _world.Tenant;
        var objType = _world.EntityType();
        var objId = _world.ObjectId();
        var perm = _world.Permission();
        var groupId = _world.SubjectId();
        var obj = new EntityRef(objType, objId);

        var plain = CheckCacheKey.Build(t, TestWorld.Version, obj, perm, new SubjectRef(_world.GroupType, groupId));
        var set = CheckCacheKey.Build(t, TestWorld.Version, obj, perm, _world.Member(groupId));
        plain.ShouldNotBe(set);
    }
}

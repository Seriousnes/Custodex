using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class AlgebraTests
{
    private readonly TestWorld _world = TestWorld.New();

    private Task<Custodex.Core.Evaluation.EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples) =>
        _world.BuildAsync(schema, tuples);

    private CheckRequest Req(EntityRef obj, string perm, string subjectId) =>
        _world.Check(obj, perm, _world.User(subjectId));

    private RelationTuple Tuple(string objType, string objId, string rel, SubjectRef subject) =>
        _world.Tuple(objType, objId, rel, subject);

    [Fact]
    public async Task Union_grants_if_either_branch_holds()
    {
        var objType = _world.EntityType();
        var viewer = _world.Relation();
        var editor = _world.Relation();
        var access = _world.Permission();
        var objId = _world.ObjectId();
        var granted = _world.SubjectId();
        var denied = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(_world.UserType))
                .Relation(editor, s => s.Type(_world.UserType))
                .Permission(access, p => p.Relation(viewer).Union(x => x.Relation(editor))))
            .Build();
        var auth = await NewAsync(schema, Tuple(objType, objId, editor, _world.User(granted)));
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, granted))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, denied))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Intersect_requires_both_branches()
    {
        var objType = _world.EntityType();
        var left = _world.Relation();
        var right = _world.Relation();
        var access = _world.Permission();
        var objId = _world.ObjectId();
        var both = _world.SubjectId();
        var leftOnly = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(left, s => s.Type(_world.UserType))
                .Relation(right, s => s.Type(_world.UserType))
                .Permission(access, p => p.Relation(left).Intersect(x => x.Relation(right))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(objType, objId, left, _world.User(both)),
            Tuple(objType, objId, right, _world.User(both)),
            Tuple(objType, objId, left, _world.User(leftOnly)));
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, both))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, leftOnly))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_revokes_the_right_branch()
    {
        var objType = _world.EntityType();
        var viewer = _world.Relation();
        var blocked = _world.Relation();
        var access = _world.Permission();
        var objId = _world.ObjectId();
        var allowed = _world.SubjectId();
        var revoked = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(_world.UserType))
                .Relation(blocked, s => s.Type(_world.UserType))
                .Permission(access, p => p.Relation(viewer).Exclude(x => x.Relation(blocked))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(objType, objId, viewer, _world.User(allowed)),
            Tuple(objType, objId, viewer, _world.User(revoked)),
            Tuple(objType, objId, blocked, _world.User(revoked)));
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, allowed))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, revoked))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_self_is_always_deny()
    {
        // a - a == deny for everyone.
        var objType = _world.EntityType();
        var viewer = _world.Relation();
        var access = _world.Permission();
        var objId = _world.ObjectId();
        var subjectId = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(_world.UserType))
                .Permission(access, p => p.Relation(viewer).Exclude(x => x.Relation(viewer))))
            .Build();
        var auth = await NewAsync(schema, Tuple(objType, objId, viewer, _world.User(subjectId)));
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), access, subjectId))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_inherits_through_a_related_object_permission()
    {
        // child.edit = link->edit ; parent.edit = editor
        var parentType = _world.EntityType();
        var childType = _world.EntityType();
        var editor = _world.Relation();
        var link = _world.Relation();
        var edit = _world.Permission();
        var parentId = _world.ObjectId();
        var childId = _world.ObjectId();
        var granted = _world.SubjectId();
        var denied = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(parentType, t => t
                .Relation(editor, s => s.Type(_world.UserType))
                .Permission(edit, p => p.Relation(editor)))
            .Type(childType, t => t
                .Relation(link, s => s.Type(parentType))
                .Permission(edit, p => p.Arrow(link, edit)))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(childType, childId, link, new SubjectRef(parentType, parentId)),
            Tuple(parentType, parentId, editor, _world.User(granted)));
        (await auth.CheckAsync(Req(new EntityRef(childType, childId), edit, granted))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef(childType, childId), edit, denied))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_sees_inner_exclusion_on_the_related_object()
    {
        // The landmine: child.edit -> parent.edit, and parent.edit contains - blocked.
        // A top-level post-filter could not see the inner exclusion; pointwise arrow recursion does.
        var parentType = _world.EntityType();
        var childType = _world.EntityType();
        var editor = _world.Relation();
        var blocked = _world.Relation();
        var link = _world.Relation();
        var edit = _world.Permission();
        var parentId = _world.ObjectId();
        var childId = _world.ObjectId();
        var revoked = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(parentType, t => t
                .Relation(editor, s => s.Type(_world.UserType))
                .Relation(blocked, s => s.Type(_world.UserType))
                .Permission(edit, p => p.Relation(editor).Exclude(x => x.Relation(blocked))))
            .Type(childType, t => t
                .Relation(link, s => s.Type(parentType))
                .Permission(edit, p => p.Arrow(link, edit)))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(childType, childId, link, new SubjectRef(parentType, parentId)),
            Tuple(parentType, parentId, editor, _world.User(revoked)),
            Tuple(parentType, parentId, blocked, _world.User(revoked)));
        (await auth.CheckAsync(Req(new EntityRef(childType, childId), edit, revoked))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_resolves_a_target_that_is_both_relation_and_permission()
    {
        // link->gate where gate is a relation that is ALSO surfaced as a permission.
        var parentType = _world.EntityType();
        var childType = _world.EntityType();
        var gate = _world.Relation();
        var link = _world.Relation();
        var guarded = _world.Permission();
        var parentId = _world.ObjectId();
        var childId = _world.ObjectId();
        var anyone = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(parentType, t => t
                .Relation(gate, s => s.Wildcard(_world.UserType))
                .Permission(gate, p => p.Relation(gate)))
            .Type(childType, t => t
                .Relation(link, s => s.Type(parentType))
                .Permission(guarded, p => p.Arrow(link, gate)))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(childType, childId, link, new SubjectRef(parentType, parentId)),
            Tuple(parentType, parentId, gate, new SubjectRef(_world.UserType, "*")));
        (await auth.CheckAsync(Req(new EntityRef(childType, childId), guarded, anyone))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Arrow_relation_fallback_resolves_when_target_has_no_such_permission()
    {
        // parent has a relation 'gate' but NO permission named 'gate'.
        // child.guarded = link->gate must fall back to resolving the 'gate' relation on the parent.
        var parentType = _world.EntityType();
        var childType = _world.EntityType();
        var gate = _world.Relation();
        var link = _world.Relation();
        var guarded = _world.Permission();
        var parentId = _world.ObjectId();
        var childId = _world.ObjectId();
        var anyone = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(parentType, t => t
                .Relation(gate, s => s.Wildcard(_world.UserType)))
            .Type(childType, t => t
                .Relation(link, s => s.Type(parentType))
                .Permission(guarded, p => p.Arrow(link, gate)))
            .Build();
        var auth = await NewAsync(schema,
            Tuple(childType, childId, link, new SubjectRef(parentType, parentId)),
            Tuple(parentType, parentId, gate, new SubjectRef(_world.UserType, "*")));
        (await auth.CheckAsync(Req(new EntityRef(childType, childId), guarded, anyone))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Conditioned_branch_passes_through_when_condition_is_satisfied()
    {
        var objType = _world.EntityType();
        var viewer = _world.Relation();
        var view = _world.Permission();
        var condition = _world.ConditionName();
        var objId = _world.ObjectId();
        var granted = _world.SubjectId();
        var denied = _world.SubjectId();

        var schema = new SchemaBuilder(_world.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(_world.UserType))
                .Permission(view, p => p.Relation(viewer).Conditioned(condition)))
            .Condition(condition, c => { })
            .Build();
        var auth = await NewAsync(schema, Tuple(objType, objId, viewer, _world.User(granted)));
        // NullConditionEvaluator treats the condition as satisfied, so the viewer is allowed; the other is not.
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), view, granted))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef(objType, objId), view, denied))).Allowed.ShouldBeFalse();
    }
}

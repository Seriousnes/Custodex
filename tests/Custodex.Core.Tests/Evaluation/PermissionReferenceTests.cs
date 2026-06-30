using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class PermissionReferenceTests
{
    private readonly TestWorld _world = TestWorld.New();

    [Fact]
    public async Task Permission_referencing_another_permission_on_same_type_grants_through_it()
    {
        var doc = _world.EntityType();
        var editor = _world.Relation();
        var edit = _world.Permission();
        var view = _world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(doc, t => t
                .Relation(editor, s => s.Type(_world.UserType))
                .Permission(edit, p => p.Relation(editor))
                .Permission(view, p => p.Relation(edit)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();

        var user = _world.SubjectId();
        var obj = _world.ObjectId();
        var auth = await _world.BuildAsync(schema, TestWorld.Tuple(doc, obj, editor, _world.User(user)));

        (await auth.CheckAsync(_world.Check(doc, obj, view, user))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Permission_referencing_another_permission_denies_when_that_permission_denies()
    {
        var doc = _world.EntityType();
        var editor = _world.Relation();
        var edit = _world.Permission();
        var view = _world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(doc, t => t
                .Relation(editor, s => s.Type(_world.UserType))
                .Permission(edit, p => p.Relation(editor))
                .Permission(view, p => p.Relation(edit)))
            .Build();

        var stranger = _world.SubjectId();
        var obj = _world.ObjectId();
        var auth = await _world.BuildAsync(schema);

        (await auth.CheckAsync(_world.Check(doc, obj, view, stranger))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Bare_name_that_is_both_a_relation_and_a_permission_resolves_the_relation_first()
    {
        var doc = _world.EntityType();
        var dup = _world.Relation();
        var absent = _world.Relation();
        var gate = _world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(doc, t => t
                .Relation(dup, s => s.Type(_world.UserType))
                .Relation(absent, s => s.Type(_world.UserType))
                .Permission(dup, p => p.Relation(absent))
                .Permission(gate, p => p.Relation(dup)))
            .Build();

        SchemaValidator.Validate(schema).IsValid.ShouldBeTrue();

        var user = _world.SubjectId();
        var obj = _world.ObjectId();
        var auth = await _world.BuildAsync(schema, TestWorld.Tuple(doc, obj, dup, _world.User(user)));

        (await auth.CheckAsync(_world.Check(doc, obj, gate, user))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task List_subjects_includes_a_subject_reachable_only_through_a_same_type_permission_reference()
    {
        var doc = _world.EntityType();
        var editor = _world.Relation();
        var edit = _world.Permission();
        var view = _world.Permission();

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(doc, t => t
                .Relation(editor, s => s.Type(_world.UserType))
                .Permission(edit, p => p.Relation(editor))
                .Permission(view, p => p.Relation(edit)))
            .Build();

        var user = _world.SubjectId();
        var obj = _world.ObjectId();
        var auth = await _world.BuildAsync(schema, TestWorld.Tuple(doc, obj, editor, _world.User(user)));

        var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
            _world.Tenant, new EntityRef(doc, obj), view,
            new RequestContext(DateTimeOffset.UnixEpoch, _world.User(user), new Dictionary<string, object?>())));

        result.Subjects.ShouldBe([_world.User(user)]);
    }
}

using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.Core.Validation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class RecursiveArrowTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _folder;
    private readonly string _owner;
    private readonly string _parent;
    private readonly string _view;

    public RecursiveArrowTests()
    {
        _folder = _world.EntityType();
        _owner = _world.Relation();
        _parent = _world.Relation();
        _view = _world.Permission();
    }

    private Schema Build() => new SchemaBuilder(TestWorld.Version)
        .Type(_folder, t => t
            .Relation(_owner, s => s.Type(_world.UserType))
            .Relation(_parent, s => s.Type(_folder))
            .Permission(_view, p => p.Relation(_owner).Union(x => x.Arrow(_parent, _view))))
        .Build();

    private RelationTuple Parent(string child, string ancestor) =>
        TestWorld.Tuple(_folder, child, _parent, new SubjectRef(_folder, ancestor));

    private RelationTuple Owner(string folderId, string userId) =>
        TestWorld.Tuple(_folder, folderId, _owner, _world.User(userId));

    [Fact]
    public void Validator_accepts_the_self_referential_recursive_schema()
    {
        SchemaValidator.Validate(Build()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_resolves_nested_ancestry_up_a_self_referential_parent_chain()
    {
        var a = _world.ObjectId();
        var b = _world.ObjectId();
        var c = _world.ObjectId();
        var owner = _world.SubjectId();
        var stranger = _world.SubjectId();

        var auth = await _world.BuildAsync(Build(), Parent(c, b), Parent(b, a), Owner(a, owner));

        (await auth.CheckAsync(_world.Check(_folder, a, _view, owner))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_folder, b, _view, owner))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_folder, c, _view, owner))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_folder, c, _view, stranger))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task List_objects_returns_every_ancestor_folder_the_owner_can_view()
    {
        var a = _world.ObjectId();
        var b = _world.ObjectId();
        var c = _world.ObjectId();
        var owner = _world.SubjectId();

        var auth = await _world.BuildAsync(Build(), Parent(c, b), Parent(b, a), Owner(a, owner));

        var result = await auth.ListObjectsAsync(new ListObjectsRequest(
            _world.Tenant, _world.User(owner), _folder, _view,
            new RequestContext(DateTimeOffset.UnixEpoch, _world.User(owner), new Dictionary<string, object?>())));

        result.ObjectIds.ShouldBe([.. new[] { a, b, c }.OrderBy(x => x, StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task List_subjects_returns_the_transitive_owner_of_an_ancestor()
    {
        var a = _world.ObjectId();
        var b = _world.ObjectId();
        var c = _world.ObjectId();
        var owner = _world.SubjectId();

        var auth = await _world.BuildAsync(Build(), Parent(c, b), Parent(b, a), Owner(a, owner));

        var result = await auth.ListSubjectsAsync(new ListSubjectsRequest(
            _world.Tenant, new EntityRef(_folder, c), _view,
            new RequestContext(DateTimeOffset.UnixEpoch, _world.User(owner), new Dictionary<string, object?>())));

        result.Subjects.ShouldBe([_world.User(owner)]);
    }

    [Fact]
    public async Task Mutually_referential_parents_terminate_and_resolve_correctly()
    {
        var a = _world.ObjectId();
        var b = _world.ObjectId();
        var owner = _world.SubjectId();
        var stranger = _world.SubjectId();

        var auth = await _world.BuildAsync(Build(), Parent(a, b), Parent(b, a), Owner(a, owner));

        (await auth.CheckAsync(_world.Check(_folder, a, _view, owner))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_folder, b, _view, owner))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(_world.Check(_folder, b, _view, stranger))).Allowed.ShouldBeFalse();
    }
}

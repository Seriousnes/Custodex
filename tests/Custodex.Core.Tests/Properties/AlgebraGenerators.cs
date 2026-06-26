using CsCheck;

using Custodex.Abstractions;
using Custodex.TestKit;

namespace Custodex.Core.Tests.Properties;

public sealed class AlgebraWorld
{
    public TestWorld World { get; }

    public string ObjectType { get; }
    public string ObjectId { get; }
    public string Viewer { get; }
    public string View { get; }

    public Gen<string> UserId { get; }

    public static readonly Gen<int> ChainLength = Gen.Int[1, 6];

    public AlgebraWorld(TestWorld world)
    {
        World = world;
        ObjectType = world.EntityType();
        ObjectId = world.ObjectId();
        Viewer = world.Relation();
        View = world.Permission();

        var pool = new[]
        {
            world.SubjectId(), world.SubjectId(), world.SubjectId(), world.SubjectId(), world.SubjectId(),
        };
        UserId = Gen.OneOf(pool.Select(Gen.Const).ToArray());
    }

    public Schema MonotoneSchema() => new SchemaBuilder(TestWorld.Version)
        .Type(World.GroupType, t => t.Relation(World.MemberRelation,
            s => s.Type(World.UserType).SubjectSet(World.GroupType, World.MemberRelation)))
        .Type(ObjectType, t => t
            .Relation(Viewer, s => s.Type(World.UserType)
                .SubjectSet(World.GroupType, World.MemberRelation).Wildcard(World.UserType))
            .Permission(View, p => p.Relation(Viewer)))
        .Build();

    public Schema SelfExcludeSchema() => new SchemaBuilder(TestWorld.Version)
        .Type(ObjectType, t => t
            .Relation(Viewer, s => s.Type(World.UserType))
            .Permission(View, p => p.Relation(Viewer).Exclude(x => x.Relation(Viewer))))
        .Build();

    public IReadOnlyList<RelationTuple> NestedChainTuples(int length, string user)
    {
        var tuples = new List<RelationTuple>
        {
            TestWorld.Tuple(ObjectType, ObjectId, Viewer, World.Member("g0")),
        };
        for (var i = 0; i < length - 1; i++)
            tuples.Add(TestWorld.Tuple(World.GroupType, $"g{i}", World.MemberRelation, World.Member($"g{i + 1}")));
        tuples.Add(TestWorld.Tuple(World.GroupType, $"g{length - 1}", World.MemberRelation, World.User(user)));
        return tuples;
    }
}

using CsCheck;
using Custodex.Abstractions;
using Custodex.TestKit;

namespace Custodex.Core.Tests.Properties;

/// <summary>
/// Holds one <see cref="TestWorld"/> and the de-domained vocabulary the algebra property laws are
/// expressed over. Every schema, tuple and query produced here reads the same bound tokens, so the
/// schema declaration and the tuples it is checked against agree by construction.
/// </summary>
public sealed class AlgebraWorld
{
    public TestWorld World { get; }

    public string ObjectType { get; }
    public string ObjectId { get; }
    public string Viewer { get; }
    public string View { get; }

    // A small fixed pool of subject ids the generators draw from.
    public Gen<string> UserId { get; }

    // A chain of nested groups g0 < g1 < ... < gN, with a subject at the bottom and a grant at the top.
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

    // A schema with an exclusion-free, condition-free permission for the monotonicity law.
    public Schema MonotoneSchema() => new SchemaBuilder(World.Version)
        .Type(World.GroupType, t => t.Relation(World.MemberRelation,
            s => s.Type(World.UserType).SubjectSet(World.GroupType, World.MemberRelation)))
        .Type(ObjectType, t => t
            .Relation(Viewer, s => s.Type(World.UserType)
                .SubjectSet(World.GroupType, World.MemberRelation).Wildcard(World.UserType))
            .Permission(View, p => p.Relation(Viewer)))
        .Build();

    // A schema whose permission is a - a (self-exclusion).
    public Schema SelfExcludeSchema() => new SchemaBuilder(World.Version)
        .Type(ObjectType, t => t
            .Relation(Viewer, s => s.Type(World.UserType))
            .Permission(View, p => p.Relation(Viewer).Exclude(x => x.Relation(Viewer))))
        .Build();

    public IReadOnlyList<RelationTuple> NestedChainTuples(int length, string user)
    {
        var tuples = new List<RelationTuple>
        {
            // <obj>#<viewer>@<group>:g0#<member>
            World.Tuple(ObjectType, ObjectId, Viewer, World.Member("g0")),
        };
        for (var i = 0; i < length - 1; i++)
            tuples.Add(World.Tuple(World.GroupType, $"g{i}", World.MemberRelation, World.Member($"g{i + 1}")));
        // bottom group gets the subject
        tuples.Add(World.Tuple(World.GroupType, $"g{length - 1}", World.MemberRelation, World.User(user)));
        return tuples;
    }
}

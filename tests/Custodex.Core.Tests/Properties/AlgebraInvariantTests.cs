using CsCheck;
using Custodex.Abstractions;
using Custodex.TestKit;

namespace Custodex.Core.Tests.Properties;

public class AlgebraInvariantTests
{
    private static CheckRequest View(AlgebraWorld w, string user) =>
        w.World.Check(w.World.Object(w.ObjectType, w.ObjectId), w.View, w.World.User(user));

    [Fact]
    public async Task SelfExclusion_always_denies()
    {
        var w = new AlgebraWorld(TestWorld.New());
        await Check.SampleAsync(w.UserId, async user =>
        {
            var auth = await w.World.BuildAsync(w.SelfExcludeSchema(),
                w.World.Tuple(w.ObjectType, w.ObjectId, w.Viewer, w.World.User(user)));
            var r = await auth.CheckAsync(View(w, user));
            return r.Allowed == false;   // a - a = deny, even when viewer holds
        });
    }

    [Fact]
    public async Task Wildcard_grants_every_user()
    {
        var w = new AlgebraWorld(TestWorld.New());
        await Check.SampleAsync(w.UserId, async user =>
        {
            var auth = await w.World.BuildAsync(w.MonotoneSchema(),
                w.World.Tuple(w.ObjectType, w.ObjectId, w.Viewer, w.World.Subject(w.World.UserType, "*")));
            var r = await auth.CheckAsync(View(w, user));
            return r.Allowed == true;
        });
    }

    [Fact]
    public async Task Nested_group_chain_is_reachable()
    {
        var w = new AlgebraWorld(TestWorld.New());
        await Check.SampleAsync(Gen.Select(AlgebraWorld.ChainLength, w.UserId),
            async (length, user) =>
            {
                var tuples = w.NestedChainTuples(length, user);
                var auth = await w.World.BuildAsync(w.MonotoneSchema(), tuples.ToArray());
                var r = await auth.CheckAsync(View(w, user));
                return r.Allowed == true;   // subject at the bottom of the chain reaches the top grant
            });
    }

    [Fact]
    public async Task Union_is_monotone_over_exclusion_free_permission()
    {
        // Granting viewer to a subject can only flip deny->allow on an exclusion-free permission.
        var w = new AlgebraWorld(TestWorld.New());
        await Check.SampleAsync(w.UserId, async user =>
        {
            var without = await w.World.BuildAsync(w.MonotoneSchema());
            var before = (await without.CheckAsync(View(w, user))).Allowed;

            var with = await w.World.BuildAsync(w.MonotoneSchema(),
                w.World.Tuple(w.ObjectType, w.ObjectId, w.Viewer, w.World.User(user)));
            var after = (await with.CheckAsync(View(w, user))).Allowed;

            // monotone: before implies after, and after must be true once granted.
            return (!before || after) && after;
        });
    }
}

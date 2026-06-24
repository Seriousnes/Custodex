using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.TestKit;

public class TestWorldTests
{
    [Fact]
    public void Output_is_stable_across_runs_for_a_fixed_seed_name()
    {
        var w = TestWorld.New("custodex-golden-seed");
        string[] actual =
        [
            w.UserType, w.GroupType, w.MemberRelation, w.Version,
            w.EntityType(), w.EntityType(), w.Relation(), w.Permission(),
            w.SubjectId(), w.ObjectId(), w.ConditionName(), w.ParamName(),
            w.Tenant.Store, w.Tenant.Tenant,
        ];

        actual.ShouldBe(
        [
            "panel", "transmitter", "copy", "v1",
            "card", "interface", "index", "input",
            "transmitter-9kft", "bandwidth-7yfu", "haptic", "quod",
            "firewall", "program",
        ]);
    }

    [Fact]
    public void Same_seed_name_reproduces_identical_output_in_process()
    {
        static string[] Draw()
        {
            var w = TestWorld.New("repro");
            return [w.UserType, w.EntityType(), w.Relation(), w.SubjectId(), w.Tenant.Store];
        }

        Draw().ShouldBe(Draw());
    }

    [Fact]
    public void Different_seed_names_produce_different_output()
    {
        var a = TestWorld.New("seed-a").EntityType();
        var b = TestWorld.New("seed-b").EntityType();
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Vended_identifiers_are_unique_across_categories()
    {
        var w = TestWorld.New();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 400; i++)
        {
            seen.Add(w.EntityType());
            seen.Add(w.Relation());
            seen.Add(w.Permission());
            seen.Add(w.SubjectId());
            seen.Add(w.ObjectId());
        }

        seen.Count.ShouldBe(400 * 5);
    }
}

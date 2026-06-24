using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class EvalContextTests
{
    private static EvalFrame Frame(string perm = "view") =>
        new(new EntityRef("animal", "EL-001"), perm, new SubjectRef("user", "alice"));

    [Fact]
    public void Memo_stores_and_returns_completed_results()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.TryGetMemo(Frame(), out _).ShouldBeFalse();
        ctx.SetMemo(Frame(), true);
        ctx.TryGetMemo(Frame(), out var hit).ShouldBeTrue();
        hit.ShouldBeTrue();
    }

    [Fact]
    public void Entering_same_frame_twice_on_path_is_detected_as_cycle()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.TryEnter(Frame(), out var scope).ShouldBeTrue();
        ctx.TryEnter(Frame(), out _).ShouldBeFalse();   // cycle: already on path
        scope.Dispose();
        ctx.TryEnter(Frame(), out _).ShouldBeTrue();     // left path -> enterable again
    }

    [Fact]
    public void Exceeding_depth_bound_throws_evaluation_limit()
    {
        var ctx = new EvalContext(new EvaluationOptions(MaxDepth: 2));
        ctx.TryEnter(Frame("a"), out _).ShouldBeTrue();
        ctx.TryEnter(Frame("b"), out _).ShouldBeTrue();
        Should.Throw<EvaluationLimitException>(() => ctx.TryEnter(Frame("c"), out _));
    }

    [Fact]
    public void Condition_touched_flag_latches()
    {
        var ctx = new EvalContext(new EvaluationOptions());
        ctx.ConditionTouched.ShouldBeFalse();
        ctx.MarkConditionTouched();
        ctx.ConditionTouched.ShouldBeTrue();
    }
}

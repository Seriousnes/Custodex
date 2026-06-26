using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class EvalContextTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _objId;
    private readonly string _perm;
    private readonly string _subjectId;

    public EvalContextTests()
    {
        _objType = _world.EntityType();
        _objId = _world.ObjectId();
        _perm = _world.Permission();
        _subjectId = _world.SubjectId();
    }

    private EvalFrame Frame() => Frame(_perm);

    private EvalFrame Frame(string perm) =>
        new(new EntityRef(_objType, _objId), perm, _world.User(_subjectId));

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
        ctx.TryEnter(Frame(), out _).ShouldBeFalse();
        scope.Dispose();
        ctx.TryEnter(Frame(), out _).ShouldBeTrue();
    }

    [Fact]
    public void Exceeding_depth_bound_throws_evaluation_limit()
    {
        var ctx = new EvalContext(new EvaluationOptions(MaxDepth: 2));
        var (a, b, c) = (_world.Permission(), _world.Permission(), _world.Permission());
        ctx.TryEnter(Frame(a), out _).ShouldBeTrue();
        ctx.TryEnter(Frame(b), out _).ShouldBeTrue();
        Should.Throw<EvaluationLimitException>(() => ctx.TryEnter(Frame(c), out _));
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

using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed record EvaluationOptions(int MaxDepth = 64);

public readonly record struct EvalFrame(EntityRef Object, string Permission, SubjectRef Subject);

/// <summary>
/// Per-request evaluation state: a memo of completed sub-checks, a visited set
/// guarding the current DFS path against cycles, a depth budget, and a latch
/// recording whether any condition was reached (so the caching layer in m0/08
/// never caches a condition-dependent decision).
/// </summary>
public sealed class EvalContext
{
    private readonly EvaluationOptions _options;
    private readonly Dictionary<EvalFrame, bool> _memo = new();
    private readonly HashSet<EvalFrame> _onPath = new();
    private readonly HashSet<EvalFrame> _relationOnPath = new();
    private int _depth;

    public EvalContext(EvaluationOptions options) => _options = options;

    public bool ConditionTouched { get; private set; }
    public void MarkConditionTouched() => ConditionTouched = true;

    public bool TryGetMemo(EvalFrame frame, out bool result) => _memo.TryGetValue(frame, out result);
    public void SetMemo(EvalFrame frame, bool result) => _memo[frame] = result;

    /// <summary>
    /// Enters <paramref name="frame"/> on the current path. Returns false (without
    /// entering) if the frame is already on the path — a cycle, which the caller
    /// treats as a non-contributing <c>false</c>. Throws when the depth bound is
    /// exceeded. Dispose the returned scope to leave the frame.
    /// </summary>
    public bool TryEnter(EvalFrame frame, out PathScope scope)
    {
        if (_onPath.Contains(frame))
        {
            scope = PathScope.NoOp;
            return false;
        }
        if (_depth >= _options.MaxDepth)
            throw new EvaluationLimitException(
                $"Evaluation depth bound of {_options.MaxDepth} exceeded at {frame.Object}#{frame.Permission}@{frame.Subject}.");

        _onPath.Add(frame);
        _depth++;
        scope = new PathScope(this, frame);
        return true;
    }

    /// <summary>
    /// Enters <paramref name="frame"/> on the current relation-resolution path. Returns false
    /// (without entering) if the frame is already on the relation path — a relation cycle,
    /// which the caller treats as a non-contributing <c>false</c>. Throws when the depth bound
    /// is exceeded. Shares <c>_depth</c> with <see cref="TryEnter"/>. Dispose the returned scope to leave.
    /// </summary>
    public bool TryEnterRelation(EvalFrame frame, out PathScope scope)
    {
        if (_relationOnPath.Contains(frame))
        {
            scope = PathScope.NoOp;
            return false;
        }
        if (_depth >= _options.MaxDepth)
            throw new EvaluationLimitException(
                $"Evaluation depth bound of {_options.MaxDepth} exceeded resolving relation {frame.Object}#{frame.Permission}@{frame.Subject}.");

        _relationOnPath.Add(frame);
        _depth++;
        scope = new PathScope(this, frame, isRelation: true);
        return true;
    }

    private void Leave(EvalFrame frame, bool isRelation)
    {
        if (isRelation) _relationOnPath.Remove(frame);
        else _onPath.Remove(frame);
        _depth--;
    }

    public readonly struct PathScope : IDisposable
    {
        private readonly EvalContext? _ctx;
        private readonly EvalFrame _frame;
        private readonly bool _isRelation;
        internal PathScope(EvalContext ctx, EvalFrame frame, bool isRelation = false)
        { _ctx = ctx; _frame = frame; _isRelation = isRelation; }
        public static PathScope NoOp => default;
        public void Dispose() => _ctx?.Leave(_frame, _isRelation);
    }
}

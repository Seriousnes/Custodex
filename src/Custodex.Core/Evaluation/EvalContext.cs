using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

/// <summary>Limits applied while evaluating a permission expression.</summary>
/// <param name="MaxDepth">The maximum recursion depth before evaluation is aborted with an exception.</param>
public sealed record EvaluationOptions(int MaxDepth = 64);

/// <summary>Identifies a single sub-check or relation resolution, used as a memo and cycle-guard key.</summary>
/// <param name="Object">The object being evaluated.</param>
/// <param name="Permission">The permission or relation being resolved on the object.</param>
/// <param name="Subject">The subject whose membership is being decided.</param>
public readonly record struct EvalFrame(EntityRef Object, string Permission, SubjectRef Subject);

/// <summary>
/// Per-request evaluation state: a memo of completed sub-checks, a visited set
/// guarding the current depth-first path against cycles, a depth budget, and a latch
/// recording whether any condition was reached (so a condition-dependent decision
/// is never cached).
/// </summary>
/// <remarks>Creates evaluation state bound to the given limits.</remarks>
/// <param name="options">The evaluation limits to enforce for this request.</param>
public sealed class EvalContext(EvaluationOptions options)
{
    private readonly EvaluationOptions _options = options;
    private readonly Dictionary<EvalFrame, EvalOutcome> _memo = [];
    private readonly Dictionary<EvalFrame, int> _onPath = [];
    private readonly HashSet<EvalFrame> _relationOnPath = [];
    private int _depth;
    private int _negations;

    /// <summary>Whether any condition was reached during this evaluation.</summary>
    public bool ConditionTouched { get; private set; }

    internal bool StructuralMarking { get; set; }

    /// <summary>Latches <see cref="ConditionTouched"/> to indicate a condition was evaluated.</summary>
    public void MarkConditionTouched() => ConditionTouched = true;

    /// <summary>Looks up a previously memoized outcome for a sub-check.</summary>
    /// <param name="frame">The sub-check to look up.</param>
    /// <param name="result">The memoized three-valued outcome when present.</param>
    /// <returns><c>true</c> if an outcome was memoized; otherwise <c>false</c>.</returns>
    public bool TryGetMemo(EvalFrame frame, out EvalOutcome result) => _memo.TryGetValue(frame, out result);

    /// <summary>Records the three-valued outcome of a completed sub-check for reuse, so a memo hit reproduces the same decision and unmet conditions.</summary>
    /// <param name="frame">The sub-check that completed.</param>
    /// <param name="result">Its outcome.</param>
    public void SetMemo(EvalFrame frame, EvalOutcome result) => _memo[frame] = result;

    /// <summary>
    /// Enters <paramref name="frame"/> on the current path. Returns false (without
    /// entering) if the frame is already on the path — a cycle, which the caller
    /// treats as a non-contributing <c>false</c>. A cycle that crossed into the excluded
    /// branch of an exclusion has no sound decision, so it throws instead of failing open.
    /// Dispose the returned scope to leave the frame.
    /// </summary>
    /// <param name="frame">The sub-check to push onto the current path.</param>
    /// <param name="scope">A disposable that leaves the frame when disposed.</param>
    /// <returns><c>true</c> when the frame was entered; <c>false</c> on a cycle.</returns>
    /// <exception cref="EvaluationLimitException">The configured depth bound was exceeded.</exception>
    /// <exception cref="ExclusionCycleException">The cycle passes through an exclusion.</exception>
    public bool TryEnter(EvalFrame frame, out PathScope scope)
    {
        if (_onPath.TryGetValue(frame, out var negationsAtEntry))
        {
            if (_negations > negationsAtEntry)
                throw new ExclusionCycleException(
                    $"Evaluation of {frame.Object}#{frame.Permission} for {frame.Subject} re-entered itself " +
                    "through an exclusion (-); the permission has no sound decision.");
            scope = PathScope.NoOp;
            return false;
        }
        if (_depth >= _options.MaxDepth)
            throw new EvaluationLimitException(
                $"Evaluation depth bound of {_options.MaxDepth} exceeded at {frame.Object}#{frame.Permission}@{frame.Subject}.");

        _onPath.Add(frame, _negations);
        _depth++;
        scope = new PathScope(this, frame);
        return true;
    }

    /// <summary>
    /// Marks evaluation as inside the excluded (right) branch of an exclusion, so a cycle
    /// that re-enters an enclosing sub-check is detected as unsound rather than failing open.
    /// Dispose the returned scope when that branch's evaluation completes.
    /// </summary>
    /// <returns>A disposable that leaves the excluded branch when disposed.</returns>
    public NegationScope EnterNegation()
    {
        _negations++;
        return new NegationScope(this);
    }

    private void LeaveNegation() => _negations--;

    /// <summary>
    /// Enters <paramref name="frame"/> on the current relation-resolution path. Returns false
    /// (without entering) if the frame is already on the relation path — a relation cycle,
    /// which the caller treats as a non-contributing <c>false</c>. Shares the depth budget with
    /// <see cref="TryEnter"/>. Dispose the returned scope to leave.
    /// </summary>
    /// <param name="frame">The relation resolution to push onto the current path.</param>
    /// <param name="scope">A disposable that leaves the frame when disposed.</param>
    /// <returns><c>true</c> when the frame was entered; <c>false</c> on a relation cycle.</returns>
    /// <exception cref="EvaluationLimitException">The configured depth bound was exceeded.</exception>
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

    /// <summary>A disposable that pops a frame off the current evaluation path when disposed.</summary>
    public readonly struct PathScope : IDisposable
    {
        private readonly EvalContext? _ctx;
        private readonly EvalFrame _frame;
        private readonly bool _isRelation;
        internal PathScope(EvalContext ctx, EvalFrame frame, bool isRelation = false)
        { _ctx = ctx; _frame = frame; _isRelation = isRelation; }

        /// <summary>A scope that holds no frame and does nothing on dispose.</summary>
        public static PathScope NoOp => default;

        /// <summary>Leaves the entered frame, removing it from the current path.</summary>
        public void Dispose() => _ctx?.Leave(_frame, _isRelation);
    }

    /// <summary>A disposable that leaves the excluded branch of an exclusion when disposed.</summary>
    public readonly struct NegationScope : IDisposable
    {
        private readonly EvalContext? _ctx;
        internal NegationScope(EvalContext ctx) => _ctx = ctx;

        /// <summary>Leaves the excluded branch.</summary>
        public void Dispose() => _ctx?.LeaveNegation();
    }
}

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
    private readonly Dictionary<EvalFrame, bool> _memo = [];
    private readonly HashSet<EvalFrame> _onPath = [];
    private readonly HashSet<EvalFrame> _relationOnPath = [];
    private int _depth;

    /// <summary>Whether any condition was reached during this evaluation.</summary>
    public bool ConditionTouched { get; private set; }

    internal bool StructuralMarking { get; set; }

    /// <summary>Latches <see cref="ConditionTouched"/> to indicate a condition was evaluated.</summary>
    public void MarkConditionTouched() => ConditionTouched = true;

    /// <summary>Looks up a previously memoized result for a sub-check.</summary>
    /// <param name="frame">The sub-check to look up.</param>
    /// <param name="result">The cached decision when present.</param>
    /// <returns><c>true</c> if a result was memoized; otherwise <c>false</c>.</returns>
    public bool TryGetMemo(EvalFrame frame, out bool result) => _memo.TryGetValue(frame, out result);

    /// <summary>Records the result of a completed sub-check for reuse.</summary>
    /// <param name="frame">The sub-check that completed.</param>
    /// <param name="result">Its decision.</param>
    public void SetMemo(EvalFrame frame, bool result) => _memo[frame] = result;

    /// <summary>
    /// Enters <paramref name="frame"/> on the current path. Returns false (without
    /// entering) if the frame is already on the path — a cycle, which the caller
    /// treats as a non-contributing <c>false</c>. Dispose the returned scope to leave the frame.
    /// </summary>
    /// <param name="frame">The sub-check to push onto the current path.</param>
    /// <param name="scope">A disposable that leaves the frame when disposed.</param>
    /// <returns><c>true</c> when the frame was entered; <c>false</c> on a cycle.</returns>
    /// <exception cref="EvaluationLimitException">The configured depth bound was exceeded.</exception>
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
}

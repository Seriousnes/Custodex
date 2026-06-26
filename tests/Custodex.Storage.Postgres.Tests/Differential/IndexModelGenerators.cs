using CsCheck;

using Custodex.Abstractions;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>One write applied to the differential model: <paramref name="Add"/> distinguishes an insert from a removal of <paramref name="Tuple"/>.</summary>
public sealed record WriteOp(bool Add, RelationTuple Tuple);

/// <summary>Generators that turn a model's tuples into a write sequence for the reverse-index differential tests, biased to add-then-remove exclusion tuples so re-add closure paths are exercised.</summary>
public static class IndexModelGenerators
{
    /// <summary>Projects a tuple set into a write sequence: each tuple is added, and every exclusion (<c>blocked</c>) tuple is also removed afterwards so the re-add path is exercised.</summary>
    public static List<WriteOp> BuildOps(IReadOnlyList<RelationTuple> tuples)
    {
        var ops = new List<WriteOp>();
        foreach (var t in tuples)
        {
            ops.Add(new WriteOp(Add: true, t));
            if (t.Relation == "blocked")
                ops.Add(new WriteOp(Add: false, t));
        }
        return ops;
    }

    /// <summary>A write sequence drawn from a random curated model, biased to include exclusion add/remove pairs.</summary>
    public static readonly Gen<IReadOnlyList<WriteOp>> WriteSequence =
        ModelGenerator.Gen.Select(m => (IReadOnlyList<WriteOp>)BuildOps(m.Tuples));
}

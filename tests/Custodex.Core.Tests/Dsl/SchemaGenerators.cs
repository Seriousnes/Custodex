using CsCheck;

using Custodex.Abstractions;
using Custodex.Core.Conditions;

namespace Custodex.Core.Tests.Dsl;

internal static class SchemaGenerators
{
    private static readonly string[] RelationPool = ["owner", "viewer", "editor", "member", "blocked"];
    private static readonly string[] CondPool = ["gated", "restricted"];

    private static readonly Gen<PermExpr> LeafExpr =
        Gen.OneOf(
            RelationPool.Select(r => Gen.Const<PermExpr>(new RelationRef(r))).ToArray()
        );

    private static Gen<PermExpr> PermExprGen(int depth)
    {
        if (depth <= 0) return LeafExpr;
        var sub = PermExprGen(depth - 1);
        return Gen.OneOf(
            LeafExpr,
            Gen.Select(sub, sub, (l, r) => (PermExpr)new Union(l, r)),
            Gen.Select(sub, sub, (l, r) => (PermExpr)new Intersect(l, r)),
            Gen.Select(sub, sub, (l, r) => (PermExpr)new Exclude(l, r)),
            Gen.Const<PermExpr>(new Arrow("container", "view")),
            Gen.Select(sub, Gen.OneOf(CondPool.Select(Gen.Const).ToArray()),
                (e, c) => (PermExpr)new Conditioned(e, c))
        );
    }

    private static readonly Gen<PermExpr> PermExprTree = PermExprGen(3);

    public static readonly Gen<Schema> SchemaGen =
        Gen.Select(
            PermExprTree,
            PermExprTree,
            (perm1, perm2) => new Schema("v1",
            [
                new EntityTypeDef("container", [new RelationDef("owner", [new SubjectTypeRef("user")])], []),
                new EntityTypeDef("widget",
                [
                    new RelationDef("owner", [new SubjectTypeRef("user")]),
                    new RelationDef("viewer", [new SubjectTypeRef("user"), new SubjectTypeRef("group", "member", false)]),
                    new RelationDef("blocked", [new SubjectTypeRef("user", null, true)]),
                    new RelationDef("container", [new SubjectTypeRef("container")]),
                ],
                [
                    new PermissionDef("view", perm1),
                    new PermissionDef("edit", perm2),
                ])
            ],
            [
                new ConditionDef("gated", [new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int)],
                    new BoolOp(
                        new Compare(new HourOf(new ContextNow()), CompareOp.Ge, new ParamRef("start")),
                        BoolConnective.And,
                        new Compare(new HourOf(new ContextNow()), CompareOp.Lt, new ParamRef("end")))),
                new ConditionDef("restricted", [], new EmptyConditionBody()),
                new ConditionDef("recent", [],
                    new Compare(new AttributeRef("created", ConditionType.Timestamp), CompareOp.Le, new ContextNow())),
            ]));
}

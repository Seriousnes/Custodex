using System.Text.Json.Serialization;

namespace Custodex.Abstractions;

/// <summary>
/// The base of the condition-body (ABAC) expression AST. Concrete nodes form a closed set the condition
/// evaluator switches over to compute a boolean from a condition's parameters and the request attributes.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(EmptyConditionBody), "empty")]
[JsonDerivedType(typeof(LiteralBool), "literalBool")]
[JsonDerivedType(typeof(LiteralInt), "literalInt")]
[JsonDerivedType(typeof(LiteralDouble), "literalDouble")]
[JsonDerivedType(typeof(LiteralString), "literalString")]
[JsonDerivedType(typeof(ParamRef), "paramRef")]
[JsonDerivedType(typeof(AttributeRef), "attributeRef")]
[JsonDerivedType(typeof(ContextNow), "contextNow")]
[JsonDerivedType(typeof(ContextSubject), "contextSubject")]
[JsonDerivedType(typeof(Compare), "compare")]
[JsonDerivedType(typeof(BoolOp), "boolOp")]
[JsonDerivedType(typeof(Not), "not")]
[JsonDerivedType(typeof(Arithmetic), "arithmetic")]
[JsonDerivedType(typeof(InList), "inList")]
[JsonDerivedType(typeof(HourOf), "hourOf")]
public abstract record ConditionExpr;

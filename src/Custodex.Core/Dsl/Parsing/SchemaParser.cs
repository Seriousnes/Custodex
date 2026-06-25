using Custodex.Abstractions;
using Custodex.Core.Dsl.Lexing;

namespace Custodex.Core.Dsl.Parsing;

/// <summary>Parses DSL schema text into the canonical <see cref="Schema"/> AST.</summary>
public static partial class SchemaParser
{
    /// <summary>Parses the given DSL source and returns a <see cref="Schema"/> with the supplied version.</summary>
    public static Schema Parse(string source, string version = "v1")
    {
        var tokens = Lexer.Tokenize(source);
        var stream = new TokenStream(tokens);
        return ParseSchema(stream, version);
    }

    private static Schema ParseSchema(TokenStream stream, string version)
    {
        var types = new List<EntityTypeDef>();
        var conditions = new List<ConditionDef>();

        while (stream.Peek().Kind != TokenKind.Eof)
        {
            var t = stream.Peek();
            if (t.Kind == TokenKind.Type)
            {
                types.Add(ParseType(stream));
            }
            else if (t.Kind == TokenKind.Condition)
            {
                conditions.Add(ParseCondition(stream));
            }
            else
            {
                throw new DslParseException($"Unexpected token '{t.Text}' at top level", t.Line, t.Column);
            }
        }

        return new Schema(version, types, conditions);
    }

    private static EntityTypeDef ParseType(TokenStream stream)
    {
        stream.Expect(TokenKind.Type);
        var name = stream.Expect(TokenKind.Ident).Text;
        stream.Expect(TokenKind.LBrace);

        var relations = new List<RelationDef>();
        var permissions = new List<PermissionDef>();

        while (stream.Peek().Kind != TokenKind.RBrace && stream.Peek().Kind != TokenKind.Eof)
        {
            var t = stream.Peek();
            if (t.Kind == TokenKind.Relation)
                relations.Add(ParseRelation(stream));
            else if (t.Kind == TokenKind.Permission)
                permissions.Add(ParsePermission(stream));
            else
                throw new DslParseException($"Expected 'relation' or 'permission' but found '{t.Text}'", t.Line, t.Column);
        }

        stream.Expect(TokenKind.RBrace);
        return new EntityTypeDef(name, relations, permissions);
    }

    private static RelationDef ParseRelation(TokenStream stream)
    {
        stream.Expect(TokenKind.Relation);
        var name = stream.Expect(TokenKind.Ident).Text;
        stream.Expect(TokenKind.Colon);

        var subjects = new List<SubjectTypeRef> { ParseSubject(stream) };
        while (stream.Accept(TokenKind.Pipe, out _))
            subjects.Add(ParseSubject(stream));

        return new RelationDef(name, subjects);
    }

    private static SubjectTypeRef ParseSubject(TokenStream stream)
    {
        var typeName = stream.Expect(TokenKind.Ident).Text;

        if (stream.Accept(TokenKind.Hash, out _))
        {
            var relation = stream.Expect(TokenKind.Ident).Text;
            return new SubjectTypeRef(typeName, relation, false);
        }

        if (stream.Accept(TokenKind.Colon, out _))
        {
            stream.Expect(TokenKind.Star);
            return new SubjectTypeRef(typeName, null, true);
        }

        return new SubjectTypeRef(typeName);
    }
}

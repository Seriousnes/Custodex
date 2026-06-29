using System.Runtime.CompilerServices;

using Custodex.Abstractions;

[assembly: InternalsVisibleTo("Custodex.Storage.MySql.Tests")]

namespace Custodex.Storage.MySql;

internal static class MySqlColumnLimits
{
    public const int StoreId = 64;
    public const int TenantId = 64;
    public const int EntityType = 64;
    public const int EntityId = 180;
    public const int Relation = 64;
    public const int SchemaVersion = 255;
    public const int ConditionName = 255;

    public static void EnsureWithin(string value, int max, string column)
    {
        if (value.Length > max)
            throw new ArgumentException(
                $"The {column} '{value}' has length {value.Length}, which exceeds the MySQL storage limit of {max} characters. " +
                "Over-length identifiers are rejected so they can never be silently truncated.",
                column);
    }

    public static void ValidateTenant(TenantContext t)
    {
        EnsureWithin(t.Store, StoreId, "store id");
        EnsureWithin(t.Tenant, TenantId, "tenant id");
    }

    public static void ValidateEntity(EntityRef obj)
    {
        EnsureWithin(obj.Type, EntityType, "object type");
        EnsureWithin(obj.Id, EntityId, "object id");
    }

    public static void ValidateTuple(RelationTuple tuple)
    {
        ValidateEntity(tuple.Object);
        EnsureWithin(tuple.Relation, Relation, "relation");
        EnsureWithin(tuple.Subject.Type, EntityType, "subject type");
        EnsureWithin(tuple.Subject.Id, EntityId, "subject id");
        if (tuple.Subject.Relation is { } subjectRelation)
            EnsureWithin(subjectRelation, Relation, "subject relation");
        if (tuple.Condition is { } condition)
            EnsureWithin(condition.Name, ConditionName, "condition name");
    }
}

using Custodex.Abstractions;

namespace Custodex.Service.Rest;

/// <summary>
/// Bidirectional converter between REST request/response DTOs and
/// <c>Custodex.Abstractions</c> contract records.
/// </summary>
public static class RestMap
{
    /// <summary>Converts an <see cref="EntityRefDto"/> to the contract record.</summary>
    public static EntityRef FromDto(EntityRefDto dto) => new(dto.Type, dto.Id);

    /// <summary>Converts a contract <see cref="EntityRef"/> to its DTO.</summary>
    public static EntityRefDto ToDto(EntityRef r) => new(r.Type, r.Id);

    /// <summary>Converts a <see cref="SubjectRefDto"/> to the contract record. A null <c>Relation</c> produces a plain subject.</summary>
    public static SubjectRef FromDto(SubjectRefDto dto) => new(dto.Type, dto.Id, dto.Relation);

    /// <summary>Converts a contract <see cref="SubjectRef"/> to its DTO.</summary>
    public static SubjectRefDto ToDto(SubjectRef r) => new(r.Type, r.Id, r.Relation);

    /// <summary>Converts a <see cref="RequestContextDto"/> to the contract record. A null <c>Now</c> defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public static RequestContext FromDto(RequestContextDto dto) =>
        new(dto.Now ?? DateTimeOffset.UtcNow,
            FromDto(dto.Subject),
            dto.Attributes ?? []);

    /// <summary>Converts a contract <see cref="ExplainNode"/> to its DTO, mapping children recursively.</summary>
    public static ExplainNodeDto ToDto(ExplainNode node) =>
        new(node.Description, node.Allowed, node.Children.Select(ToDto).ToList());

    /// <summary>Builds a <see cref="TenantContext"/> from separate store and tenant strings.</summary>
    public static TenantContext Tenant(string store, string tenant) => new(store, tenant);
}

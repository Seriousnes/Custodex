using Custodex.Abstractions;
using Custodex.Client.Transport;
using Custodex.Protos;

using Google.Protobuf.WellKnownTypes;

using Proto = Custodex.Api;

namespace Custodex.Client;

/// <summary>
/// Implements <see cref="IRelationManager"/> by forwarding calls to a remote
/// <c>Custodex.Service</c> via the gRPC <c>Relations</c> service.
/// </summary>
public sealed class GrpcRelationManager(Proto.Relations.RelationsClient client) : IRelationManager
{
    /// <inheritdoc/>
    public async Task<ConsistencyToken> WriteTuplesAsync(TenantContext tenant, string actor,
        IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        var proto = new Proto.WriteTuplesRequest { Actor = actor };
        foreach (var t in tuples) proto.Tuples.Add(ProtoMap.ToProto(t));
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.WriteTuplesAsync(proto, headers: ClientHeaders.TenantMeta(tenant), cancellationToken: ct).ResponseAsync);
        return new ConsistencyToken(response.ConsistencyToken);
    }

    /// <inheritdoc/>
    public async Task<ConsistencyToken> DeleteTuplesAsync(TenantContext tenant, string actor,
        IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
    {
        var proto = new Proto.DeleteTuplesRequest { Actor = actor };
        foreach (var t in tuples) proto.Tuples.Add(ProtoMap.ToProto(t));
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.DeleteTuplesAsync(proto, headers: ClientHeaders.TenantMeta(tenant), cancellationToken: ct).ResponseAsync);
        return new ConsistencyToken(response.ConsistencyToken);
    }

    /// <inheritdoc/>
    public async Task<ConsistencyToken> WriteAttributesAsync(TenantContext tenant, string actor,
        EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default)
    {
        var proto = new Proto.WriteAttributesRequest
        {
            Actor = actor,
            Object = ProtoMap.ToProto(obj),
            Attributes = ProtoMap.ToStruct(attributes),
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.WriteAttributesAsync(proto, headers: ClientHeaders.TenantMeta(tenant), cancellationToken: ct).ResponseAsync);
        return new ConsistencyToken(response.ConsistencyToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(
        TenantContext tenant, TupleFilter filter, CancellationToken ct = default)
    {
        var proto = new Proto.ReadTuplesRequest
        {
            Filter = new Proto.TupleFilter
            {
                ObjectType = filter.ObjectType ?? string.Empty,
                ObjectId = filter.ObjectId ?? string.Empty,
                Relation = filter.Relation ?? string.Empty,
                SubjectType = filter.SubjectType ?? string.Empty,
                SubjectId = filter.SubjectId ?? string.Empty,
            },
        };
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ReadTuplesAsync(proto, headers: ClientHeaders.TenantMeta(tenant), cancellationToken: ct).ResponseAsync);
        return [.. response.Tuples.Select(ProtoMap.FromProto)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(
        TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default)
    {
        var proto = new Proto.ReadChangeLogRequest
        {
            Actor = filter.Actor ?? string.Empty,
            Limit = filter.Limit,
        };
        if (filter.Since.HasValue)
            proto.Since = Timestamp.FromDateTimeOffset(filter.Since.Value);
        var response = await RemoteStatus.UnwrapAsync(() =>
            client.ReadChangeLogAsync(proto, headers: ClientHeaders.TenantMeta(tenant), cancellationToken: ct).ResponseAsync);
        return [.. response.Entries.Select(e => new ChangeLogEntry(
            e.Id, e.Actor, e.Operation, e.Target,
            string.IsNullOrEmpty(e.BeforeJson) ? null : (object)e.BeforeJson,
            string.IsNullOrEmpty(e.AfterJson) ? null : (object)e.AfterJson,
            e.OccurredAt.ToDateTimeOffset()))];
    }
}

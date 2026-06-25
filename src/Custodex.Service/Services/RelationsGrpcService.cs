using Custodex.Service.Mapping;
using Custodex.Service.Tenancy;
using Custodex.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contracts = Custodex.Abstractions;

namespace Custodex.Service.Services;

/// <summary>
/// gRPC service for tuple and attribute management, delegating to <see cref="Contracts.IRelationManager"/>.
/// </summary>
public sealed class RelationsGrpcService(
    Contracts.IRelationManager relations,
    ITenantContextAccessor tc) : Relations.RelationsBase
{
    /// <inheritdoc />
    public override async Task<WriteTuplesResponse> WriteTuples(WriteTuplesRequest request, ServerCallContext context)
    {
        var tuples = request.Tuples.Select(FromProto).ToList();
        await relations.WriteTuplesAsync(tc.Current, request.Actor, tuples, context.CancellationToken);
        return new WriteTuplesResponse { Count = tuples.Count };
    }

    /// <inheritdoc />
    public override async Task<DeleteTuplesResponse> DeleteTuples(DeleteTuplesRequest request, ServerCallContext context)
    {
        var tuples = request.Tuples.Select(FromProto).ToList();
        await relations.DeleteTuplesAsync(tc.Current, request.Actor, tuples, context.CancellationToken);
        return new DeleteTuplesResponse { Count = tuples.Count };
    }

    /// <inheritdoc />
    public override async Task<WriteAttributesResponse> WriteAttributes(WriteAttributesRequest request, ServerCallContext context)
    {
        await relations.WriteAttributesAsync(
            tc.Current,
            request.Actor,
            ProtoMap.FromProto(request.Object),
            ProtoMap.FromStruct(request.Attributes),
            context.CancellationToken);
        return new WriteAttributesResponse();
    }

    /// <inheritdoc />
    public override async Task<ReadTuplesResponse> ReadTuples(ReadTuplesRequest request, ServerCallContext context)
    {
        var filter = new Contracts.TupleFilter(
            ObjectType: NullIfEmpty(request.Filter?.ObjectType),
            ObjectId: NullIfEmpty(request.Filter?.ObjectId),
            Relation: NullIfEmpty(request.Filter?.Relation),
            SubjectType: NullIfEmpty(request.Filter?.SubjectType),
            SubjectId: NullIfEmpty(request.Filter?.SubjectId));

        var tuples = await relations.ReadTuplesAsync(tc.Current, filter, context.CancellationToken);
        var response = new ReadTuplesResponse();
        response.Tuples.AddRange(tuples.Select(ToProto));
        return response;
    }

    /// <inheritdoc />
    public override async Task<ReadChangeLogResponse> ReadChangeLog(ReadChangeLogRequest request, ServerCallContext context)
    {
        var filter = new Contracts.ChangeLogFilter(
            Since: request.Since?.ToDateTimeOffset(),
            Actor: NullIfEmpty(request.Actor),
            Limit: request.Limit > 0 ? request.Limit : 100);

        var entries = await relations.ReadChangeLogAsync(tc.Current, filter, context.CancellationToken);
        var response = new ReadChangeLogResponse();
        response.Entries.AddRange(entries.Select(e => new ChangeLogEntry
        {
            Id = e.Id,
            Actor = e.Actor,
            Operation = e.Operation,
            Target = e.Target,
            BeforeJson = e.Before is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(e.Before),
            AfterJson = e.After is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(e.After),
            OccurredAt = Timestamp.FromDateTimeOffset(e.OccurredAt),
        }));
        return response;
    }

    private static Contracts.RelationTuple FromProto(RelationTuple t) =>
        new(ProtoMap.FromProto(t.Object), t.Relation, ProtoMap.FromProto(t.Subject),
            t.Condition is { Name.Length: > 0 } c ? ProtoMap.FromProto(c) : null);

    private static RelationTuple ToProto(Contracts.RelationTuple t)
    {
        var msg = new RelationTuple
        {
            Object = ProtoMap.ToProto(t.Object),
            Relation = t.Relation,
            Subject = ProtoMap.ToProto(t.Subject),
        };
        if (t.Condition is not null)
            msg.Condition = ProtoMap.ToProto(t.Condition);
        return msg;
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrEmpty(s) ? null : s;
}

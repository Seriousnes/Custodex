using Custodex.Abstractions;
using Custodex.TestKit;

using Grpc.Core;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class GrpcRelationManagerMappingTests
{
    [Fact]
    public async Task WriteTuples_returns_the_consistency_token_from_the_response()
    {
        var world = TestWorld.New();
        var expected = ConsistencyToken.Create(world.Tenant, epoch: 4, changeLogId: 12);
        var fake = new FakeRelationsClient(new Custodex.Api.WriteTuplesResponse
        {
            Count = 1,
            ConsistencyToken = expected.Value,
        });
        var manager = new GrpcRelationManager(fake);

        var token = await manager.WriteTuplesAsync(world.Tenant, world.SubjectId(),
        [
            new RelationTuple(new EntityRef(world.EntityType(), world.ObjectId()), world.Relation(), world.User(world.SubjectId())),
        ]);

        token.Value.ShouldBe(expected.Value);
        token.Decode().Tenant.ShouldBe(world.Tenant.Tenant);
    }

    private sealed class FakeRelationsClient(Custodex.Api.WriteTuplesResponse response) : Custodex.Api.Relations.RelationsClient
    {
        private readonly Custodex.Api.WriteTuplesResponse _response = response;

        public override AsyncUnaryCall<Custodex.Api.WriteTuplesResponse> WriteTuplesAsync(
            Custodex.Api.WriteTuplesRequest request, CallOptions options) =>
            new(Task.FromResult(_response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
    }
}

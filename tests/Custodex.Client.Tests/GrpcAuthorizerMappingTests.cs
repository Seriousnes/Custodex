using Custodex.Abstractions;

using Grpc.Core;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class GrpcAuthorizerMappingTests
{
    [Fact]
    public async Task Check_shapes_request_correctly_and_returns_allowed()
    {
        var fake = new FakeDecisionClient(allowed: true);
        var authorizer = new GrpcAuthorizer(fake);

        var tenant = new TenantContext("s-1", "t-1");
        var result = await authorizer.CheckAsync(new CheckRequest(
            tenant,
            new EntityRef("doc", "d-1"),
            "edit",
            new SubjectRef("user", "u-1", null),
            new RequestContext(DateTimeOffset.UtcNow, new SubjectRef("user", "u-1", null), new Dictionary<string, object?>())));

        result.Allowed.ShouldBeTrue();
        fake.LastCheckRequest.ShouldNotBeNull();
        fake.LastCheckRequest!.Permission.ShouldBe("edit");
        fake.LastCheckRequest.Object.Id.ShouldBe("d-1");
    }

    [Fact]
    public async Task ListObjects_null_token_sends_empty_string_and_empty_response_decodes_to_null()
    {
        var fake = new FakeDecisionClient(objectIds: [], continuationToken: string.Empty);
        var authorizer = new GrpcAuthorizer(fake);

        var result = await authorizer.ListObjectsAsync(new ListObjectsRequest(
            new TenantContext("s-1", "t-1"),
            new SubjectRef("user", "u-1", null),
            "doc",
            "edit",
            new RequestContext(DateTimeOffset.UtcNow, new SubjectRef("user", "u-1", null), new Dictionary<string, object?>()),
            ContinuationToken: null));

        result.ContinuationToken.ShouldBeNull();
        fake.LastListObjectsRequest.ShouldNotBeNull();
        fake.LastListObjectsRequest!.ContinuationToken.ShouldBe(string.Empty);
    }

    private sealed class FakeDecisionClient : Custodex.V1.Decision.DecisionClient
    {
        private readonly bool _allowed;
        private readonly IEnumerable<string> _objectIds;
        private readonly string _continuationToken;

        public Custodex.V1.CheckRequest? LastCheckRequest { get; private set; }
        public Custodex.V1.ListObjectsRequest? LastListObjectsRequest { get; private set; }

        public FakeDecisionClient(bool allowed = false, IEnumerable<string>? objectIds = null, string continuationToken = "")
        {
            _allowed = allowed;
            _objectIds = objectIds ?? [];
            _continuationToken = continuationToken;
        }

        public override AsyncUnaryCall<Custodex.V1.CheckResponse> CheckAsync(
            Custodex.V1.CheckRequest request,
            CallOptions options = default)
        {
            LastCheckRequest = request;
            var response = new Custodex.V1.CheckResponse { Allowed = _allowed };
            return new AsyncUnaryCall<Custodex.V1.CheckResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override AsyncUnaryCall<Custodex.V1.ListObjectsResponse> ListObjectsAsync(
            Custodex.V1.ListObjectsRequest request,
            CallOptions options = default)
        {
            LastListObjectsRequest = request;
            var response = new Custodex.V1.ListObjectsResponse
            {
                ContinuationToken = _continuationToken,
            };
            foreach (var id in _objectIds)
                response.ObjectIds.Add(id);
            return new AsyncUnaryCall<Custodex.V1.ListObjectsResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }
}

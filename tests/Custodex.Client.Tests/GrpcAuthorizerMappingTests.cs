using Custodex.Abstractions;
using Custodex.TestKit;

using Grpc.Core;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class GrpcAuthorizerMappingTests
{
    [Fact]
    public async Task Conditional_response_round_trips_decision_and_unmet_conditions()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var key = world.ParamName();
        var canned = new Custodex.Api.CheckResponse
        {
            Allowed = false,
            Decision = Custodex.Api.CheckDecision.Conditional,
        };
        var unmet = new Custodex.Api.UnmetCondition { Condition = condition };
        unmet.MissingKeys.Add(key);
        canned.UnmetConditions.Add(unmet);

        var authorizer = new GrpcAuthorizer(new FakeDecisionClient(checkResponse: canned));
        var result = await authorizer.CheckAsync(Request(world));

        result.Decision.ShouldBe(CheckDecision.Conditional);
        result.Allowed.ShouldBeFalse();
        result.UnmetConditions.Count.ShouldBe(1);
        result.UnmetConditions[0].Condition.ShouldBe(condition);
        result.UnmetConditions[0].MissingKeys.ShouldBe([key]);
    }

    [Fact]
    public async Task Legacy_response_without_a_decision_field_reconciles_from_allowed()
    {
        var world = TestWorld.New();
        var canned = new Custodex.Api.CheckResponse { Allowed = true };

        var authorizer = new GrpcAuthorizer(new FakeDecisionClient(checkResponse: canned));
        var result = await authorizer.CheckAsync(Request(world));

        result.Decision.ShouldBe(CheckDecision.Allow);
        result.Allowed.ShouldBeTrue();
        result.UnmetConditions.ShouldBeEmpty();
    }

    private static CheckRequest Request(TestWorld world)
    {
        var subject = world.User(world.SubjectId());
        return new CheckRequest(
            world.Tenant,
            new EntityRef(world.EntityType(), world.ObjectId()),
            world.Permission(),
            subject,
            new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>()));
    }

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
    public async Task Check_forwards_the_at_least_as_fresh_consistency_selector()
    {
        var world = TestWorld.New();
        var token = ConsistencyToken.Create(world.Tenant, epoch: 6, changeLogId: 2);
        var fake = new FakeDecisionClient(allowed: true);
        var authorizer = new GrpcAuthorizer(fake);
        var subject = world.User(world.SubjectId());

        await authorizer.CheckAsync(new CheckRequest(
            world.Tenant,
            new EntityRef(world.EntityType(), world.ObjectId()),
            world.Permission(),
            subject,
            new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>(),
                Consistency.AtLeastAsFresh(token))));

        fake.LastCheckRequest!.Context.Consistency.Mode.ShouldBe(Custodex.Api.ConsistencyMode.AtLeastAsFresh);
        fake.LastCheckRequest.Context.Consistency.Token.ShouldBe(token.Value);
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

    private sealed class FakeDecisionClient(
        bool allowed = false, IEnumerable<string>? objectIds = null, string continuationToken = "",
        Custodex.Api.CheckResponse? checkResponse = null) : Custodex.Api.Decision.DecisionClient
    {
        private readonly bool _allowed = allowed;
        private readonly IEnumerable<string> _objectIds = objectIds ?? [];
        private readonly string _continuationToken = continuationToken;
        private readonly Custodex.Api.CheckResponse? _checkResponse = checkResponse;

        public Custodex.Api.CheckRequest? LastCheckRequest { get; private set; }
        public Custodex.Api.ListObjectsRequest? LastListObjectsRequest { get; private set; }

        public override AsyncUnaryCall<Custodex.Api.CheckResponse> CheckAsync(
            Custodex.Api.CheckRequest request,
            CallOptions options = default)
        {
            LastCheckRequest = request;
            var response = _checkResponse ?? new Custodex.Api.CheckResponse { Allowed = _allowed };
            return new AsyncUnaryCall<Custodex.Api.CheckResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override AsyncUnaryCall<Custodex.Api.ListObjectsResponse> ListObjectsAsync(
            Custodex.Api.ListObjectsRequest request,
            CallOptions options = default)
        {
            LastListObjectsRequest = request;
            var response = new Custodex.Api.ListObjectsResponse
            {
                ContinuationToken = _continuationToken,
            };
            foreach (var id in _objectIds)
                response.ObjectIds.Add(id);
            return new AsyncUnaryCall<Custodex.Api.ListObjectsResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }
    }
}

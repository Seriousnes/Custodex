using Custodex.Abstractions;
using Custodex.Core;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using DecisionClient = Custodex.Api.Decision.DecisionClient;
using ProvisioningClient = Custodex.Api.Provisioning.ProvisioningClient;
using RelationsClient = Custodex.Api.Relations.RelationsClient;
using SchemaGrpcClient = Custodex.Api.Schema.SchemaClient;

namespace Custodex.Client.Tests;

[Collection("client-parity")]
public sealed class ClientParityTests(ServiceFixture fx)
{
    private static RequestContext Ctx(SubjectRef subject) =>
        new(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>());

    private async Task<(IAuthorizer Remote, IAuthorizer InProcess, TenantContext Tenant)> SetupAsync(
        Schema schema, RelationTuple[] tuples)
    {
        var tenant = $"t-{Guid.NewGuid():N}";
        var tc = new TenantContext(ServiceFixture.AdminStore, tenant);

        var storeMgr = new GrpcStoreManager(new ProvisioningClient(fx.GrpcChannel));
        var tenantMgr = new GrpcTenantManager(new ProvisioningClient(fx.GrpcChannel));
        var schemaMgr = new GrpcSchemaManager(new SchemaGrpcClient(fx.GrpcChannel));
        var relationMgr = new GrpcRelationManager(new RelationsClient(fx.GrpcChannel));
        var remote = new GrpcAuthorizer(new DecisionClient(fx.GrpcChannel));

        await storeMgr.CreateStoreAsync(ServiceFixture.AdminStore);
        await tenantMgr.CreateTenantAsync(tc);
        await schemaMgr.SetActiveSchemaAsync(ServiceFixture.AdminStore, schema);
        if (tuples.Length > 0)
            await relationMgr.WriteTuplesAsync(tc, "test", tuples);

        var inProcess = fx.Factory.Services.GetRequiredService<IAuthorizer>();
        return (remote, inProcess, tc);
    }

    [Fact]
    public async Task Example_1_direct_grant_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("read", p => p.Relation("owner")))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var userId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "owner", new SubjectRef("user", userId, null), null)
        ]);

        var req = new CheckRequest(tc, new EntityRef("res", objId), "read",
            new SubjectRef("user", userId, null), Ctx(new SubjectRef("user", userId, null)));

        var remoteResult = await remote.CheckAsync(req);
        var localResult = await inProcess.CheckAsync(req);

        remoteResult.Allowed.ShouldBeTrue();
        remoteResult.Allowed.ShouldBe(localResult.Allowed);
    }

    [Fact]
    public async Task Example_2_union_of_two_relations_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("editor", s => s.Type("user"))
                .Relation("viewer", s => s.Type("user"))
                .Permission("read", p => p.Relation("editor").Relation("viewer")))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var viewerId = $"u-{Guid.NewGuid():N}";
        var otherId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "viewer", new SubjectRef("user", viewerId, null), null)
        ]);

        var allowedReq = new CheckRequest(tc, new EntityRef("res", objId), "read",
            new SubjectRef("user", viewerId, null), Ctx(new SubjectRef("user", viewerId, null)));
        var deniedReq = new CheckRequest(tc, new EntityRef("res", objId), "read",
            new SubjectRef("user", otherId, null), Ctx(new SubjectRef("user", otherId, null)));

        var remoteAllowed = await remote.CheckAsync(allowedReq);
        var remoteDenied = await remote.CheckAsync(deniedReq);
        var localAllowed = await inProcess.CheckAsync(allowedReq);
        var localDenied = await inProcess.CheckAsync(deniedReq);

        remoteAllowed.Allowed.ShouldBeTrue();
        remoteDenied.Allowed.ShouldBeFalse();
        remoteAllowed.Allowed.ShouldBe(localAllowed.Allowed);
        remoteDenied.Allowed.ShouldBe(localDenied.Allowed);
    }

    [Fact]
    public async Task Example_3_exclusion_removes_banned_subject_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("member", s => s.Type("user"))
                .Relation("banned", s => s.Type("user"))
                .Permission("access", p => p.Relation("member").Exclude(e => e.Relation("banned"))))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var memberId = $"u-{Guid.NewGuid():N}";
        var bannedId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "member", new SubjectRef("user", memberId, null), null),
            new RelationTuple(new EntityRef("res", objId), "member", new SubjectRef("user", bannedId, null), null),
            new RelationTuple(new EntityRef("res", objId), "banned", new SubjectRef("user", bannedId, null), null),
        ]);

        var allowedReq = new CheckRequest(tc, new EntityRef("res", objId), "access",
            new SubjectRef("user", memberId, null), Ctx(new SubjectRef("user", memberId, null)));
        var deniedReq = new CheckRequest(tc, new EntityRef("res", objId), "access",
            new SubjectRef("user", bannedId, null), Ctx(new SubjectRef("user", bannedId, null)));

        (await remote.CheckAsync(allowedReq)).Allowed.ShouldBeTrue();
        (await remote.CheckAsync(deniedReq)).Allowed.ShouldBeFalse();
        (await remote.CheckAsync(allowedReq)).Allowed.ShouldBe((await inProcess.CheckAsync(allowedReq)).Allowed);
        (await remote.CheckAsync(deniedReq)).Allowed.ShouldBe((await inProcess.CheckAsync(deniedReq)).Allowed);
    }

    [Fact]
    public async Task Example_4_arrow_traversal_through_parent_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("read", p => p.Relation("owner")))
            .Type("doc", t => t
                .Relation("parent", s => s.Type("folder"))
                .Permission("read", p => p.Arrow("parent", "read")))
            .Build();
        var folderId = $"f-{Guid.NewGuid():N}";
        var docId = $"d-{Guid.NewGuid():N}";
        var userId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("folder", folderId), "owner", new SubjectRef("user", userId, null), null),
            new RelationTuple(new EntityRef("doc", docId), "parent", new SubjectRef("folder", folderId, null), null),
        ]);

        var req = new CheckRequest(tc, new EntityRef("doc", docId), "read",
            new SubjectRef("user", userId, null), Ctx(new SubjectRef("user", userId, null)));

        (await remote.CheckAsync(req)).Allowed.ShouldBeTrue();
        (await remote.CheckAsync(req)).Allowed.ShouldBe((await inProcess.CheckAsync(req)).Allowed);
    }

    [Fact]
    public async Task Example_5_intersection_gate_requires_both_conditions_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("approved", s => s.Type("user"))
                .Relation("certified", s => s.Type("user"))
                .Permission("access", p => p
                    .Relation("approved")
                    .Intersect(x => x.Relation("certified"))))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var fullId = $"u-{Guid.NewGuid():N}";
        var halfId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "approved", new SubjectRef("user", fullId, null), null),
            new RelationTuple(new EntityRef("res", objId), "certified", new SubjectRef("user", fullId, null), null),
            new RelationTuple(new EntityRef("res", objId), "approved", new SubjectRef("user", halfId, null), null),
        ]);

        var allowedReq = new CheckRequest(tc, new EntityRef("res", objId), "access",
            new SubjectRef("user", fullId, null), Ctx(new SubjectRef("user", fullId, null)));
        var deniedReq = new CheckRequest(tc, new EntityRef("res", objId), "access",
            new SubjectRef("user", halfId, null), Ctx(new SubjectRef("user", halfId, null)));

        (await remote.CheckAsync(allowedReq)).Allowed.ShouldBeTrue();
        (await remote.CheckAsync(deniedReq)).Allowed.ShouldBeFalse();
        (await remote.CheckAsync(allowedReq)).Allowed.ShouldBe((await inProcess.CheckAsync(allowedReq)).Allowed);
        (await remote.CheckAsync(deniedReq)).Allowed.ShouldBe((await inProcess.CheckAsync(deniedReq)).Allowed);
    }

    [Fact]
    public async Task Example_6_wildcard_grants_all_users_remote_equals_in_process()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("public", s => s.Wildcard("user"))
                .Permission("view", p => p.Relation("public")))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var anyUserId = $"u-{Guid.NewGuid():N}";
        var (remote, inProcess, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "public", new SubjectRef("user", "*", null), null),
        ]);

        var req = new CheckRequest(tc, new EntityRef("res", objId), "view",
            new SubjectRef("user", anyUserId, null), Ctx(new SubjectRef("user", anyUserId, null)));

        (await remote.CheckAsync(req)).Allowed.ShouldBeTrue();
        (await remote.CheckAsync(req)).Allowed.ShouldBe((await inProcess.CheckAsync(req)).Allowed);
    }

    [Fact]
    public async Task Unknown_permission_via_remote_throws_UnknownPermissionException()
    {
        var schema = new SchemaBuilder("v1")
            .Type("res", t => t
                .Relation("owner", s => s.Type("user"))
                .Permission("read", p => p.Relation("owner")))
            .Build();
        var objId = $"obj-{Guid.NewGuid():N}";
        var userId = $"u-{Guid.NewGuid():N}";
        var (remote, _, tc) = await SetupAsync(schema, [
            new RelationTuple(new EntityRef("res", objId), "owner", new SubjectRef("user", userId, null), null),
        ]);

        var req = new CheckRequest(tc, new EntityRef("res", objId), "nonexistent_permission",
            new SubjectRef("user", userId, null), Ctx(new SubjectRef("user", userId, null)));

        await Should.ThrowAsync<UnknownPermissionException>(() => remote.CheckAsync(req));
    }
}

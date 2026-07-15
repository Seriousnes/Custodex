using System.Diagnostics.Metrics;

using Custodex.Abstractions;
using Custodex.Core;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Di;

[Collection("postgres")]
public class CheckDurationMetricsTests(PostgresFixture fx) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Check_through_the_UsePostgres_authorizer_records_duration_and_a_cache_hit_does_not()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var viewer = world.Relation();
        var view = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();

        var schemaBuilder = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(world.UserType))
                .Permission(view, p => p.Relation(viewer)));

        var services = new ServiceCollection();
        services.AddCustodex().UsePostgres(fx.ConnectionString).UseSchema(schemaBuilder);
        await using var provider = services.BuildServiceProvider();

        var store = world.Tenant.Store;
        await provider.GetRequiredService<IStoreManager>().CreateStoreAsync(store);
        await provider.GetRequiredService<ITenantManager>().CreateTenantAsync(world.Tenant);
        await provider.GetRequiredService<ITenantManager>().CreateTenantAsync(new TenantContext(store, store));
        await provider.GetRequiredService<ISchemaManager>().SetActiveSchemaAsync(store, schemaBuilder.Build());
        await provider.GetRequiredService<IRelationManager>().WriteTuplesAsync(world.Tenant, world.SubjectId(),
            [TestWorld.Tuple(objType, objId, viewer, world.User(subjectId))]);

        var recorded = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CustodexDiagnostics.Name && instrument.Name == "Custodex.check.duration")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => recorded++);
        listener.Start();

        using var scope = provider.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IAuthorizer>();
        var request = world.Check(objType, objId, view, subjectId);

        var first = await authorizer.CheckAsync(request);
        first.Allowed.ShouldBeTrue();
        recorded.ShouldBe(1);

        var second = await authorizer.CheckAsync(request);
        second.Allowed.ShouldBeTrue();
        recorded.ShouldBe(1);
    }
}

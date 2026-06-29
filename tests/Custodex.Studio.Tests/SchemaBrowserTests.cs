using Blazor.Diagrams.Components;
using Blazor.Diagrams.Core.Geometry;

using Bunit;

using Custodex.Abstractions;
using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.Studio.Components.Schema;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class SchemaBrowserTests
{
    private sealed class FakeSchemaManager : ISchemaManager
    {
        private readonly Schema? _schema;

        public FakeSchemaManager(Schema? schema) => _schema = schema;

        public SchemaValidationResult ValidateSchema(Schema schema) => new(true, []);

        public Task SetActiveSchemaAsync(string store, Schema schema, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Schema?> GetActiveSchemaAsync(string store, CancellationToken ct = default) =>
            Task.FromResult(_schema);
    }

    private static BunitContext CreateContext(StudioConnectionState state, ISchemaManager manager)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1000, 800));
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(manager);
        return ctx;
    }

    [Fact]
    public void Not_connected_shows_notice_and_no_canvas()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeSchemaManager(null));

        var cut = ctx.Render<SchemaBrowser>();

        cut.Markup.ShouldContain("Overview");
        cut.FindComponents<DiagramCanvas>().ShouldBeEmpty();
    }

    [Fact]
    public void Connected_with_no_active_schema_shows_notice()
    {
        var world = TestWorld.New();
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, new FakeSchemaManager(null));

        var cut = ctx.Render<SchemaBrowser>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No active schema"));
        cut.FindComponents<DiagramCanvas>().ShouldBeEmpty();
    }

    [Fact]
    public void Schema_renders_tree_names_a_formatted_expression_and_the_canvas()
    {
        var world = TestWorld.New();
        var userType = world.EntityType();
        var resourceType = world.EntityType();
        var viewRel = world.Relation();
        var readPerm = world.Permission();
        var otherPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [new PermissionDef(readPerm, new Union(new RelationRef(viewRel), new Arrow(viewRel, otherPerm)))])
        ], []);

        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, new FakeSchemaManager(schema));

        var cut = ctx.Render<SchemaBrowser>();

        var formatted = PermExprFormatter.Format(schema.Types[1].Permissions[0].Expression);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain(resourceType);
            cut.Markup.ShouldContain(viewRel);
            cut.Markup.ShouldContain(readPerm);
            cut.Find(".schema-expr").TextContent.ShouldContain(formatted);
        });

        cut.FindComponents<DiagramCanvas>().Count.ShouldBe(1);
    }

    [Fact]
    public void Wildcard_and_subject_set_subjects_are_summarised_in_the_tree()
    {
        var world = TestWorld.New();
        var userType = world.EntityType();
        var groupType = world.EntityType();
        var memberRel = world.Relation();
        var resourceType = world.EntityType();
        var viewRel = world.Relation();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(groupType, [new RelationDef(memberRel, [new SubjectTypeRef(userType)])], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel,
                [
                    new SubjectTypeRef(userType, Wildcard: true),
                    new SubjectTypeRef(groupType, memberRel)
                ])],
                [])
        ], []);

        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, new FakeSchemaManager(schema));

        var cut = ctx.Render<SchemaBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain($"{userType}:*");
            cut.Markup.ShouldContain($"{groupType}#{memberRel}");
        });
    }
}

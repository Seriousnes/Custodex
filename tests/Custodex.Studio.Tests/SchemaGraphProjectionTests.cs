using Custodex.Abstractions;
using Custodex.Studio.Components.Schema;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class SchemaGraphProjectionTests
{
    [Fact]
    public void Projects_a_node_for_each_type_relation_and_permission()
    {
        var world = TestWorld.New();
        var userType = world.EntityType();
        var resourceType = world.EntityType();
        var viewRel = world.Relation();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [new PermissionDef(readPerm, new RelationRef(viewRel))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Nodes.ShouldContain(n => n.Id == $"type:{userType}" && n.Kind == SchemaNodeKind.Type);
        graph.Nodes.ShouldContain(n => n.Id == $"type:{resourceType}" && n.Kind == SchemaNodeKind.Type);
        graph.Nodes.ShouldContain(n => n.Id == $"relation:{resourceType}.{viewRel}" && n.Label == viewRel && n.Kind == SchemaNodeKind.Relation);
        graph.Nodes.ShouldContain(n => n.Id == $"permission:{resourceType}.{readPerm}" && n.Label == readPerm && n.Kind == SchemaNodeKind.Permission);
    }

    [Fact]
    public void Declares_edges_connect_a_type_to_its_relations_and_permissions()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var userType = world.EntityType();
        var viewRel = world.Relation();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [new PermissionDef(readPerm, new RelationRef(viewRel))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.ShouldContain(e => e.FromId == $"type:{resourceType}" && e.ToId == $"relation:{resourceType}.{viewRel}" && e.Kind == SchemaEdgeKind.DeclaresRelation);
        graph.Edges.ShouldContain(e => e.FromId == $"type:{resourceType}" && e.ToId == $"permission:{resourceType}.{readPerm}" && e.Kind == SchemaEdgeKind.DeclaresPermission);
    }

    [Fact]
    public void AllowsSubject_edge_targets_the_subject_type_node()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var userType = world.EntityType();
        var viewRel = world.Relation();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.ShouldContain(e => e.FromId == $"relation:{resourceType}.{viewRel}" && e.ToId == $"type:{userType}" && e.Kind == SchemaEdgeKind.AllowsSubject);
    }

    [Fact]
    public void AllowsSubject_edge_targets_the_qualified_relation_node_when_present()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var groupType = world.EntityType();
        var memberRel = world.Relation();
        var editRel = world.Relation();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(groupType, [new RelationDef(memberRel, [])], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(editRel, [new SubjectTypeRef(groupType, memberRel)])],
                [])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.ShouldContain(e => e.FromId == $"relation:{resourceType}.{editRel}" && e.ToId == $"relation:{groupType}.{memberRel}" && e.Kind == SchemaEdgeKind.AllowsSubject);
    }

    [Fact]
    public void RelationRef_in_a_permission_yields_a_references_edge_to_the_same_type_relation()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var userType = world.EntityType();
        var viewRel = world.Relation();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [new PermissionDef(readPerm, new RelationRef(viewRel))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.ShouldContain(e => e.FromId == $"permission:{resourceType}.{readPerm}" && e.ToId == $"relation:{resourceType}.{viewRel}" && e.Kind == SchemaEdgeKind.References);
    }

    [Fact]
    public void Arrow_in_a_permission_yields_a_references_edge_to_the_named_relation()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var parentRel = world.Relation();
        var folderType = world.EntityType();
        var folderPerm = world.Permission();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(folderType, [], [new PermissionDef(folderPerm, new RelationRef(parentRel))]),
            new EntityTypeDef(resourceType,
                [new RelationDef(parentRel, [new SubjectTypeRef(folderType)])],
                [new PermissionDef(readPerm, new Arrow(parentRel, folderPerm))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.ShouldContain(e => e.FromId == $"permission:{resourceType}.{readPerm}" && e.ToId == $"relation:{resourceType}.{parentRel}" && e.Kind == SchemaEdgeKind.References);
    }

    [Fact]
    public void Reference_edges_are_deduplicated()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var userType = world.EntityType();
        var viewRel = world.Relation();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(viewRel, [new SubjectTypeRef(userType)])],
                [new PermissionDef(readPerm, new Union(new RelationRef(viewRel), new RelationRef(viewRel)))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Edges.Count(e => e.FromId == $"permission:{resourceType}.{readPerm}" && e.ToId == $"relation:{resourceType}.{viewRel}" && e.Kind == SchemaEdgeKind.References)
            .ShouldBe(1);
    }

    [Fact]
    public void Unknown_relation_references_do_not_throw_and_emit_a_best_effort_edge()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var missingRel = world.Relation();
        var readPerm = world.Permission();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(resourceType, [], [new PermissionDef(readPerm, new RelationRef(missingRel))])
        ], []);

        var graph = Should.NotThrow(() => SchemaGraphProjection.Project(schema));

        graph.Edges.ShouldContain(e => e.FromId == $"permission:{resourceType}.{readPerm}" && e.ToId == $"relation:{resourceType}.{missingRel}" && e.Kind == SchemaEdgeKind.References);
    }

    [Fact]
    public void Relation_and_permission_with_same_name_yield_two_distinct_nodes()
    {
        var world = TestWorld.New();
        var resourceType = world.EntityType();
        var userType = world.EntityType();
        var sharedName = world.Relation();

        var schema = new Schema(TestWorld.Version,
        [
            new EntityTypeDef(userType, [], []),
            new EntityTypeDef(resourceType,
                [new RelationDef(sharedName, [new SubjectTypeRef(userType)])],
                [new PermissionDef(sharedName, new RelationRef(sharedName))])
        ], []);

        var graph = SchemaGraphProjection.Project(schema);

        graph.Nodes.ShouldContain(n => n.Id == $"relation:{resourceType}.{sharedName}" && n.Kind == SchemaNodeKind.Relation);
        graph.Nodes.ShouldContain(n => n.Id == $"permission:{resourceType}.{sharedName}" && n.Kind == SchemaNodeKind.Permission);
        graph.Nodes.Count(n => n.Label == sharedName && n.Id.StartsWith("relation:")).ShouldBe(1);
        graph.Nodes.Count(n => n.Label == sharedName && n.Id.StartsWith("permission:")).ShouldBe(1);

        graph.Edges.ShouldContain(e => e.FromId == $"type:{resourceType}" && e.ToId == $"relation:{resourceType}.{sharedName}" && e.Kind == SchemaEdgeKind.DeclaresRelation);
        graph.Edges.ShouldContain(e => e.FromId == $"type:{resourceType}" && e.ToId == $"permission:{resourceType}.{sharedName}" && e.Kind == SchemaEdgeKind.DeclaresPermission);
        graph.Edges.ShouldContain(e => e.FromId == $"permission:{resourceType}.{sharedName}" && e.ToId == $"relation:{resourceType}.{sharedName}" && e.Kind == SchemaEdgeKind.References);
    }
}

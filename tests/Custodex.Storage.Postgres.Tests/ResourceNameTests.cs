using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

public class ResourceNameTests
{
    [Fact]
    public void Migration_sql_is_embedded_with_correct_resource_name()
    {
        var names = typeof(MigrationRunner).Assembly.GetManifestResourceNames();
        names.ShouldContain("Custodex.Storage.Postgres.Migrations.001_initial_schema.sql");
    }
}

using System.Data;
using System.Data.Common;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class UnitOfWorkFactoryTests(PostgresFixture fx)
{
    private sealed class NotNpgsqlConnection : DbConnection
    {
#pragma warning disable CS8764, CS8765
        public override string ConnectionString { get => ""; set { } }
#pragma warning restore CS8764, CS8765
        public override string Database => "";
        public override string DataSource => "";
        public override string ServerVersion => "";
        public override ConnectionState State => ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => throw new NotSupportedException();
        public override void Open() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    [Fact]
    public async Task BeginAsync_returns_an_owned_unit_of_work_with_live_handles()
    {
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        await using var uow = await factory.BeginAsync();

        var npg = NpgsqlUnitOfWork.From(uow);
        npg.Connection.State.ShouldBe(System.Data.ConnectionState.Open);
        npg.Transaction.ShouldNotBeNull();
    }

    [Fact]
    public void Enlist_rejects_a_non_npgsql_connection()
    {
        var factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        DbConnection fake = new NotNpgsqlConnection();
        Should.Throw<InvalidOperationException>(() => factory.Enlist(fake, null!));
    }
}

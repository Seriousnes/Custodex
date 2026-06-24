using Shouldly;

namespace Custodex.Abstractions.Tests;

public class IndexStoreContractTests
{
    [Fact]
    public void ReverseIndexRow_carries_its_columns()
    {
        var row = new ReverseIndexRow("user:alice", "edit", "doc", "item-1", Conditioned: false);
        row.Subject.ShouldBe("user:alice");
        row.Permission.ShouldBe("edit");
        row.ObjectType.ShouldBe("doc");
        row.ObjectId.ShouldBe("item-1");
        row.Conditioned.ShouldBeFalse();
    }

    [Fact]
    public void IIndexStore_declares_the_maintenance_and_query_members()
    {
        var t = typeof(IIndexStore);
        t.GetMethod("UpsertAsync").ShouldNotBeNull();
        t.GetMethod("DeleteForObjectAsync").ShouldNotBeNull();
        t.GetMethod("DeleteRowsAsync").ShouldNotBeNull();
        t.GetMethod("QueryObjectsAsync").ShouldNotBeNull();
        t.GetMethod("ReadForObjectAsync").ShouldNotBeNull();
        t.GetMethod("ClearAsync").ShouldNotBeNull();
        t.GetMethod("IsBuiltAsync").ShouldNotBeNull();
        t.GetMethod("MarkBuiltAsync").ShouldNotBeNull();
    }
}

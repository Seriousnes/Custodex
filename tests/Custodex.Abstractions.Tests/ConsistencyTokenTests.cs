using Custodex.TestKit;

using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ConsistencyTokenTests
{
    [Fact]
    public void Create_then_decode_round_trips_every_component()
    {
        var tenant = TestWorld.New().Tenant;

        var token = ConsistencyToken.Create(tenant, epoch: 42, changeLogId: 1007);
        var parts = token.Decode();

        parts.Store.ShouldBe(tenant.Store);
        parts.Tenant.ShouldBe(tenant.Tenant);
        parts.Epoch.ShouldBe(42);
        parts.ChangeLogId.ShouldBe(1007);
    }

    [Fact]
    public void EpochFor_returns_the_encoded_epoch_for_the_matching_tenant()
    {
        var tenant = TestWorld.New().Tenant;

        var token = ConsistencyToken.Create(tenant, epoch: 9, changeLogId: 3);

        token.EpochFor(tenant).ShouldBe(9);
    }

    [Fact]
    public void EpochFor_throws_when_the_token_belongs_to_a_different_tenant()
    {
        var tenant = TestWorld.New().Tenant;
        var other = new TenantContext(tenant.Store, tenant.Tenant + "-other");

        var token = ConsistencyToken.Create(tenant, epoch: 5, changeLogId: 2);

        Should.Throw<InvalidConsistencyTokenException>(() => token.EpochFor(other));
    }

    [Fact]
    public void EpochFor_throws_when_the_token_belongs_to_a_different_store()
    {
        var tenant = TestWorld.New().Tenant;
        var other = new TenantContext(tenant.Store + "-other", tenant.Tenant);

        var token = ConsistencyToken.Create(tenant, epoch: 5, changeLogId: 2);

        Should.Throw<InvalidConsistencyTokenException>(() => token.EpochFor(other));
    }

    [Fact]
    public void Decode_throws_on_garbage_input()
    {
        var token = new ConsistencyToken("not-a-real-token");

        Should.Throw<InvalidConsistencyTokenException>(() => token.Decode());
    }

    [Fact]
    public void Decode_throws_on_an_empty_value()
    {
        var token = new ConsistencyToken("");

        Should.Throw<InvalidConsistencyTokenException>(() => token.Decode());
    }

    [Fact]
    public void Decode_throws_on_a_truncated_token()
    {
        var tenant = TestWorld.New().Tenant;
        var token = ConsistencyToken.Create(tenant, epoch: 7, changeLogId: 11);

        var truncated = new ConsistencyToken(token.Value[..(token.Value.Length / 2)]);

        Should.Throw<InvalidConsistencyTokenException>(() => truncated.Decode());
    }

    [Fact]
    public void Decode_throws_when_the_version_marker_is_unsupported()
    {
        var tenant = TestWorld.New().Tenant;
        var token = ConsistencyToken.Create(tenant, epoch: 7, changeLogId: 11);

        var bytes = Base64Url.Decode(token.Value);
        bytes[0] = 0x7F;
        var reversioned = new ConsistencyToken(Base64Url.Encode(bytes));

        Should.Throw<InvalidConsistencyTokenException>(() => reversioned.Decode());
    }

    [Fact]
    public void Decode_throws_on_a_valid_version_with_a_corrupt_length_prefix()
    {
        var corrupt = new ConsistencyToken(Base64Url.Encode([1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));

        Should.Throw<InvalidConsistencyTokenException>(() => corrupt.Decode());
    }

    [Fact]
    public void Encoded_value_begins_with_the_version_marker()
    {
        var tenant = TestWorld.New().Tenant;

        var token = ConsistencyToken.Create(tenant, epoch: 1, changeLogId: 1);

        Base64Url.Decode(token.Value)[0].ShouldBe((byte)1);
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
            return Convert.FromBase64String(padded);
        }
    }
}

using Relkit.Core.Caching;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Caching;

public class CacheValueCodecTests
{
    [Fact]
    public void Round_trips_true_and_false()
    {
        CacheValueCodec.Decode(CacheValueCodec.Encode(true)).ShouldBeTrue();
        CacheValueCodec.Decode(CacheValueCodec.Encode(false)).ShouldBeFalse();
    }

    [Fact]
    public void Encodes_to_a_single_byte()
    {
        CacheValueCodec.Encode(true).Length.ShouldBe(1);
    }
}

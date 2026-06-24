using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ContinuationCursorTests
{
    [Fact]
    public void Encode_then_decode_round_trips_the_last_id()
    {
        var world = TestWorld.New();
        var id = world.ObjectId();
        var token = ContinuationCursor.Encode(id);
        token.ShouldNotBe(id);                 // opaque, not the raw id
        ContinuationCursor.DecodeAfter(token).ShouldBe(id);
    }

    [Fact]
    public void Null_or_empty_token_decodes_to_null_meaning_start()
    {
        ContinuationCursor.DecodeAfter(null).ShouldBeNull();
        ContinuationCursor.DecodeAfter("").ShouldBeNull();
    }
}

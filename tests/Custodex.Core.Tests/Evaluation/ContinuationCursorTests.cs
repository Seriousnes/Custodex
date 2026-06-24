using Custodex.Core.Evaluation;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ContinuationCursorTests
{
    [Fact]
    public void Encode_then_decode_round_trips_the_last_id()
    {
        var token = ContinuationCursor.Encode("wallaby");
        token.ShouldNotBe("wallaby");                 // opaque, not the raw id
        ContinuationCursor.DecodeAfter(token).ShouldBe("wallaby");
    }

    [Fact]
    public void Null_or_empty_token_decodes_to_null_meaning_start()
    {
        ContinuationCursor.DecodeAfter(null).ShouldBeNull();
        ContinuationCursor.DecodeAfter("").ShouldBeNull();
    }
}

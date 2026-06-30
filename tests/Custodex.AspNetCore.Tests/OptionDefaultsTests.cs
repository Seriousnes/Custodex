using System.Security.Claims;

using Custodex.AspNetCore;

using Shouldly;

namespace Custodex.AspNetCore.Tests;

public class OptionDefaultsTests
{
    [Fact]
    public void Options_carry_the_documented_defaults()
    {
        var options = new CustodexAuthorizationOptions();

        options.PolicyPrefix.ShouldBe("custodex");
        options.PolicySeparator.ShouldBe(":");
        options.SubjectType.ShouldBe("user");
        options.SubjectIdClaim.ShouldBe(ClaimTypes.NameIdentifier);
        options.StoreClaim.ShouldBe("Custodex:store");
        options.TenantClaim.ShouldBe("Custodex:tenant");
        options.TenantHeader.ShouldBe("X-Custodex-Tenant");
        options.RootObject.ShouldBeNull();
        options.ThrowOnEvaluationError.ShouldBeFalse();
    }

    [Fact]
    public void Constants_match_the_service_wire_values()
    {
        CustodexClaimTypes.Store.ShouldBe("Custodex:store");
        CustodexClaimTypes.Tenant.ShouldBe("Custodex:tenant");
        CustodexHeaders.Tenant.ShouldBe("X-Custodex-Tenant");
    }
}

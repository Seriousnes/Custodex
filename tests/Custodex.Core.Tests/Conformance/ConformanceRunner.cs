using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Validation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Conformance;

public static class ConformanceRunner
{
    public static Task<CheckResult> RunAsync(ConformanceCase c, CancellationToken ct = default)
        => RunAsync(c, new NullConditionEvaluator(), ct);

    public static async Task<CheckResult> RunAsync(
        ConformanceCase c, IConditionEvaluator conditions, CancellationToken ct = default)
    {
        var validation = SchemaValidator.Validate(c.Schema);
        if (!validation.IsValid)
            throw new SchemaValidationException(validation.Errors);

        var world = TestWorld.New();
        var attributes = c.Attributes
            .Select(seed => (seed.Object, seed.Attributes))
            .ToList();
        var authorizer = await world.BuildAsync(c.Schema, conditions, c.Tuples, attributes);

        var request = new CheckRequest(
            world.Tenant, c.Object, c.Permission, c.Subject,
            new RequestContext(c.Now, c.Subject, c.Context));
        return await authorizer.CheckAsync(request, ct);
    }

    public static async Task AssertAsync(ConformanceCase c)
    {
        var result = await RunAsync(c);
        result.Allowed.ShouldBe(c.Expected,
            $"Conformance case '{c.Name}': expected Allowed={c.Expected} for " +
            $"{c.Subject} on {c.Object}#{c.Permission}, got {result.Allowed}.");
    }

    public static async Task AssertAsync(ConformanceCase c, IConditionEvaluator conditions)
    {
        var result = await RunAsync(c, conditions);
        result.Allowed.ShouldBe(c.Expected,
            $"Conformance case '{c.Name}': expected Allowed={c.Expected} for " +
            $"{c.Subject} on {c.Object}#{c.Permission}, got {result.Allowed}.");
    }
}

using Debloater.Core.Model;
using static Debloater.Core.Model.ActionState;

namespace Debloater.Core.Tests;

public class StatusAggregatorTests
{
    [Theory]
    [InlineData(TweakState.Applied, Desired)]
    [InlineData(TweakState.Applied, Desired, Desired, NotApplicable)]
    [InlineData(TweakState.NotApplied, NotDesired)]
    [InlineData(TweakState.NotApplied, NotDesired, NotApplicable)]
    [InlineData(TweakState.Partial, Desired, NotDesired)]
    [InlineData(TweakState.Partial, NotApplicable, Desired, NotDesired)]
    [InlineData(TweakState.NotApplicable, NotApplicable)]
    [InlineData(TweakState.NotApplicable, NotApplicable, NotApplicable)]
    [InlineData(TweakState.NotApplicable)]
    [InlineData(TweakState.Error, Error)]
    [InlineData(TweakState.Error, Desired, Error, NotDesired)]
    [InlineData(TweakState.Error, NotApplicable, Error)]
    public void Aggregate(TweakState expected, params ActionState[] states) =>
        Assert.Equal(expected, StatusAggregator.Aggregate(states));
}

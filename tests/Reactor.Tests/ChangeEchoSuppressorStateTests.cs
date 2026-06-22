using Microsoft.UI.Reactor.Core;
using Xunit;

namespace Microsoft.UI.Reactor.Tests;

/// <summary>
/// Issue #207 — the change-event trampolines were collapsed from two attached-DP
/// reads (ShouldSuppress + GetElementTag) to one (TryGetReactorState → state).
/// Suppression now flows through the <see cref="Reconciler.ReactorState"/>-based
/// overloads of <see cref="ChangeEchoSuppressor"/>. These headless tests pin the
/// exact decrement-and-suppress semantics those overloads must preserve — the
/// UIElement overloads simply read the DP once and delegate here, so verifying the
/// state overload verifies the behavior every trampoline now depends on.
///
/// (The FrameworkElement-facing <see cref="Reconciler.TryGetReactorState"/> read and
/// the end-to-end "callback fires / echo swallowed once" round-trip require a live
/// WinUI control and are covered by the ReactorStateCoalescing selftest fixture.)
/// </summary>
public class ChangeEchoSuppressorStateTests
{
    [Fact]
    public void ShouldSuppress_NoPendingToken_ReturnsFalse()
    {
        var state = new Reconciler.ReactorState();
        Assert.False(ChangeEchoSuppressor.ShouldSuppress(state));
    }

    [Fact]
    public void ShouldSuppress_CounterToken_SuppressesOnceThenDecrements()
    {
        var state = new Reconciler.ReactorState { EchoSuppressCount = 1 };

        Assert.True(ChangeEchoSuppressor.ShouldSuppress(state));   // consumes the token
        Assert.Equal(0, state.EchoSuppressCount);
        Assert.False(ChangeEchoSuppressor.ShouldSuppress(state));  // none left → falls through
    }

    [Fact]
    public void ShouldSuppress_MultipleTokens_DecrementOnePerCall()
    {
        var state = new Reconciler.ReactorState { EchoSuppressCount = 2 };

        Assert.True(ChangeEchoSuppressor.ShouldSuppress(state));
        Assert.Equal(1, state.EchoSuppressCount);
        Assert.True(ChangeEchoSuppressor.ShouldSuppress(state));
        Assert.Equal(0, state.EchoSuppressCount);
        Assert.False(ChangeEchoSuppressor.ShouldSuppress(state));
    }

    [Fact]
    public void ShouldSuppress_SetterScope_SuppressesWithoutConsumingCounter()
    {
        var state = new Reconciler.ReactorState { EchoSuppressScopeDepth = 1, EchoSuppressCount = 1 };

        // Scope wins first and is non-consuming: the counter token survives.
        Assert.True(ChangeEchoSuppressor.ShouldSuppress(state));
        Assert.Equal(1, state.EchoSuppressCount);
        Assert.Equal(1, state.EchoSuppressScopeDepth);
    }

    [Fact]
    public void ShouldSuppressEcho_NoArmNoCounter_ReturnsFalse()
    {
        var state = new Reconciler.ReactorState();
        Assert.False(ChangeEchoSuppressor.ShouldSuppressEcho(state, 5));
    }

    [Fact]
    public void ShouldSuppressEcho_PendingMatchHit_SuppressesAndConsumesArm()
    {
        var state = new Reconciler.ReactorState { PendingEchoMatch = v => Equals(v, 7) };

        Assert.True(ChangeEchoSuppressor.ShouldSuppressEcho(state, 7));
        Assert.Null(state.PendingEchoMatch);                       // arm consumed
        Assert.False(ChangeEchoSuppressor.ShouldSuppressEcho(state, 7)); // not re-armed
    }

    [Fact]
    public void ShouldSuppressEcho_PendingMatchMiss_FallsThroughAndClearsArm()
    {
        var state = new Reconciler.ReactorState { PendingEchoMatch = v => Equals(v, 7) };

        // A real user change superseded the pending write — readback differs.
        Assert.False(ChangeEchoSuppressor.ShouldSuppressEcho(state, 9));
        Assert.Null(state.PendingEchoMatch);                       // stale arm cleared
    }

    [Fact]
    public void ShouldSuppressEcho_CounterWins_ConsumesTokenAndClearsArm()
    {
        var state = new Reconciler.ReactorState
        {
            EchoSuppressCount = 1,
            PendingEchoMatch = _ => true,
        };

        Assert.True(ChangeEchoSuppressor.ShouldSuppressEcho(state, 123));
        Assert.Equal(0, state.EchoSuppressCount);                  // counter token consumed
        Assert.Null(state.PendingEchoMatch);                       // coincident arm cleared
    }

    [Fact]
    public void ShouldSuppressEcho_ScopeWins_DoesNotConsumeCounterButClearsArm()
    {
        var state = new Reconciler.ReactorState
        {
            EchoSuppressScopeDepth = 1,
            EchoSuppressCount = 1,
            PendingEchoMatch = _ => true,
        };

        Assert.True(ChangeEchoSuppressor.ShouldSuppressEcho(state, 123));
        Assert.Equal(1, state.EchoSuppressCount);                  // scope is non-consuming
        Assert.Null(state.PendingEchoMatch);                       // arm cleared
    }

    [Fact]
    public void StateOverload_MatchesUIElementOverloadContract_ForCounterPath()
    {
        // The UIElement overload returns false when no state exists (the no-tag
        // control case). The state overload models the has-state branch: a fresh
        // ReactorState with no tokens must not suppress, so a real change event
        // dispatches through to the user callback.
        var state = new Reconciler.ReactorState();
        Assert.False(ChangeEchoSuppressor.ShouldSuppress(state));
        Assert.False(ChangeEchoSuppressor.ShouldSuppressEcho(state, null));
    }
}

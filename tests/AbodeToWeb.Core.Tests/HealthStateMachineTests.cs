using AbodeToWeb.Core.Health;

namespace AbodeToWeb.Core.Tests;

public sealed class HealthStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly HealthThresholds Thresholds = new(2, 3);

    [Fact]
    public void Requires_configured_success_threshold_before_healthy()
    {
        var starting = HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, Now);

        var first = HealthStateMachine.Apply(starting, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(1));
        var second = HealthStateMachine.Apply(first, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(2));

        Assert.Equal(HealthState.Starting, first.State);
        Assert.Equal(HealthState.Healthy, second.State);
        Assert.Equal(2, second.ConsecutiveSuccesses);
    }

    [Fact]
    public void Healthy_becomes_degraded_then_unhealthy_at_failure_threshold()
    {
        var state = HealthyState();

        state = HealthStateMachine.Apply(state, HealthObservation.Failed("timeout 1"), Thresholds, Now.AddSeconds(3));
        Assert.Equal(HealthState.Degraded, state.State);
        state = HealthStateMachine.Apply(state, HealthObservation.Failed("timeout 2"), Thresholds, Now.AddSeconds(4));
        Assert.Equal(HealthState.Degraded, state.State);
        state = HealthStateMachine.Apply(state, HealthObservation.Failed("timeout 3"), Thresholds, Now.AddSeconds(5));

        Assert.Equal(HealthState.Unhealthy, state.State);
        Assert.Equal(3, state.ConsecutiveFailures);
        Assert.Equal("timeout 3", state.Detail);
    }

    [Fact]
    public void Unhealthy_enters_recovery_until_success_threshold()
    {
        var state = HealthyState();
        for (var index = 0; index < 3; index++)
        {
            state = HealthStateMachine.Apply(state, HealthObservation.Failed(), Thresholds, Now.AddSeconds(3 + index));
        }

        state = HealthStateMachine.Apply(state, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(6));
        Assert.Equal(HealthState.Recovering, state.State);
        state = HealthStateMachine.Apply(state, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(7));

        Assert.Equal(HealthState.Healthy, state.State);
    }

    [Fact]
    public void Stopped_and_disabled_states_ignore_late_observations()
    {
        var stopped = HealthStateMachine.MarkStopped(HealthyState(), Now.AddSeconds(3));
        var disabled = HealthStateMachine.MarkStopped(HealthyState(), Now.AddSeconds(3), disabled: true);

        Assert.Same(stopped, HealthStateMachine.Apply(stopped, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(4)));
        Assert.Same(disabled, HealthStateMachine.Apply(disabled, HealthObservation.Failed(), Thresholds, Now.AddSeconds(4)));
    }

    [Fact]
    public void Rejects_out_of_order_observations()
    {
        var state = HealthStateMachine.Apply(
            HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, Now),
            HealthObservation.Succeeded(),
            Thresholds,
            Now.AddSeconds(2));

        Assert.Throws<ArgumentException>(() => HealthStateMachine.Apply(state, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(1)));
    }

    private static HealthSnapshot HealthyState()
    {
        var state = HealthStateMachine.MarkStarted(HealthSnapshot.Unknown, Now);
        state = HealthStateMachine.Apply(state, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(1));
        return HealthStateMachine.Apply(state, HealthObservation.Succeeded(), Thresholds, Now.AddSeconds(2));
    }
}

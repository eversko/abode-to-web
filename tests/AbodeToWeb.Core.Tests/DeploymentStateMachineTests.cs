using AbodeToWeb.Core.Deployments;

namespace AbodeToWeb.Core.Tests;

public sealed class DeploymentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ExistingDeployment = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private const string Sha = "a73c81fa73c81fa73c81fa73c81fa73c81fa73c8";

    [Fact]
    public void Create_requires_full_hex_commit_identity()
    {
        Assert.Throws<ArgumentException>(() => DeploymentStateMachine.Create(Guid.NewGuid(), "sample-app", "a73c81f", Now));
        Assert.Throws<ArgumentException>(() => DeploymentStateMachine.Create(Guid.NewGuid(), "sample-app", new string('z', 40), Now));
    }

    [Fact]
    public void Stages_move_forward_and_cannot_regress()
    {
        var state = Create();
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.Fetching, Now.AddSeconds(1));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.Building, Now.AddSeconds(2));

        Assert.Throws<DeploymentTransitionException>(() =>
            DeploymentStateMachine.Transition(state, DeploymentStatus.PreparingWorktree, Now.AddSeconds(3)));
    }

    [Fact]
    public void Route_switch_is_forbidden_until_candidate_is_healthy()
    {
        var verifying = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));

        Assert.Throws<DeploymentTransitionException>(() =>
            DeploymentStateMachine.Transition(verifying, DeploymentStatus.SwitchingRoute, Now.AddSeconds(2)));
    }

    [Fact]
    public void Failed_candidate_keeps_previous_deployment_active()
    {
        var verifying = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));

        var failed = DeploymentStateMachine.RecordCandidateHealth(verifying, false, Now.AddSeconds(2), "Health check timed out.");

        Assert.Equal(DeploymentStatus.Failed, failed.Status);
        Assert.Equal(ExistingDeployment, failed.ActiveDeploymentId);
        Assert.True(failed.PreviousDeploymentPreserved);
        Assert.False(failed.RouteActivated);
        Assert.Equal("candidate.unhealthy", failed.FailureCode);
    }

    [Fact]
    public void Healthy_candidate_can_atomically_activate_route_and_complete()
    {
        var state = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));
        state = DeploymentStateMachine.RecordCandidateHealth(state, true, Now.AddSeconds(2));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.SwitchingRoute, Now.AddSeconds(3));
        state = DeploymentStateMachine.RecordRouteActivated(state, Now.AddSeconds(4));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.DrainingPrevious, Now.AddSeconds(5));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.Succeeded, Now.AddSeconds(6));

        Assert.Equal(DeploymentStatus.Succeeded, state.Status);
        Assert.Equal(state.Id, state.ActiveDeploymentId);
        Assert.True(state.CandidateHealthVerified);
        Assert.True(state.RouteActivated);
        Assert.True(state.PreviousDeploymentPreserved);
    }

    [Fact]
    public void Success_is_forbidden_without_route_activation()
    {
        var state = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));
        state = DeploymentStateMachine.RecordCandidateHealth(state, true, Now.AddSeconds(2));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.SwitchingRoute, Now.AddSeconds(3));

        Assert.Throws<DeploymentTransitionException>(() =>
            DeploymentStateMachine.Transition(state, DeploymentStatus.Succeeded, Now.AddSeconds(4)));
    }

    [Fact]
    public void Successful_deployment_can_be_marked_rolled_back_to_retained_release()
    {
        var state = SuccessfulState();

        var rolledBack = DeploymentStateMachine.MarkRolledBack(state, Now.AddSeconds(10), ExistingDeployment);

        Assert.Equal(DeploymentStatus.RolledBack, rolledBack.Status);
        Assert.Equal(ExistingDeployment, rolledBack.ActiveDeploymentId);
    }

    [Fact]
    public void Terminal_deployment_rejects_further_failure_transition()
    {
        var state = SuccessfulState();

        Assert.Throws<DeploymentTransitionException>(() =>
            DeploymentStateMachine.Fail(state, "late.error", "Late error.", Now.AddSeconds(10)));
    }

    [Fact]
    public void Failure_after_route_activation_restores_previous_active_identity()
    {
        var state = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));
        state = DeploymentStateMachine.RecordCandidateHealth(state, true, Now.AddSeconds(2));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.SwitchingRoute, Now.AddSeconds(3));
        state = DeploymentStateMachine.RecordRouteActivated(state, Now.AddSeconds(4));

        var failed = DeploymentStateMachine.Fail(state, "drain.failed", "Previous process did not drain.", Now.AddSeconds(5));

        Assert.Equal(ExistingDeployment, failed.ActiveDeploymentId);
        Assert.False(failed.RouteActivated);
        Assert.True(failed.PreviousDeploymentPreserved);
    }

    private static DeploymentState Create() =>
        DeploymentStateMachine.Create(Guid.Parse("20000000-0000-0000-0000-000000000002"), "sample-app", Sha, Now, ExistingDeployment);

    private static DeploymentState SuccessfulState()
    {
        var state = DeploymentStateMachine.Transition(Create(), DeploymentStatus.VerifyingCandidate, Now.AddSeconds(1));
        state = DeploymentStateMachine.RecordCandidateHealth(state, true, Now.AddSeconds(2));
        state = DeploymentStateMachine.Transition(state, DeploymentStatus.SwitchingRoute, Now.AddSeconds(3));
        state = DeploymentStateMachine.RecordRouteActivated(state, Now.AddSeconds(4));
        return DeploymentStateMachine.Transition(state, DeploymentStatus.Succeeded, Now.AddSeconds(5));
    }
}

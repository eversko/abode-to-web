namespace AbodeToWeb.Core.Health;

public enum HealthState
{
    Unknown,
    Starting,
    Healthy,
    Degraded,
    Unhealthy,
    Recovering,
    Stopped,
    Disabled
}

public enum HealthObservationKind
{
    Success,
    Failure
}

public sealed record HealthObservation(HealthObservationKind Kind, string? Detail = null)
{
    public static HealthObservation Succeeded(string? detail = null) => new(HealthObservationKind.Success, detail);

    public static HealthObservation Failed(string? detail = null) => new(HealthObservationKind.Failure, detail);
}

public sealed record HealthThresholds(int HealthyThreshold, int UnhealthyThreshold)
{
    public void Validate()
    {
        if (HealthyThreshold < 1 || UnhealthyThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthyThreshold), "Health thresholds must be at least one.");
        }
    }
}

public sealed record HealthSnapshot(
    HealthState State,
    int ConsecutiveSuccesses,
    int ConsecutiveFailures,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? StateChangedAt,
    string? Detail)
{
    public static HealthSnapshot Unknown { get; } = new(HealthState.Unknown, 0, 0, null, null, null);
}

public static class HealthStateMachine
{
    public static HealthSnapshot MarkStarted(HealthSnapshot current, DateTimeOffset now) =>
        Change(current, HealthState.Starting, now, "Waiting for the first successful health check.", resetCounters: true);

    public static HealthSnapshot MarkStopped(HealthSnapshot current, DateTimeOffset now, bool disabled = false) =>
        Change(current, disabled ? HealthState.Disabled : HealthState.Stopped, now, disabled ? "Project is disabled." : "Project is stopped.", resetCounters: true);

    public static HealthSnapshot Apply(
        HealthSnapshot current,
        HealthObservation observation,
        HealthThresholds thresholds,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.Validate();
        if (current.LastCheckedAt is not null && observedAt < current.LastCheckedAt)
        {
            throw new ArgumentException("Health observations must be applied in chronological order.", nameof(observedAt));
        }

        if (current.State is HealthState.Stopped or HealthState.Disabled)
        {
            return current;
        }

        HealthState nextState;
        int successes;
        int failures;
        if (observation.Kind == HealthObservationKind.Success)
        {
            successes = current.ConsecutiveSuccesses + 1;
            failures = 0;
            nextState = successes >= thresholds.HealthyThreshold
                ? HealthState.Healthy
                : current.State switch
                {
                    HealthState.Healthy => HealthState.Healthy,
                    HealthState.Degraded or HealthState.Unhealthy or HealthState.Recovering => HealthState.Recovering,
                    _ => HealthState.Starting
                };
        }
        else
        {
            successes = 0;
            failures = current.ConsecutiveFailures + 1;
            nextState = failures >= thresholds.UnhealthyThreshold
                ? HealthState.Unhealthy
                : current.State switch
                {
                    HealthState.Healthy or HealthState.Degraded => HealthState.Degraded,
                    HealthState.Unhealthy => HealthState.Unhealthy,
                    HealthState.Recovering => HealthState.Recovering,
                    _ => HealthState.Starting
                };
        }

        return new HealthSnapshot(
            nextState,
            successes,
            failures,
            observedAt,
            nextState == current.State ? current.StateChangedAt : observedAt,
            observation.Detail);
    }

    private static HealthSnapshot Change(
        HealthSnapshot current,
        HealthState state,
        DateTimeOffset now,
        string detail,
        bool resetCounters) =>
        new(
            state,
            resetCounters ? 0 : current.ConsecutiveSuccesses,
            resetCounters ? 0 : current.ConsecutiveFailures,
            current.LastCheckedAt,
            state == current.State ? current.StateChangedAt : now,
            detail);
}

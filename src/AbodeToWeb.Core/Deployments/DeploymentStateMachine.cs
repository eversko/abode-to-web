namespace AbodeToWeb.Core.Deployments;

public enum DeploymentStatus
{
    Queued,
    Fetching,
    ResolvingCommit,
    PreparingWorktree,
    Installing,
    Building,
    StartingCandidate,
    VerifyingCandidate,
    SwitchingRoute,
    DrainingPrevious,
    Succeeded,
    Failed,
    Cancelled,
    RolledBack
}

public sealed record DeploymentState(
    Guid Id,
    string ProjectId,
    string CommitSha,
    DeploymentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? PreviousDeploymentId,
    Guid? ActiveDeploymentId,
    bool CandidateHealthVerified,
    bool RouteActivated,
    bool PreviousDeploymentPreserved,
    string? FailureCode,
    string? Detail)
{
    public bool IsTerminal => Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled or DeploymentStatus.RolledBack;
}

public sealed class DeploymentTransitionException : InvalidOperationException
{
    public DeploymentTransitionException(DeploymentStatus current, DeploymentStatus requested, string reason)
        : base($"Cannot transition deployment from {current} to {requested}: {reason}")
    {
        Current = current;
        Requested = requested;
    }

    public DeploymentStatus Current { get; }

    public DeploymentStatus Requested { get; }
}

public static class DeploymentStateMachine
{
    private static readonly Dictionary<DeploymentStatus, int> ActiveStageOrder =
        new Dictionary<DeploymentStatus, int>
        {
            [DeploymentStatus.Queued] = 0,
            [DeploymentStatus.Fetching] = 1,
            [DeploymentStatus.ResolvingCommit] = 2,
            [DeploymentStatus.PreparingWorktree] = 3,
            [DeploymentStatus.Installing] = 4,
            [DeploymentStatus.Building] = 5,
            [DeploymentStatus.StartingCandidate] = 6,
            [DeploymentStatus.VerifyingCandidate] = 7,
            [DeploymentStatus.SwitchingRoute] = 8,
            [DeploymentStatus.DrainingPrevious] = 9
        };

    public static DeploymentState Create(
        Guid id,
        string projectId,
        string commitSha,
        DateTimeOffset now,
        Guid? activeDeploymentId = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Deployment id cannot be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        if (!IsCommitSha(commitSha))
        {
            throw new ArgumentException("Commit must be a 40 or 64 character hexadecimal SHA.", nameof(commitSha));
        }

        return new DeploymentState(
            id,
            projectId,
            commitSha.ToLowerInvariant(),
            DeploymentStatus.Queued,
            now,
            now,
            activeDeploymentId,
            activeDeploymentId,
            false,
            false,
            activeDeploymentId is not null,
            null,
            "Deployment queued.");
    }

    public static DeploymentState Transition(
        DeploymentState current,
        DeploymentStatus requested,
        DateTimeOffset now,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureChronological(current, now);

        if (requested == DeploymentStatus.Failed)
        {
            return Fail(current, "deployment.failed", detail ?? "Deployment failed.", now);
        }

        if (requested == DeploymentStatus.Cancelled)
        {
            if (current.IsTerminal)
            {
                throw Invalid(current, requested, "a terminal deployment cannot be cancelled");
            }

            if (current.RouteActivated)
            {
                throw Invalid(current, requested, "an activated route must be completed or explicitly rolled back");
            }

            return current with { Status = requested, UpdatedAt = now, Detail = detail ?? "Deployment cancelled." };
        }

        if (current.IsTerminal)
        {
            throw Invalid(current, requested, "the current state is terminal");
        }

        if (requested == DeploymentStatus.Succeeded)
        {
            if (!current.RouteActivated)
            {
                throw Invalid(current, requested, "the route has not been activated");
            }

            if (current.Status is not (DeploymentStatus.SwitchingRoute or DeploymentStatus.DrainingPrevious))
            {
                throw Invalid(current, requested, "deployment has not reached the route-switch stage");
            }

            return current with { Status = requested, UpdatedAt = now, Detail = detail ?? "Deployment succeeded." };
        }

        if (!ActiveStageOrder.TryGetValue(current.Status, out var currentOrder) ||
            !ActiveStageOrder.TryGetValue(requested, out var requestedOrder) ||
            requestedOrder <= currentOrder)
        {
            throw Invalid(current, requested, "stages can only move forward");
        }

        if (requestedOrder > ActiveStageOrder[DeploymentStatus.VerifyingCandidate] && !current.CandidateHealthVerified)
        {
            throw Invalid(current, requested, "candidate health has not been verified");
        }

        if (requested == DeploymentStatus.DrainingPrevious && !current.RouteActivated)
        {
            throw Invalid(current, requested, "the route has not been activated");
        }

        return current with { Status = requested, UpdatedAt = now, Detail = detail };
    }

    public static DeploymentState RecordCandidateHealth(
        DeploymentState current,
        bool healthy,
        DateTimeOffset now,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureChronological(current, now);
        if (current.Status != DeploymentStatus.VerifyingCandidate)
        {
            throw Invalid(current, current.Status, "candidate health can only be recorded during verification");
        }

        return healthy
            ? current with
            {
                CandidateHealthVerified = true,
                UpdatedAt = now,
                Detail = detail ?? "Candidate is healthy."
            }
            : Fail(current, "candidate.unhealthy", detail ?? "Candidate failed its health check.", now);
    }

    public static DeploymentState RecordRouteActivated(DeploymentState current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureChronological(current, now);
        if (current.Status != DeploymentStatus.SwitchingRoute || !current.CandidateHealthVerified)
        {
            throw Invalid(current, DeploymentStatus.SwitchingRoute, "a healthy candidate must be at the route-switch stage");
        }

        return current with
        {
            ActiveDeploymentId = current.Id,
            RouteActivated = true,
            PreviousDeploymentPreserved = current.PreviousDeploymentId is not null,
            UpdatedAt = now,
            Detail = "Gateway route activated."
        };
    }

    public static DeploymentState Fail(DeploymentState current, string failureCode, string safeDetail, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureChronological(current, now);
        if (current.IsTerminal)
        {
            throw Invalid(current, DeploymentStatus.Failed, "the current state is terminal");
        }

        if (string.IsNullOrWhiteSpace(failureCode) || string.IsNullOrWhiteSpace(safeDetail))
        {
            throw new ArgumentException("Failure code and safe detail are required.");
        }

        return current with
        {
            Status = DeploymentStatus.Failed,
            UpdatedAt = now,
            ActiveDeploymentId = current.PreviousDeploymentId ?? (current.RouteActivated ? current.ActiveDeploymentId : null),
            RouteActivated = current.PreviousDeploymentId is null && current.RouteActivated,
            PreviousDeploymentPreserved = current.PreviousDeploymentId is not null,
            FailureCode = failureCode,
            Detail = safeDetail
        };
    }

    public static DeploymentState MarkRolledBack(DeploymentState current, DateTimeOffset now, Guid restoredDeploymentId)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureChronological(current, now);
        if (current.Status != DeploymentStatus.Succeeded)
        {
            throw Invalid(current, DeploymentStatus.RolledBack, "only a successful deployment can be marked rolled back");
        }

        if (restoredDeploymentId == Guid.Empty || restoredDeploymentId == current.Id)
        {
            throw new ArgumentException("A different retained deployment must be restored.", nameof(restoredDeploymentId));
        }

        return current with
        {
            Status = DeploymentStatus.RolledBack,
            ActiveDeploymentId = restoredDeploymentId,
            UpdatedAt = now,
            Detail = $"Rolled back to deployment {restoredDeploymentId}."
        };
    }

    private static bool IsCommitSha(string value) =>
        value is not null && value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static void EnsureChronological(DeploymentState current, DateTimeOffset now)
    {
        if (now < current.UpdatedAt)
        {
            throw new ArgumentException("Deployment events must be applied in chronological order.", nameof(now));
        }
    }

    private static DeploymentTransitionException Invalid(DeploymentState current, DeploymentStatus requested, string reason) =>
        new(current.Status, requested, reason);
}

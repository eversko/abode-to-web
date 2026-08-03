using AbodeToWeb.Core.Configuration;

namespace AbodeToWeb.Core.Supervision;

public sealed record RestartPolicySettings(
    RestartPolicy Policy,
    int MaxRestarts,
    TimeSpan Window,
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    TimeSpan StableRuntime)
{
    public static RestartPolicySettings From(RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RestartPolicySettings(
            options.RestartPolicy,
            options.MaxRestarts,
            TimeSpan.FromSeconds(options.RestartWindowSeconds),
            TimeSpan.FromSeconds(options.InitialBackoffSeconds),
            TimeSpan.FromSeconds(options.MaxBackoffSeconds),
            TimeSpan.FromSeconds(options.StableRuntimeSeconds));
    }
}

public sealed record RestartHistory(IReadOnlyList<DateTimeOffset> Attempts)
{
    public static RestartHistory Empty { get; } = new([]);
}

public sealed record RestartDecision(
    bool ShouldRestart,
    bool CircuitOpen,
    TimeSpan Delay,
    DateTimeOffset? RetryAfter,
    int AttemptsInWindow,
    string Reason,
    RestartHistory History);

public static class RestartBackoff
{
    public static TimeSpan CalculateDelay(int attemptNumber, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, initialDelay);

        var ticks = initialDelay.Ticks;
        for (var index = 1; index < attemptNumber && ticks < maximumDelay.Ticks; index++)
        {
            ticks = ticks > maximumDelay.Ticks / 2 ? maximumDelay.Ticks : ticks * 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }
}

public static class RestartPolicyEvaluator
{
    public static RestartDecision EvaluateExit(
        RestartPolicySettings settings,
        RestartHistory history,
        DateTimeOffset startedAt,
        DateTimeOffset exitedAt,
        int exitCode,
        bool stopRequested)
    {
        ValidateSettings(settings);
        ArgumentNullException.ThrowIfNull(history);
        if (exitedAt < startedAt)
        {
            throw new ArgumentException("Exit time cannot precede start time.", nameof(exitedAt));
        }

        var cutoff = exitedAt - settings.Window;
        var attempts = history.Attempts
            .Where(attempt => attempt > cutoff && attempt <= exitedAt)
            .Order()
            .ToList();

        if (exitedAt - startedAt >= settings.StableRuntime)
        {
            attempts.Clear();
        }

        if (stopRequested)
        {
            return NoRestart("Stop was requested.", attempts);
        }

        if (settings.Policy == RestartPolicy.Never)
        {
            return NoRestart("Restart policy is never.", attempts);
        }

        if (settings.Policy == RestartPolicy.OnFailure && exitCode == 0)
        {
            return NoRestart("Process exited successfully.", attempts);
        }

        if (attempts.Count >= settings.MaxRestarts)
        {
            var retryAfter = attempts.Count == 0 ? exitedAt + settings.Window : attempts[0] + settings.Window;
            return new RestartDecision(
                false,
                true,
                TimeSpan.Zero,
                retryAfter,
                attempts.Count,
                $"Circuit breaker opened after {attempts.Count} restarts in {settings.Window.TotalSeconds:0} seconds.",
                new RestartHistory(attempts));
        }

        var attemptNumber = attempts.Count + 1;
        var delay = RestartBackoff.CalculateDelay(attemptNumber, settings.InitialDelay, settings.MaximumDelay);
        attempts.Add(exitedAt);
        return new RestartDecision(
            true,
            false,
            delay,
            null,
            attempts.Count,
            $"Restart {attemptNumber} scheduled after process exit code {exitCode}.",
            new RestartHistory(attempts));
    }

    private static RestartDecision NoRestart(string reason, List<DateTimeOffset> attempts) =>
        new(false, false, TimeSpan.Zero, null, attempts.Count, reason, new RestartHistory(attempts.ToArray()));

    private static void ValidateSettings(RestartPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaxRestarts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Max restarts cannot be negative.");
        }

        if (settings.Window <= TimeSpan.Zero || settings.InitialDelay < TimeSpan.Zero ||
            settings.MaximumDelay < settings.InitialDelay || settings.StableRuntime < TimeSpan.Zero)
        {
            throw new ArgumentException("Restart timing settings are invalid.", nameof(settings));
        }
    }
}

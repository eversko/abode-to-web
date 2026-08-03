using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Supervision;

namespace AbodeToWeb.Core.Tests;

public sealed class RestartPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(20, 5)]
    public void Backoff_is_exponential_and_capped(int attempt, int expectedSeconds)
    {
        var delay = RestartBackoff.CalculateDelay(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void On_failure_restarts_nonzero_exit_but_not_success()
    {
        var settings = Settings(RestartPolicy.OnFailure);

        var failed = RestartPolicyEvaluator.EvaluateExit(settings, RestartHistory.Empty, Now, Now.AddSeconds(1), 1, false);
        var succeeded = RestartPolicyEvaluator.EvaluateExit(settings, RestartHistory.Empty, Now, Now.AddSeconds(1), 0, false);

        Assert.True(failed.ShouldRestart);
        Assert.False(succeeded.ShouldRestart);
    }

    [Fact]
    public void Always_restarts_clean_exit_and_never_policy_does_not()
    {
        var always = RestartPolicyEvaluator.EvaluateExit(Settings(RestartPolicy.Always), RestartHistory.Empty, Now, Now.AddSeconds(1), 0, false);
        var never = RestartPolicyEvaluator.EvaluateExit(Settings(RestartPolicy.Never), RestartHistory.Empty, Now, Now.AddSeconds(1), 1, false);

        Assert.True(always.ShouldRestart);
        Assert.False(never.ShouldRestart);
    }

    [Fact]
    public void Requested_stop_overrides_always_policy()
    {
        var decision = RestartPolicyEvaluator.EvaluateExit(Settings(RestartPolicy.Always), RestartHistory.Empty, Now, Now.AddSeconds(1), 1, true);

        Assert.False(decision.ShouldRestart);
        Assert.Contains("requested", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Circuit_breaker_opens_after_windowed_limit()
    {
        var history = new RestartHistory([Now.AddSeconds(-30), Now.AddSeconds(-20), Now.AddSeconds(-10)]);
        var settings = Settings(RestartPolicy.Always) with { MaxRestarts = 3 };

        var decision = RestartPolicyEvaluator.EvaluateExit(settings, history, Now.AddSeconds(-1), Now, 1, false);

        Assert.False(decision.ShouldRestart);
        Assert.True(decision.CircuitOpen);
        Assert.Equal(3, decision.AttemptsInWindow);
        Assert.Equal(Now.AddSeconds(30), decision.RetryAfter);
    }

    [Fact]
    public void Attempts_outside_window_are_pruned()
    {
        var history = new RestartHistory([Now.AddMinutes(-10), Now.AddSeconds(-20)]);

        var decision = RestartPolicyEvaluator.EvaluateExit(Settings(RestartPolicy.Always), history, Now.AddSeconds(-1), Now, 1, false);

        Assert.True(decision.ShouldRestart);
        Assert.Equal(2, decision.AttemptsInWindow);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
    }

    [Fact]
    public void Stable_runtime_resets_backoff_history()
    {
        var history = new RestartHistory([Now.AddSeconds(-30), Now.AddSeconds(-20)]);

        var decision = RestartPolicyEvaluator.EvaluateExit(Settings(RestartPolicy.Always), history, Now.AddMinutes(-6), Now, 1, false);

        Assert.True(decision.ShouldRestart);
        Assert.Single(decision.History.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.Delay);
    }

    [Fact]
    public void Rejects_exit_before_start()
    {
        Assert.Throws<ArgumentException>(() => RestartPolicyEvaluator.EvaluateExit(
            Settings(RestartPolicy.Always), RestartHistory.Empty, Now, Now.AddSeconds(-1), 1, false));
    }

    private static RestartPolicySettings Settings(RestartPolicy policy) => new(
        policy,
        MaxRestarts: 5,
        Window: TimeSpan.FromMinutes(1),
        InitialDelay: TimeSpan.FromSeconds(1),
        MaximumDelay: TimeSpan.FromSeconds(30),
        StableRuntime: TimeSpan.FromMinutes(5));
}

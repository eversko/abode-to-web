using AbodeToWeb.Infrastructure.Deployments;
using AbodeToWeb.Infrastructure.Operations;
using AbodeToWeb.Server.Services;
using Microsoft.AspNetCore.Diagnostics;

namespace AbodeToWeb.Server.Api;

public sealed partial class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = exception switch
        {
            KeyNotFoundException => (404, "not_found", "The requested resource was not found."),
            OperationConflictException => (409, "operation_conflict", exception.Message),
            OperationQueueFullException queueFull => (503, queueFull.Code, queueFull.SafeMessage),
            DeploymentException deployment => (409, deployment.Code, deployment.SafeMessage),
            OnboardingException onboarding => (400, onboarding.Code, onboarding.Message),
            ProjectConfigurationException configuration when configuration.Code == "project.not_found" =>
                (404, configuration.Code, configuration.Message),
            ProjectConfigurationException configuration => (400, configuration.Code, configuration.Message),
            ArgumentException => (400, "invalid_request", "The request contains an invalid value."),
            BadHttpRequestException => (400, "invalid_request", "The request could not be read."),
            _ => (500, "internal_error", "AbodeToWeb could not complete the request. Check the system log.")
        };
        if (status >= 500) LogUnhandled(logger, exception);
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            type = "about:blank",
            title = status >= 500 ? "Internal error" : "Request failed",
            status,
            detail,
            code,
            traceId = httpContext.TraceIdentifier
        }, cancellationToken);
        return true;
    }

    [LoggerMessage(401, LogLevel.Error, "Unhandled API error")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}

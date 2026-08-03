using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Infrastructure.Configuration;

namespace AbodeToWeb.Server.Services;

public sealed class LocalProjectConfigurationService(
    ManifestProvider manifest)
{
    public Task SaveAsync(
        string projectId,
        SaveLocalProjectRequest request,
        CancellationToken cancellationToken = default) => manifest.UpdateAtomicAsync(current =>
    {
        var project = new LocalProjectManifest
        {
            Id = projectId,
            Name = RequiredOrEmpty(request.Name),
            Description = NullIfWhiteSpace(request.Description),
            RepositoryPath = RequiredOrEmpty(request.RepositoryPath),
            PlannedHostname = NullIfWhiteSpace(request.PlannedHostname),
            Notes = NullIfWhiteSpace(request.Notes)
        };
        var projects = current.LocalProjects.Where(candidate =>
                !candidate.Id.Equals(projectId, StringComparison.OrdinalIgnoreCase))
            .Append(project)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var next = current with { LocalProjects = projects };
        var validation = manifest.ValidatePersisted(next);
        if (!validation.IsValid)
        {
            var issue = validation.Errors.First();
            throw new ProjectConfigurationException(issue.Code, issue.Message);
        }
        return next;
    }, cancellationToken);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequiredOrEmpty(string? value) => value?.Trim() ?? string.Empty;
}

using AbodeToWeb.Core.Configuration;
using AbodeToWeb.Core.Contracts;
using AbodeToWeb.Infrastructure.Configuration;

namespace AbodeToWeb.Server.Services;

public sealed class ExternalSiteConfigurationService(
    ManifestProvider manifest)
{
    public Task SaveAsync(
        string siteId,
        SaveExternalSiteRequest request,
        CancellationToken cancellationToken = default) => manifest.UpdateAtomicAsync(current =>
    {
        var site = new ExternalSiteManifest
        {
            Id = siteId,
            Name = RequiredOrEmpty(request.Name),
            Description = NullIfWhiteSpace(request.Description),
            Url = RequiredOrEmpty(request.Url),
            Provider = RequiredOrEmpty(request.Provider),
            ManagementUrl = NullIfWhiteSpace(request.ManagementUrl),
            RepositoryPath = NullIfWhiteSpace(request.RepositoryPath)
        };
        var sites = current.ExternalSites.Where(candidate =>
                !candidate.Id.Equals(siteId, StringComparison.OrdinalIgnoreCase))
            .Append(site)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var next = current with { ExternalSites = sites };
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

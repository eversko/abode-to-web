namespace AbodeToWeb.Core.Validation;

public enum ValidationSeverity
{
    Error,
    Warning
}

public sealed record ValidationIssue(
    string Code,
    string Path,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error);

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool IsValid => _issues.All(issue => issue.Severity != ValidationSeverity.Error);

    public IEnumerable<ValidationIssue> Errors =>
        _issues.Where(issue => issue.Severity == ValidationSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings =>
        _issues.Where(issue => issue.Severity == ValidationSeverity.Warning);

    public void Add(string code, string path, string message) =>
        _issues.Add(new ValidationIssue(code, path, message));

    public void AddWarning(string code, string path, string message) =>
        _issues.Add(new ValidationIssue(code, path, message, ValidationSeverity.Warning));

    public void AddRange(IEnumerable<ValidationIssue> issues) => _issues.AddRange(issues);
}

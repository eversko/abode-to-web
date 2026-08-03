namespace AbodeToWeb.Core.Security;

public enum PathExpectation
{
    Any,
    File,
    Directory
}

public sealed record PathValidationRequest(
    bool MustExist = true,
    PathExpectation Expectation = PathExpectation.Any);

public sealed record ValidatedProjectPath(
    string Input,
    string LexicalPath,
    string CanonicalPath,
    string AllowedRoot,
    bool Exists,
    bool RequiresRevalidation);

public sealed record ProjectPathValidationResult(
    ValidatedProjectPath? Path,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsValid => Path is not null;

    public static ProjectPathValidationResult Success(ValidatedProjectPath path) => new(path, null, null);

    public static ProjectPathValidationResult Failure(string code, string message) => new(null, code, message);
}

public interface IPathInspector
{
    bool Exists(string path);

    FileAttributes GetAttributes(string path);

    string? ResolveLinkTarget(string path);
}

public sealed class PhysicalPathInspector : IPathInspector
{
    public bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public string? ResolveLinkTarget(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
    }
}

public interface IProjectPathPolicy
{
    ProjectPathValidationResult Validate(string path, PathValidationRequest? request = null);
}

public sealed class ProjectPathPolicy : IProjectPathPolicy
{
    private const int MaxReparseHops = 32;
    private readonly IPathInspector _inspector;
    private readonly AllowedRoot[] _roots;
    private readonly StringComparison _comparison;

    public ProjectPathPolicy(IEnumerable<string> allowedRoots, IPathInspector? inspector = null)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _inspector = inspector ?? new PhysicalPathInspector();
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var roots = new List<AllowedRoot>();
        foreach (var root in allowedRoots)
        {
            if (!TryNormalizeInput(root, out var lexical, out var error))
            {
                throw new ArgumentException($"Invalid allowed project root '{root}': {error}", nameof(allowedRoots));
            }

            var canonical = ResolveExistingComponents(lexical!, out _);
            if (_inspector.Exists(canonical) && !_inspector.GetAttributes(canonical).HasFlag(FileAttributes.Directory))
            {
                throw new ArgumentException($"Allowed project root '{root}' is not a directory.", nameof(allowedRoots));
            }

            roots.Add(new AllowedRoot(TrimEndingSeparator(lexical!), TrimEndingSeparator(canonical)));
        }

        _roots = roots
            .DistinctBy(root => root.Lexical, PathComparer)
            .OrderByDescending(root => root.Lexical.Length)
            .ToArray();

        if (_roots.Length == 0)
        {
            throw new ArgumentException("At least one allowed project root is required.", nameof(allowedRoots));
        }
    }

    public ProjectPathValidationResult Validate(string path, PathValidationRequest? request = null)
    {
        request ??= new PathValidationRequest();
        if (!TryNormalizeInput(path, out var lexical, out var inputError))
        {
            return ProjectPathValidationResult.Failure("path.invalid", inputError!);
        }

        var lexicalRoot = _roots.FirstOrDefault(root => IsWithin(root.Lexical, lexical!));
        if (lexicalRoot is null)
        {
            return ProjectPathValidationResult.Failure(
                "path.outside_allowed_roots",
                "Path is outside the configured project roots.");
        }

        string canonical;
        bool containsMissingComponent;
        try
        {
            canonical = ResolveExistingComponents(lexical!, out containsMissingComponent);
        }
        catch (InvalidDataException exception)
        {
            return ProjectPathValidationResult.Failure("path.reparse_invalid", exception.Message);
        }
        catch (IOException exception)
        {
            return ProjectPathValidationResult.Failure("path.unavailable", $"Path could not be inspected safely: {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return ProjectPathValidationResult.Failure("path.access_denied", "Path could not be inspected due to access restrictions.");
        }

        if (!IsWithin(lexicalRoot.Canonical, canonical))
        {
            return ProjectPathValidationResult.Failure(
                "path.reparse_escape",
                "Path traverses a junction or symbolic link outside the configured project root.");
        }

        var exists = _inspector.Exists(canonical);
        if (request.MustExist && !exists)
        {
            return ProjectPathValidationResult.Failure("path.not_found", "Path does not exist.");
        }

        if (exists && request.Expectation != PathExpectation.Any)
        {
            var isDirectory = _inspector.GetAttributes(canonical).HasFlag(FileAttributes.Directory);
            if (request.Expectation == PathExpectation.Directory && !isDirectory)
            {
                return ProjectPathValidationResult.Failure("path.not_directory", "Path is not a directory.");
            }

            if (request.Expectation == PathExpectation.File && isDirectory)
            {
                return ProjectPathValidationResult.Failure("path.not_file", "Path is not a file.");
            }
        }

        return ProjectPathValidationResult.Success(new ValidatedProjectPath(
            path,
            lexical!,
            canonical,
            lexicalRoot.Canonical,
            exists,
            containsMissingComponent));
    }

    private static IEqualityComparer<string> PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private string ResolveExistingComponents(string fullPath, out bool containsMissingComponent)
    {
        containsMissingComponent = false;
        var pending = new Queue<string>();
        EnqueueComponents(fullPath, pending, out var current);
        var visitedLinks = new HashSet<string>(PathComparer);
        var hopCount = 0;

        while (pending.TryDequeue(out var component))
        {
            current = Path.GetFullPath(Path.Combine(current, component));
            if (!_inspector.Exists(current))
            {
                containsMissingComponent = true;
                continue;
            }

            var attributes = _inspector.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (++hopCount > MaxReparseHops || !visitedLinks.Add(current))
            {
                throw new InvalidDataException("Path contains a cyclic or excessively nested reparse point.");
            }

            var target = _inspector.ResolveLinkTarget(current);
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidDataException("A reparse point target could not be resolved.");
            }

            var resolvedTarget = Path.IsPathFullyQualified(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, target));
            if (IsUnsafeNamespace(resolvedTarget))
            {
                throw new InvalidDataException("A reparse point resolves to an unsafe device path.");
            }

            var remainder = pending.ToArray();
            pending.Clear();
            EnqueueComponents(resolvedTarget, pending, out current);
            foreach (var item in remainder)
            {
                pending.Enqueue(item);
            }
        }

        return TrimEndingSeparator(Path.GetFullPath(current));
    }

    private static void EnqueueComponents(string fullPath, Queue<string> destination, out string root)
    {
        root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("Path has no filesystem root.");
        var remainder = fullPath[root.Length..];
        foreach (var component in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            destination.Enqueue(component);
        }
    }

    private bool IsWithin(string root, string candidate)
    {
        if (candidate.Equals(root, _comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, _comparison);
    }

    private static bool TryNormalizeInput(string? path, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path cannot be empty.";
            return false;
        }

        if (path.Contains('\0', StringComparison.Ordinal) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "Path contains an invalid character.";
            return false;
        }

        if (HasParentTraversal(path))
        {
            error = "Parent-directory traversal ('..') is not allowed.";
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            error = "Path must be fully qualified.";
            return false;
        }

        if (IsUnsafeNamespace(path))
        {
            error = "Device and extended-length path namespaces are not allowed.";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
            if (path.AsSpan(rootLength).Contains(':'))
            {
                error = "Alternate data stream paths are not allowed.";
                return false;
            }
        }

        try
        {
            normalized = TrimEndingSeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Path is not a valid fully qualified filesystem path.";
            return false;
        }
    }

    private static bool HasParentTraversal(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(component => component == "..");

    private static bool IsUnsafeNamespace(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        path.StartsWith(@"\??\", StringComparison.Ordinal);

    private static string TrimEndingSeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null && path.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return path;
        }

        return Path.TrimEndingDirectorySeparator(path);
    }

    private sealed record AllowedRoot(string Lexical, string Canonical);
}

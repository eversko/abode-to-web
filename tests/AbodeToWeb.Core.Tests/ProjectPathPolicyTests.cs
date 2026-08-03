using AbodeToWeb.Core.Security;

namespace AbodeToWeb.Core.Tests;

public sealed class ProjectPathPolicyTests
{
    [Fact]
    public void Accepts_child_path_and_returns_canonical_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "AbodeToWebPathTests", "allowed");
        var path = Path.Combine(root, "sample-app");
        var policy = new ProjectPathPolicy([root]);

        var result = policy.Validate(path, new PathValidationRequest(MustExist: false, PathExpectation.Directory));

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(Path.GetFullPath(path), result.Path!.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        Assert.True(result.Path.RequiresRevalidation);
    }

    [Fact]
    public void Rejects_relative_and_parent_traversal_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "AbodeToWebPathTests", "allowed");
        var policy = new ProjectPathPolicy([root]);

        var relative = policy.Validate("sample-app", new PathValidationRequest(false));
        var traversal = policy.Validate(Path.Combine(root, "..", "elsewhere"), new PathValidationRequest(false));

        Assert.Equal("path.invalid", relative.ErrorCode);
        Assert.Equal("path.invalid", traversal.ErrorCode);
        Assert.Contains("traversal", traversal.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_sibling_with_matching_text_prefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "AbodeToWebPathTests");
        var policy = new ProjectPathPolicy([Path.Combine(parent, "allowed")]);

        var result = policy.Validate(Path.Combine(parent, "allowed-evil", "app"), new PathValidationRequest(false));

        Assert.Equal("path.outside_allowed_roots", result.ErrorCode);
    }

    [Fact]
    public void Rejects_missing_path_when_existence_is_required()
    {
        var root = Path.Combine(Path.GetTempPath(), "AbodeToWebPathTests", Guid.NewGuid().ToString("N"));
        var policy = new ProjectPathPolicy([root]);

        var result = policy.Validate(Path.Combine(root, "missing"));

        Assert.Equal("path.not_found", result.ErrorCode);
    }

    [Fact]
    public void Rejects_junction_or_symlink_that_escapes_allowed_root()
    {
        var inspector = new FakePathInspector();
        var root = Path.GetFullPath(@"C:\allowed");
        var link = Path.Combine(root, "repo");
        inspector.AddDirectory(root);
        inspector.AddLink(link, Path.GetFullPath(@"C:\outside"));
        var policy = new ProjectPathPolicy([root], inspector);

        var result = policy.Validate(Path.Combine(link, "child"), new PathValidationRequest(false));

        Assert.Equal("path.reparse_escape", result.ErrorCode);
    }

    [Fact]
    public void Accepts_junction_or_symlink_whose_final_target_remains_inside_root()
    {
        var inspector = new FakePathInspector();
        var root = Path.GetFullPath(@"C:\allowed");
        var link = Path.Combine(root, "alias");
        var target = Path.Combine(root, "real");
        inspector.AddDirectory(root);
        inspector.AddDirectory(target);
        inspector.AddLink(link, target);
        var policy = new ProjectPathPolicy([root], inspector);

        var result = policy.Validate(link, new PathValidationRequest(true, PathExpectation.Directory));

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal(target, result.Path!.CanonicalPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_cyclic_reparse_points()
    {
        var inspector = new FakePathInspector();
        var root = Path.GetFullPath(@"C:\allowed");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        inspector.AddDirectory(root);
        inspector.AddLink(first, second);
        inspector.AddLink(second, first);
        var policy = new ProjectPathPolicy([root], inspector);

        var result = policy.Validate(first, new PathValidationRequest(false));

        Assert.Equal("path.reparse_invalid", result.ErrorCode);
    }

    private sealed class FakePathInspector : IPathInspector
    {
        private readonly Dictionary<string, FileAttributes> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _targets = new(StringComparer.OrdinalIgnoreCase);

        internal void AddDirectory(string path) => _entries[Normalize(path)] = FileAttributes.Directory;

        internal void AddLink(string path, string target)
        {
            _entries[Normalize(path)] = FileAttributes.Directory | FileAttributes.ReparsePoint;
            _targets[Normalize(path)] = Normalize(target);
        }

        public bool Exists(string path) => _entries.ContainsKey(Normalize(path));

        public FileAttributes GetAttributes(string path) => _entries[Normalize(path)];

        public string? ResolveLinkTarget(string path) => _targets.GetValueOrDefault(Normalize(path));

        private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

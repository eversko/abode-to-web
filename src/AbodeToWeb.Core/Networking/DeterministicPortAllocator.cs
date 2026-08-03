using System.Text;

namespace AbodeToWeb.Core.Networking;

public interface IPortAllocator
{
    int Allocate(string projectId, IReadOnlySet<int> unavailablePorts);
}

public sealed class PortRangeExhaustedException : InvalidOperationException
{
    public PortRangeExhaustedException(int firstPort, int lastPort)
        : base($"No available port remains in range {firstPort}-{lastPort}.")
    {
        FirstPort = firstPort;
        LastPort = lastPort;
    }

    public int FirstPort { get; }

    public int LastPort { get; }
}

public sealed class DeterministicPortAllocator : IPortAllocator
{
    private readonly HashSet<int> _reserved;

    public DeterministicPortAllocator(int firstPort = 5100, int lastPort = 5999, IEnumerable<int>? reservedPorts = null)
    {
        if (firstPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPort));
        }

        if (lastPort is < 1 or > 65_535 || lastPort < firstPort)
        {
            throw new ArgumentOutOfRangeException(nameof(lastPort));
        }

        FirstPort = firstPort;
        LastPort = lastPort;
        _reserved = reservedPorts?.ToHashSet() ?? [];
    }

    public int FirstPort { get; }

    public int LastPort { get; }

    public int Allocate(string projectId, IReadOnlySet<int> unavailablePorts)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        ArgumentNullException.ThrowIfNull(unavailablePorts);
        var count = LastPort - FirstPort + 1;
        var start = (int)(StableHash(projectId) % (uint)count);
        for (var offset = 0; offset < count; offset++)
        {
            var candidate = FirstPort + ((start + offset) % count);
            if (!_reserved.Contains(candidate) && !unavailablePorts.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new PortRangeExhaustedException(FirstPort, LastPort);
    }

    public IReadOnlyDictionary<string, int> AllocateMany(IEnumerable<string> projectIds, IReadOnlySet<int> unavailablePorts)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(unavailablePorts);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var occupied = unavailablePorts.ToHashSet();
        foreach (var projectId in projectIds.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (result.ContainsKey(projectId))
            {
                throw new ArgumentException($"Duplicate project id '{projectId}'.", nameof(projectIds));
            }

            var port = Allocate(projectId, occupied);
            result.Add(projectId, port);
            occupied.Add(port);
        }

        return result;
    }

    private static uint StableHash(string value)
    {
        const uint offsetBasis = 2_166_136_261;
        const uint prime = 16_777_619;
        var hash = offsetBasis;
        foreach (var item in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash ^= item;
            hash *= prime;
        }

        return hash;
    }
}

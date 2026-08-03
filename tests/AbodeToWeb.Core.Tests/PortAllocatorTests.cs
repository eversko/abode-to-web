using AbodeToWeb.Core.Networking;

namespace AbodeToWeb.Core.Tests;

public sealed class PortAllocatorTests
{
    [Fact]
    public void Allocation_is_stable_for_same_project_and_inputs()
    {
        var allocator = new DeterministicPortAllocator(5100, 5199);

        var first = allocator.Allocate("sample-app", new HashSet<int>());
        var second = allocator.Allocate("sample-app", new HashSet<int>());

        Assert.Equal(first, second);
        Assert.InRange(first, 5100, 5199);
    }

    [Fact]
    public void Allocation_probes_forward_and_wraps_around_collisions()
    {
        var allocator = new DeterministicPortAllocator(5100, 5102);
        var preferred = allocator.Allocate("sample-app", new HashSet<int>());
        var next = allocator.Allocate("sample-app", new HashSet<int> { preferred });

        Assert.NotEqual(preferred, next);
        Assert.InRange(next, 5100, 5102);
    }

    [Fact]
    public void Reserved_ports_are_never_selected()
    {
        var allocator = new DeterministicPortAllocator(5100, 5101, [5100]);

        Assert.Equal(5101, allocator.Allocate("sample-app", new HashSet<int>()));
    }

    [Fact]
    public void Allocate_many_is_independent_of_input_order_and_produces_unique_ports()
    {
        var allocator = new DeterministicPortAllocator(5100, 5110);

        var forward = allocator.AllocateMany(["sample-app", "docs-site", "api-service"], new HashSet<int>());
        var reverse = allocator.AllocateMany(["api-service", "docs-site", "sample-app"], new HashSet<int>());

        Assert.Equal(forward.OrderBy(pair => pair.Key), reverse.OrderBy(pair => pair.Key));
        Assert.Equal(3, forward.Values.Distinct().Count());
    }

    [Fact]
    public void Exhaustion_throws_typed_exception_with_range()
    {
        var allocator = new DeterministicPortAllocator(5100, 5101);

        var exception = Assert.Throws<PortRangeExhaustedException>(() =>
            allocator.Allocate("sample-app", new HashSet<int> { 5100, 5101 }));

        Assert.Equal(5100, exception.FirstPort);
        Assert.Equal(5101, exception.LastPort);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(5100, 5099)]
    [InlineData(5100, 70000)]
    public void Constructor_rejects_invalid_range(int first, int last)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeterministicPortAllocator(first, last));
    }
}

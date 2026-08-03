using AbodeToWeb.Core.Routing;

namespace AbodeToWeb.Core.Tests;

public sealed class HostnameTests
{
    [Theory]
    [InlineData("Sample-App.Example.COM", "sample-app.example.com")]
    [InlineData("sample-app.example.com.", "sample-app.example.com")]
    [InlineData("münich.example.com", "xn--mnich-kva.example.com")]
    public void Parse_normalizes_valid_hostnames(string input, string expected)
    {
        Assert.Equal(expected, Hostname.Parse(input).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("sample-app.local")]
    [InlineData("127.0.0.1")]
    [InlineData("*.example.com")]
    [InlineData("sample_app.example.com")]
    [InlineData("-sample-app.example.com")]
    [InlineData("sample-app-.example.com")]
    [InlineData("example")]
    public void Parse_rejects_invalid_or_local_hostnames(string input)
    {
        Assert.False(Hostname.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("sample-app.example.com", true)]
    [InlineData("SAMPLE-APP.EXAMPLE.COM:4580", true)]
    [InlineData("sample-app.example.com:0", false)]
    [InlineData("sample-app.example.com:99999", false)]
    [InlineData("sample-app.example.com@evil.example", false)]
    [InlineData("sample-app.example.com/path", false)]
    [InlineData("sample-app.example.com evil.example", false)]
    public void Matcher_handles_host_headers_safely(string header, bool expected)
    {
        Assert.Equal(expected, HostnameMatcher.Matches(Hostname.Parse("sample-app.example.com"), header));
    }

    [Fact]
    public void Route_table_exactly_matches_known_hostname_and_rejects_unknown()
    {
        var table = new HostnameRouteTable<string>(
        [
            KeyValuePair.Create(Hostname.Parse("sample-app.example.com"), "sample-app")
        ]);

        Assert.True(table.TryMatch("SAMPLE-APP.EXAMPLE.COM:4580", out var route));
        Assert.Equal("sample-app", route);
        Assert.False(table.TryMatch("unknown.example.com", out _));
    }

    [Fact]
    public void Route_table_rejects_duplicate_hostname()
    {
        Assert.Throws<ArgumentException>(() => new HostnameRouteTable<string>(
        [
            KeyValuePair.Create(Hostname.Parse("sample-app.example.com"), "one"),
            KeyValuePair.Create(Hostname.Parse("SAMPLE-APP.EXAMPLE.COM"), "two")
        ]));
    }
}

using System.Text;
using AbodeToWeb.Core.Security;

namespace AbodeToWeb.Core.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redacts_registered_secret_in_plain_url_encoded_and_base64_forms()
    {
        const string secret = "p@ss word/123";
        var encoded = Uri.EscapeDataString(secret);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        var redactor = new SecretRedactor([new SecretValue("APP_TOKEN", secret)]);

        var output = redactor.Redact($"raw={secret} encoded={encoded} base64={base64}");

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, output, StringComparison.Ordinal);
        Assert.DoesNotContain(base64, output, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(output, SecretRedactor.Replacement));
    }

    [Theory]
    [InlineData("password=hunter2", "password=[REDACTED]")]
    [InlineData("{\"api_key\":\"abc123\"}", "{\"api_key\":[REDACTED]}")]
    [InlineData("token: abc123", "token: [REDACTED]")]
    [InlineData("Authorization: Bearer ey-secret", "Authorization: Bearer [REDACTED]")]
    [InlineData("https://example.test/?token=hunter2&next=ok", "https://example.test/?token=[REDACTED]&next=ok")]
    [InlineData("https://example.test/?next=ok&api_key=abc123#section", "https://example.test/?next=ok&api_key=[REDACTED]#section")]
    [InlineData("?token=abc%26def&next=ok", "?token=[REDACTED]&next=ok")]
    [InlineData("%3Faccess_token%3Dabc123%26next%3Dok", "%3Faccess_token%3D[REDACTED]%26next%3Dok")]
    [InlineData("?client_secret=abc123&amp;next=ok", "?client_secret=[REDACTED]&amp;next=ok")]
    public void Redacts_sensitive_key_assignments_even_when_value_was_not_registered(string input, string expected)
    {
        var output = new SecretRedactor().Redact(input);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void Query_redaction_matches_sensitive_keys_exactly()
    {
        const string input = "?not_token=visible&tokenizer=visible&mytoken=visible&token=hidden&next=ok";

        var output = new SecretRedactor().Redact(input);

        Assert.Equal(
            "?not_token=visible&tokenizer=visible&mytoken=visible&token=[REDACTED]&next=ok",
            output);

        Assert.Equal(
            "%3Faccess_tokenizer%3Dvisible%26access_token%3D[REDACTED]%26next%3Dok",
            new SecretRedactor().Redact(
                "%3Faccess_tokenizer%3Dvisible%26access_token%3Dhidden%26next%3Dok"));
    }

    [Fact]
    public void Longer_overlapping_secret_is_replaced_first()
    {
        var redactor = new SecretRedactor(
        [
            new SecretValue("SHORT", "secret"),
            new SecretValue("LONG", "secret-value")
        ]);

        Assert.Equal(SecretRedactor.Replacement, redactor.Redact("secret-value"));
    }

    [Fact]
    public void Empty_secret_value_does_not_destroy_log_text()
    {
        var redactor = new SecretRedactor([new SecretValue("EMPTY", "")]);

        Assert.Equal("normal application output", redactor.Redact("normal application output"));
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
}

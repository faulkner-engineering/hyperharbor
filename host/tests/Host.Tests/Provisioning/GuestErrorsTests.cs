using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Provisioning;

public class GuestErrorsTests
{
    [Fact]
    public void Clean_ReplacesSecrets_LongestFirst()
    {
        // "abc" is part of "abcdef"; replacing it first would leave "def" behind.
        var cleaned = GuestErrors.Clean("x abcdef y abc z", "abc", "abcdef");

        Assert.Equal("x [redacted] y [redacted] z", cleaned);
    }

    [Fact]
    public void Clean_IgnoresEmptySecrets()
    {
        Assert.Equal("unchanged", GuestErrors.Clean("unchanged", null, string.Empty));
    }

    [Fact]
    public void Clean_RemovesControlCharacters()
    {
        var cleaned = GuestErrors.Clean("line one\r\nline two \u001b[31mred\u001b[0m\0");

        Assert.DoesNotContain(cleaned, char.IsControl);
        Assert.StartsWith("line one  line two", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_CapsLength()
    {
        var cleaned = GuestErrors.Clean(new string('a', GuestErrors.MaxMessageLength * 3));

        Assert.Equal(GuestErrors.MaxMessageLength + 3, cleaned.Length);
        Assert.EndsWith("...", cleaned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(GuestCredentialRejectedException))]
    [InlineData(typeof(GuestUnavailableException))]
    [InlineData(typeof(GuestOperationException))]
    [InlineData(typeof(GuestAccountConflictException))]
    public void Sanitize_KeepsType_AndDropsOriginal(Type type)
    {
        var original = (Exception)Activator.CreateInstance(type, "failed for s3cret")!;

        var sanitized = GuestErrors.Sanitize(original, "s3cret");

        Assert.IsType(type, sanitized);
        Assert.Equal("failed for [redacted]", sanitized.Message);
        Assert.Null(sanitized.InnerException);
    }
}

using HyperHarbor.Host.Service.Tray;

namespace HyperHarbor.Host.Tests.Tray;

public class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadsLines_StrippingCarriageReturns_AndDropsUnterminatedTail()
    {
        var reader = new BoundedLineReader(new StringReader("one\r\ntwo\n\nthree"), maxLength: 100);

        Assert.Equal("one", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("two", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadsLinesLongerThanItsBuffer()
    {
        var line = new string('a', 10_000);
        var reader = new BoundedLineReader(new StringReader(line + "\nnext\n"), maxLength: 20_000);

        Assert.Equal(line, await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("next", await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsALineAtTheLimit_AndRejectsOneOverIt()
    {
        var reader = new BoundedLineReader(new StringReader("12345\n123456\n"), maxLength: 5);

        Assert.Equal("12345", await reader.ReadLineAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAnEndlessLine_WithoutReadingItAll()
    {
        var reader = new BoundedLineReader(new EndlessReader(), maxLength: 64 * 1024);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
    }

    private sealed class EndlessReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count)
        {
            Array.Fill(buffer, 'x', index, count);
            return count;
        }
    }
}

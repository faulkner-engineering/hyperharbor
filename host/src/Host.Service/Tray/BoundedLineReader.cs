using System.Text;

namespace HyperHarbor.Host.Service.Tray;

/// <summary>
/// Reads "\n"-terminated lines like <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>, but
/// throws <see cref="InvalidDataException"/> once a line exceeds a maximum length, so a peer cannot
/// make the reader buffer without limit.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxLength)
{
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _position;
    private int _count;

    /// <summary>The next line without its terminator, or null at the end of the stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_position == _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken);
                _position = 0;
                if (_count == 0)
                {
                    // A final line without a terminator is incomplete; drop it.
                    return null;
                }
            }

            var newline = Array.IndexOf(_buffer, '\n', _position, _count - _position);
            var end = newline < 0 ? _count : newline;
            _line.Append(_buffer, _position, end - _position);
            _position = newline < 0 ? _count : newline + 1;

            if (_line.Length > maxLength)
            {
                throw new InvalidDataException($"Line exceeds {maxLength} characters.");
            }

            if (newline >= 0)
            {
                var line = _line.ToString().TrimEnd('\r');
                _line.Clear();
                return line;
            }
        }
    }
}

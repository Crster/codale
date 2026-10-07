using System.Text;

using Codale.Core.Agents;

namespace Codale.Core.Tasks;

/// <summary>
/// The terminals the user has open, as the agent's <c>list_terminals</c> / <c>read_terminal</c>
/// tools see them. The app implements it over its terminal tabs.
/// </summary>
public interface ITerminalSource
{
    IReadOnlyList<TaskSnapshot> List();

    /// <summary>Output since an offset (or the last <paramref name="tailChars"/> characters); null when no such terminal is open.</summary>
    TaskSnapshot? Read(string id, long? since, int? tailChars);
}

/// <summary>
/// What a terminal printed, as plain text: escape sequences are removed so the agent reads
/// the build output rather than colour codes. Capped like a hosted task's buffer, with
/// absolute offsets so a reader can ask for just what arrived after its last read.
/// </summary>
public sealed class TerminalTranscript
{
    private const int MaxChars = 200_000;
    private const int TrimSlack = MaxChars / 10;

    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private long _dropped;

    public void Append(string raw)
    {
        // A lone carriage return redraws the line (progress bars); only the CRLF pair is a line break.
        var text = BackgroundTaskDetector.StripAnsi(raw)
            .Replace("\r\n", "\n")
            .Replace("\r", "")
            .Replace("\a", "")
            .Replace("\b", "");

        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxChars + TrimSlack)
            {
                var cut = _buffer.Length - MaxChars;
                _buffer.Remove(0, cut);
                _dropped += cut;
            }
        }
    }

    /// <summary>Everything still held after <paramref name="since"/>, capped to the last <paramref name="tailChars"/>, and the offset just past it.</summary>
    public (string Text, long NextOffset) Read(long? since = null, int? tailChars = null)
    {
        lock (_gate)
        {
            var end = _dropped + _buffer.Length;
            var from = Math.Min(Math.Max(since ?? _dropped, _dropped), end);

            if (tailChars is > 0 && end - from > tailChars)
            {
                from = end - tailChars.Value;
            }

            return (_buffer.ToString((int)(from - _dropped), (int)(end - from)), end);
        }
    }
}

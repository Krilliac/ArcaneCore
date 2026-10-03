namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Reads several dumps as one text stream, in order, with a newline between files. One reader
/// over the whole stream means the existing importers (which build a single
/// <c>MySqlDumpReader</c> per <c>Read(TextReader)</c> call) see one <c>CREATE TABLE</c> registry
/// across files and rows of later files replacing earlier ones, exactly as if the files had
/// been concatenated. Inputs are opened lazily and closed as they are exhausted.
/// </summary>
public sealed class ChainedTextReader : TextReader
{
    private readonly IEnumerator<DumpInput> _inputs;
    private TextReader? _current;
    private bool _separatorPending;

    private ChainedTextReader(IEnumerable<DumpInput> inputs) => _inputs = inputs.GetEnumerator();

    /// <summary>A reader over <paramref name="inputs"/> in order.</summary>
    public static TextReader Create(IEnumerable<DumpInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return new ChainedTextReader(inputs);
    }

    public override int Peek()
    {
        while (true)
        {
            if (_separatorPending)
            {
                return '\n';
            }

            if (_current is null)
            {
                if (!_inputs.MoveNext())
                {
                    return -1;
                }

                _current = _inputs.Current.Open();
            }

            int next = _current.Peek();
            if (next >= 0)
            {
                return next;
            }

            _current.Dispose();
            _current = null;
            _separatorPending = true;
        }
    }

    public override int Read()
    {
        if (Peek() < 0)
        {
            return -1;
        }

        if (_separatorPending)
        {
            _separatorPending = false;
            return '\n';
        }

        return _current!.Read();
    }

    public override int Read(Span<char> buffer)
    {
        if (buffer.IsEmpty || Peek() < 0)
        {
            return 0;
        }

        if (_separatorPending)
        {
            buffer[0] = '\n';
            _separatorPending = false;
            return 1;
        }

        return _current!.Read(buffer);
    }

    public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _current?.Dispose();
            _current = null;
            _inputs.Dispose();
        }

        base.Dispose(disposing);
    }
}

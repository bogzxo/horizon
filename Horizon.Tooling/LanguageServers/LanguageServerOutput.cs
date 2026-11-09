using System.Text;

namespace Horizon.UI.Scripting;

// Drain even unterminated lines without letting a noisy server retain unlimited memory.
internal sealed class LanguageServerOutput
{
    private const int Limit = 8192;
    private readonly StringBuilder tail = new();
    private readonly Lock gate = new();

    internal string Text { get { lock (gate) return tail.ToString(); } }

    internal async Task Read(StreamReader reader)
    {
        char[] buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (gate)
                {
                    tail.Append(buffer, 0, count);
                    if (tail.Length > Limit) tail.Remove(0, tail.Length - Limit);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}

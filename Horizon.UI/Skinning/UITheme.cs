using Horizon.HIDL.Runtime;

namespace Horizon.UI.Skinning;

/// <summary>
/// A theme of a skin as its file describes it, before any of it is loaded. What an options screen lists.
/// </summary>
/// <param name="Name">What it is picked by, the key under <c>themes</c> or the name of its file.</param>
/// <param name="Title">What to call it in front of a player, its <c>title</c>, its name if it has none.</param>
/// <param name="Art">The layer of the skin's art it draws with, its own name unless its file says <c>art</c>. The sheet falls back if it hasn't got one.</param>
/// <param name="Base">The theme it starts from, null for the skin itself.</param>
public sealed record UIThemeInfo(string Name, string Title, string Art, string? Base);

/// <summary>
/// A theme is a palette and the layer of the art it is drawn with, and that is all it may say. One of the skin's
/// (<c>themes: { name: { ... } }</c> in the skin file, or a <c>name.hor</c> in a <c>themes</c> folder next to it with
/// a <c>let theme = { ... }</c> in it). What it may hold:
/// <list type="bullet">
/// <item><c>title</c>, what an options screen calls it.</item>
/// <item><c>base</c>, another theme of the skin to start from, this one's colours going over that one's.</item>
/// <item><c>art</c>, the layer of the sheet to draw with, for a theme that shares its art with another. Its own name if it doesn't say.</item>
/// <item>Any colour of the <see cref="UIPalette"/> (<c>text_color</c>, <c>accent_color</c>...), and <c>background</c> and
/// <c>surface</c>, which the colours it doesn't write are worked out from.</item>
/// <item><c>syntax</c>, the colours of code it wants its own way, the rest stay as they were.</item>
/// <item><c>terminal</c>, the sixteen colours of a terminal it wants its own way (red, bright_red), the rest worked out of its other colours.</item>
/// <item><c>regions</c> and <c>icons</c>, a piece of art tinted for this theme, added to the skin's.</item>
/// <item><c>corner_radius</c>, <c>popup_radius</c> and <c>row_height</c>, for a theme that's another shape of the skin as well as
/// other colours, square and tight the way Visual Studio is.</item>
/// </list>
/// Anything else (a font, the size of its text, the art scale) is the skin's and a theme that says it is refused.
/// </summary>
internal sealed class UITheme
{
    public const string FOLDER = "themes";

    public string Name { get; }
    public string Title { get; }
    public string? Base { get; }
    public string Art { get; }

    /// <summary>What it changes, by key, the colours and the syntax, regions and icons objects, as read.</summary>
    public IReadOnlyDictionary<string, IRuntimeValue> Changes { get; }

    public UIThemeInfo Info => new(Name, Title, Art, Base);

    private static readonly HashSet<string> Allowed = ["title", "base", "art", "syntax", "terminal", "regions", "icons", "corner_radius", "popup_radius", "row_height"];

    private UITheme(string name, string title, string? baseTheme, string art, Dictionary<string, IRuntimeValue> changes)
    {
        Name = name;
        Title = title;
        Base = baseTheme;
        Art = art;
        Changes = changes;
    }

    /// <summary>
    /// Reads a theme out of its object. Throws, saying which key, for one that says something a theme can't.
    /// </summary>
    public static UITheme Read(string name, IRuntimeValue value, string where)
    {
        if (value is not ObjectValue { Properties: var properties })
            throw new Exception($"{where} has to be an object.");

        var changes = new Dictionary<string, IRuntimeValue>();
        string title = name, art = name;
        string? baseTheme = null;

        foreach (var (key, entry) in properties)
        {
            switch (key)
            {
                case "title":
                    title = Scripting.UIScript.ToText(entry, $"{where}.title");
                    break;
                case "base":
                    baseTheme = Scripting.UIScript.ToText(entry, $"{where}.base");
                    if (baseTheme == name) throw new Exception($"{where} is based on itself.");
                    break;
                case "art":
                    art = Scripting.UIScript.ToText(entry, $"{where}.art");
                    break;
                default:
                    if (!UIPalette.IsKey(key) && !Allowed.Contains(key))
                        throw new Exception($"'{key}' isn't something a theme has, a theme is colours and art, '{key}' belongs in the skin.");

                    changes[key] = entry;
                    break;
            }
        }

        return new UITheme(name, title, baseTheme, art, changes);
    }
}

namespace Horizon.UI.Skinning;

// ReSharper's colours for C#, a name coloured by what it is. The dark ones are Rider's Dark scheme (Rider being
// ReSharper with an IDE round it), purple types, green methods, teal fields and properties, pink events and numbers,
// on its grey text. The light ones are ReSharper's own in Visual Studio's light theme, the dark cyan methods, purple
// fields and magenta events it's always had, over Visual Studio's keywords, texts and comments. Neither colours
// brackets by how deep they are, nor a local or a parameter apart from the rest of the text.
public sealed partial class UISyntaxColors
{
    /// <summary>ReSharper's colours for code on a dark theme, Rider's Dark scheme.</summary>
    public static UISyntaxColors ReSharperDark() => new()
    {
        Name = Rgb(0xBDBDBD),
        Keyword = Rgb(0x6C95EB),
        Control = Rgb(0x6C95EB),
        Property = Rgb(0x66C3CC),
        Call = Rgb(0x39CC9B),
        Text = Rgb(0xC9A26D),
        Number = Rgb(0xED94C0),
        Comment = Rgb(0x85C46C),
        Punctuation = Rgb(0xBDBDBD),
        LineNumber = Rgb(0x606366),
        Directive = Rgb(0x787878),
        Declaration = Rgb(0xBDBDBD),
        Builtin = Rgb(0xC191FF),
        Type = Rgb(0xC191FF),
        Constant = Rgb(0x6C95EB),
        Reference = Rgb(0xC9A26D),
        Escape = Rgb(0xD688D4),
        Markup = Rgb(0xD688D4),
        Operator = Rgb(0xBDBDBD),
        Bracket1 = Rgb(0xBDBDBD),
        Bracket2 = Rgb(0xBDBDBD),
        Bracket3 = Rgb(0xBDBDBD),

        Class = Rgb(0xC191FF),
        Interface = Rgb(0xC191FF),
        Record = Rgb(0xC191FF),
        TypeParameter = Rgb(0xC191FF),
        Struct = Rgb(0xE1BFFF),
        Enum = Rgb(0xE1BFFF),
        Delegate = Rgb(0xE1BFFF),
        Method = Rgb(0x39CC9B),
        ExtensionMethod = Rgb(0x39CC9B),
        Field = Rgb(0x66C3CC),
        NamedConstant = Rgb(0x66C3CC),
        EnumMember = Rgb(0x66C3CC),
        Event = Rgb(0xED94C0),
        Namespace = Rgb(0xBDBDBD),
        Local = Rgb(0xBDBDBD),
        Parameter = Rgb(0xBDBDBD),
        Label = Rgb(0xBDBDBD)
    };

    /// <summary>ReSharper's colours for code on a light theme, its identifiers over Visual Studio's light colours.</summary>
    public static UISyntaxColors ReSharperLight()
    {
        UISyntaxColors colors = VisualStudioLight();
        colors.Property = Rgb(0x800080);
        colors.Call = Rgb(0x008B8B);
        colors.Declaration = Rgb(0x000000);

        colors.Class = Rgb(0x2B91AF);
        colors.Interface = Rgb(0x2B91AF);
        colors.Record = Rgb(0x2B91AF);
        colors.Struct = Rgb(0x2B91AF);
        colors.Enum = Rgb(0x2B91AF);
        colors.Delegate = Rgb(0x2B91AF);
        colors.TypeParameter = Rgb(0x2B91AF);
        colors.Method = Rgb(0x008B8B);
        colors.ExtensionMethod = Rgb(0x008B8B);
        colors.Field = Rgb(0x800080);
        colors.NamedConstant = Rgb(0x800080);
        colors.EnumMember = Rgb(0x800080);
        colors.Event = Rgb(0xFF00FF);
        colors.Namespace = Rgb(0x000000);
        colors.Local = Rgb(0x000000);
        colors.Parameter = Rgb(0x000000);
        colors.Label = Rgb(0x000000);
        return colors;
    }
}

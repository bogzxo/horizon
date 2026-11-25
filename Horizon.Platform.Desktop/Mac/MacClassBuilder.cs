using System.Runtime.Versioning;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Makes a class of Objective-C's at run time, one of the program's own with the methods it says, which gives it objects
/// for what a Mac calls by name: the target of an action, a menu's delegate, an observer of a notification. The
/// methods are <c>[UnmanagedCallersOnly]</c> static methods, their address taken with <c>&amp;</c>
/// (<c>(nint)(delegate* unmanaged[Cdecl]&lt;nint, nint, nint, void&gt;)&amp;Pick</c>) and given with the type encoding the
/// runtime wants ("v@:@" for a method that returns nothing and takes an object, after <c>self</c> and the selector).
/// No block and nothing that isn't known when it's trimmed.
/// <code>
/// var builder = new MacClassBuilder("HexMenuTarget");
/// builder.AddMethod("pick:", (nint)(delegate* unmanaged[Cdecl]&lt;nint, nint, nint, void&gt;)&amp;Pick, "v@:@");
/// nint target = builder.Register(); // the class, then MacObjC.New(target) for an object of it
/// </code>
/// A class can only be registered once for a name, <see cref="Find"/> gets it back.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacClassBuilder
{
    private readonly string name;
    private nint type;
    private bool registered;

    /// <summary>A class of a name to be, a subclass of <c>NSObject</c> unless a class is said.</summary>
    public MacClassBuilder(string name, string superclass = "NSObject")
    {
        this.name = name;
        nint parent = MacObjC.Class(superclass);
        if (parent == 0)
            throw new InvalidOperationException($"There is no class {superclass} to make {name} from.");

        type = MacObjC.Runtime.AllocateClassPair(parent, name, 0);
        if (type == 0)
            throw new InvalidOperationException($"The class {name} is already there.");
    }

    /// <summary>The class of a name that was made, 0 if there isn't one.</summary>
    public static nint Find(string name) => MacObjC.Class(name);

    /// <summary>Gives the class a method. <paramref name="implementation"/> is the address of an <c>[UnmanagedCallersOnly]</c> static method.</summary>
    /// <param name="selector">What it's called, "pick:".</param>
    /// <param name="types">The type encoding, the return, then <c>@:</c> for self and the selector, then each argument ("v@:@", "c@:@" for a <c>BOOL</c> back).</param>
    public MacClassBuilder AddMethod(string selector, nint implementation, string types)
    {
        if (registered)
            throw new InvalidOperationException($"{name} is registered, methods go in before.");

        if (!MacObjC.Runtime.AddMethod(type, MacObjC.Sel(selector), implementation, types))
            throw new InvalidOperationException($"{name} has {selector} already.");

        return this;
    }

    /// <summary>Has the class adopt a protocol by its name ("NSMenuDelegate", "NSMenuItemValidation"), when the system has one of it, so a check of conformance says yes.</summary>
    public MacClassBuilder AddProtocol(string protocol)
    {
        nint found = MacObjC.Runtime.GetProtocol(protocol);
        if (found != 0)
            MacObjC.Runtime.AddProtocol(type, found);
        return this;
    }

    /// <summary>Makes the class there is to use. Once. Returns the class.</summary>
    public nint Register()
    {
        if (!registered)
        {
            MacObjC.Runtime.RegisterClassPair(type);
            registered = true;
        }

        return type;
    }

    /// <summary>An object of the class, <c>[[Class alloc] init]</c>, which the caller owns.</summary>
    public nint NewInstance() => MacObjC.New(Register());
}

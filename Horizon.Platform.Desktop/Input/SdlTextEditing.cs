using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Horizon.Input;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Watches SDL's IME text-editing and text-input events directly via <c>SDL_AddEventWatch</c> (AOT-safe
/// <see cref="UnmanagedCallersOnlyAttribute"/> callback).
/// <para>
/// Silk.NET's <c>KeyChar</c> callback splits UTF-8 input into individual UTF-16 <see cref="char"/> events and does not
/// forward <c>SDL_TEXTEDITING</c> / <c>SDL_TEXTEDITING_EXT</c> preedit state. Watching SDL directly preserves full
/// surrogate pairs (e.g. macOS Emoji picker) in a single <see cref="InputEvent.Typed"/> event and delivers inline IME
/// preedit composition updates via <see cref="InputEvent.Composition"/>.
/// </para>
/// </summary>
internal static unsafe class SdlTextEditing
{
    private const uint SDL_TEXTEDITING = 0x302;
    private const uint SDL_TEXTINPUT = 0x303;
    private const uint SDL_TEXTEDITING_EXT = 0x305;

    private static int installed;
    private static bool textInputStarted;
    private static Action<InputEvent>? listener;

    /// <summary>True once the SDL event watch has been installed.</summary>
    public static bool Active { get; private set; }

    /// <summary>True when SDL text input is being delivered through this watch and Silk's <c>KeyChar</c> should be skipped.</summary>
    public static bool HandlesTextInput => Active && Volatile.Read(ref listener) is not null;

    /// <summary>Installs the SDL event watch (once) and routes text-editing and text-input events to <paramref name="onEvent"/>.</summary>
    public static void Install(Action<InputEvent> onEvent)
    {
        Volatile.Write(ref listener, onEvent);
        if (Interlocked.Exchange(ref installed, 1) != 0)
            return;

        try
        {
            SdlProvider.SDL.Value.SetHint("SDL_IME_SUPPORT_EXTENDED_TEXT", "1");
            SdlProvider.SDL.Value.AddEventWatch(new PfnEventFilter(&Watch), null);
            Active = true;
            EnsureTextInputStarted();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // No SDL native library available; SilkInputSource falls back to keyboard.KeyChar
        }
    }

    private static void EnsureTextInputStarted()
    {
        if (textInputStarted || !Active)
            return;

        // Initializing SDL text input ensures Cocoa's SDLTranslatorResponder exists before SDL_SetTextInputRect runs on macOS.
        textInputStarted = true;
        SdlProvider.SDL.Value.StartTextInput();
    }

    /// <summary>Detaches <paramref name="onEvent"/> if it is the current listener.</summary>
    public static void Uninstall(Action<InputEvent> onEvent) =>
        Interlocked.CompareExchange(ref listener, null, onEvent);

    /// <summary>Sets the SDL IME candidate window rectangle in window points. Must be called on the SDL/platform thread.</summary>
    public static void SetTextInputRect(int x, int y, int width, int height)
    {
        if (!Active)
            return;

        try
        {
            EnsureTextInputStarted();
            var rect = new Rectangle<int>(x, y, Math.Max(1, width), Math.Max(1, height));
            SdlProvider.SDL.Value.SetTextInputRect(&rect);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Watch(void* userdata, Event* e)
    {
        try
        {
            if (e == null)
                return 1;

            Action<InputEvent>? sink = Volatile.Read(ref listener);
            if (sink is null)
                return 1;

            uint type = e->Type;
            if (type == SDL_TEXTEDITING)
            {
                string text = Marshal.PtrToStringUTF8((nint)e->Edit.Text) ?? string.Empty;
                sink(InputEvent.Composition(text, e->Edit.Start, e->Edit.Length));
            }
            else if (type == SDL_TEXTEDITING_EXT)
            {
                // Inside SDL_AddEventWatch, SDL still owns EditExt.Text and frees it after event processing;
                // copy synchronously and never free SDL's pointer.
                string text = e->EditExt.Text != null
                    ? Marshal.PtrToStringUTF8((nint)e->EditExt.Text) ?? string.Empty
                    : string.Empty;
                sink(InputEvent.Composition(text, e->EditExt.Start, e->EditExt.Length));
            }
            else if (type == SDL_TEXTINPUT)
            {
                string text = Marshal.PtrToStringUTF8((nint)e->Text.Text) ?? string.Empty;
                if (text.Length > 0)
                    sink(InputEvent.Typed(text));
            }
        }
        catch (Exception)
        {
            // Never let managed exceptions cross back into SDL's C event loop
        }

        return 1;
    }
}

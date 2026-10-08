using System.Globalization;

namespace Horizon.Input;

/// <summary>
/// A list of button presses to be played on a gamepad that isn't there, so a game can be walked through with nobody holding one.
/// For checking a menu or a level from a script, for a demo that plays itself, and for showing somebody how to reproduce a bug.
/// <code>
/// var script = new InputScript()
///     .Tap(2.0f, GamepadInput.A)
///     .Hold(3.0f, GamepadInput.DPadRight, 1.5f)
///     .Quit(8.0f);
///
/// Engine.Input.Play(script);
/// </code>
/// One can be written as a file as well, a step a line. Times are in seconds since the script was started, and anything after a hash is a comment.
/// <code>
/// 2.0 tap A
/// 3.0 hold DPadRight 1.5
/// 8.0 quit
/// </code>
/// Start any game of the engine with <see cref="ENVIRONMENT_VARIABLE"/> set to the path of such a file and it is played from the first update on.
/// The gamepad it plays on takes the first slot, so it is player one wherever that matters.
/// </summary>
public sealed class InputScript
{
    public const string ENVIRONMENT_VARIABLE = "HORIZON_INPUT_SCRIPT";

    // How long (in seconds) a tap holds its button, long enough for every update rate to see it
    private const float TAP_TIME = 0.08f;

    private const string STEP_TAP = "tap";
    private const string STEP_HOLD = "hold";
    private const string STEP_QUIT = "quit";

    private readonly record struct Step(float From, float Until, GamepadInput Input);

    private readonly List<Step> _steps = [];
    private float _quitAt = float.PositiveInfinity;

    /// <summary>
    /// When the last step of the script is over, in seconds.
    /// </summary>
    public float Length { get; private set; }

    /// <summary>
    /// Presses something and lets go of it right away.
    /// </summary>
    public InputScript Tap(float at, GamepadInput input) => Hold(at, input, TAP_TIME);

    /// <summary>
    /// Holds something down for a while.
    /// </summary>
    public InputScript Hold(float at, GamepadInput input, float seconds)
    {
        _steps.Add(new Step(at, at + MathF.Max(0.0f, seconds), input));
        Length = MathF.Max(Length, at + seconds);
        return this;
    }

    /// <summary>
    /// Closes the game at a moment, which is how a script that checks something ends.
    /// </summary>
    public InputScript Quit(float at)
    {
        _quitAt = at;
        Length = MathF.Max(Length, at);
        return this;
    }

    /// <summary>
    /// Whether the script wants the game closed by a moment.
    /// </summary>
    internal bool WantsQuit(float time) => time >= _quitAt;

    /// <summary>
    /// What the gamepad of the script holds at a moment.
    /// </summary>
    internal GamepadSnapshot At(float time)
    {
        var snapshot = new GamepadSnapshot();

        foreach (Step step in _steps)
        {
            if (time >= step.From && time < step.Until)
                snapshot.Buttons |= GamepadInputs.Bit(step.Input);
        }

        return snapshot;
    }

    /// <summary>
    /// Reads a script out of a file. Lines that make no sense are skipped and said so in <paramref name="problems"/>.
    /// </summary>
    public static InputScript Load(string path, out List<string> problems)
    {
        var script = new InputScript();
        problems = [];

        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;

            int comment = raw.IndexOf('#');
            string[] words = (comment >= 0 ? raw[..comment] : raw).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) continue;

            if (!script.TryRead(words))
                problems.Add($"line {number} is not a step: '{raw.Trim()}'");
        }

        return script;
    }

    /// <summary>
    /// Helper method to turn the words of a line into a step, false if they aren't one.
    /// </summary>
    private bool TryRead(string[] words)
    {
        if (words.Length < 2 || !TryNumber(words[0], out float at)) return false;

        switch (words[1].ToLowerInvariant())
        {
            case STEP_QUIT:
                Quit(at);
                return true;

            case STEP_TAP when words.Length >= 3 && GamepadInputs.TryParse(words[2], out GamepadInput tapped):
                Tap(at, tapped);
                return true;

            case STEP_HOLD when words.Length >= 4 && GamepadInputs.TryParse(words[2], out GamepadInput held) && TryNumber(words[3], out float seconds):
                Hold(at, held, seconds);
                return true;

            default:
                return false;
        }
    }

    private static bool TryNumber(string text, out float number) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}

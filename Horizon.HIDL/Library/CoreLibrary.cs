using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL.Library;

/// <summary>
/// The functions every program gets. Walking objects (keys, values, has), making ranges, print, and include for
/// pulling another file in. The text and math ones are in their own files.
/// </summary>
public static class CoreLibrary
{
    public static void Declare(HIDLRuntime runtime, Environment scope)
    {
        scope.Declare("keys", Natives.Function("keys", args => Natives.List(args.Object(0).Properties.Keys)), true);

        scope.Declare("values", Natives.Function("values", args =>
        {
            var list = new ListValue();
            foreach (IRuntimeValue value in args.Object(0).Properties.Values)
                list.Items.Add(value is NativeValue native ? native.AccessorCallback() : value);

            return list;
        }), true);

        scope.Declare("has", Natives.Function("has", args => args[0] switch
        {
            ObjectValue obj => Values.Bool(obj.Properties.ContainsKey(args.Text(1))),
            ListValue list => Values.Bool(list.Items.Exists(item => Values.AreEqual(item, args[1]))),
            StringValue text => Values.Bool(text.Value.Contains(args.Text(1), StringComparison.Ordinal)),
            _ => throw args.Wrong($"a {Values.TypeName(args[0])} has nothing to look in.")
        }), true);

        // range(n) is 0 up to n, range(a, b) is a up to b, range(a, b, step) in steps
        scope.Declare("range", Natives.Function("range", args =>
        {
            float from = args.Has(1) ? args.Number(0) : 0.0f;
            float to = args.Has(1) ? args.Number(1) : args.Number(0);
            float step = args.Number(2, 1.0f);

            if (step == 0.0f) throw args.Wrong("a step of 0 never gets anywhere.");
            if ((to - from) / step > 10_000_000) throw args.Wrong("that is too many numbers for one list.");

            var list = new ListValue();
            for (float value = from; step > 0.0f ? value < to : value > to; value += step)
                list.Items.Add(new NumberValue(value));

            return list;
        }), true);

        scope.Declare("print", Natives.Function("print", args =>
        {
            var pieces = new string[args.Count];
            for (int i = 0; i < pieces.Length; i++) pieces[i] = Values.Text(args[i]);

            string line = string.Join(" ", pieces);
            runtime.Output?.Invoke(line);
            return new StringValue(line);
        }), true);

        // error("message") stops the program with a message of the script's own
        scope.Declare("error", Natives.Function("error", args => throw new HidlRuntimeException(args.Text(0, "The script said stop."))), true);

        // include("other.hor") runs another file and hands back an object of everything it declared.
        // The path is next to the file that is running, or wherever the runtime was told its files are
        scope.Declare("include", Natives.Function("include", args => runtime.Include(args.Text(0))), true);
    }
}

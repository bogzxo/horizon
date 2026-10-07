using Horizon.HIDL.Runtime;

using Environment = Horizon.HIDL.Runtime.Environment;

namespace Horizon.HIDL.Library;

/// <summary>
/// The math object. math.min(a, b), math.clamp(x, 0, 1), math.lerp(a, b, t), math.random() and the rest of what a
/// game script reaches for. Most of them take vectors as well as numbers and work on every component.
/// </summary>
public static class MathLibrary
{
    private static readonly Random random = new();

    public static void Declare(Environment scope)
    {
        scope.Declare("math", Natives.Object(
            ("pi", new NumberValue(MathF.PI)),
            ("tau", new NumberValue(MathF.Tau)),
            ("e", new NumberValue(MathF.E)),

            ("abs", Each("abs", MathF.Abs)),
            ("floor", Each("floor", MathF.Floor)),
            ("ceil", Each("ceil", MathF.Ceiling)),
            ("sqrt", Each("sqrt", MathF.Sqrt)),
            ("sign", Each("sign", x => MathF.Sign(x))),
            ("sin", Each("sin", MathF.Sin)),
            ("cos", Each("cos", MathF.Cos)),
            ("tan", Each("tan", MathF.Tan)),
            ("asin", Each("asin", MathF.Asin)),
            ("acos", Each("acos", MathF.Acos)),
            ("atan", Each("atan", MathF.Atan)),
            ("exp", Each("exp", MathF.Exp)),
            ("log", Each("log", MathF.Log)),
            ("to_radians", Each("to_radians", x => x * MathF.PI / 180.0f)),
            ("to_degrees", Each("to_degrees", x => x * 180.0f / MathF.PI)),

            ("round", Natives.Function("round", args =>
            {
                // To whole numbers, or to so many digits after the point
                float scale = MathF.Pow(10.0f, args.Integer(1, 0));
                return Apply("round", args[0], x => MathF.Round(x * scale, MidpointRounding.AwayFromZero) / scale);
            })),
            ("atan2", Natives.Function("atan2", args => new NumberValue(MathF.Atan2(args.Number(0), args.Number(1))))),
            ("pow", Natives.Function("pow", args => new NumberValue(MathF.Pow(args.Number(0), args.Number(1))))),

            ("min", Natives.Function("min", args => Fold("min", args, MathF.Min))),
            ("max", Natives.Function("max", args => Fold("max", args, MathF.Max))),
            ("clamp", Natives.Function("clamp", args => Apply("clamp", args[0], x => Math.Clamp(x, args.Number(1), args.Number(2))))),
            ("lerp", Natives.Function("lerp", args =>
            {
                float t = args.Number(2);
                return Combine("lerp", args[0], args[1], (a, b) => a + (b - a) * t);
            })),
            ("distance", Natives.Function("distance", args =>
            {
                Span<float> a = stackalloc float[4], b = stackalloc float[4];
                int count = args.Components(0, a);
                if (args.Components(1, b) != count) throw args.Wrong("the two vectors have to be the same size.");

                float sum = 0.0f;
                for (int i = 0; i < count; i++) sum += (a[i] - b[i]) * (a[i] - b[i]);
                return new NumberValue(MathF.Sqrt(sum));
            })),
            ("length", Natives.Function("length", args =>
            {
                Span<float> a = stackalloc float[4];
                int count = args.Components(0, a);

                float sum = 0.0f;
                for (int i = 0; i < count; i++) sum += a[i] * a[i];
                return new NumberValue(MathF.Sqrt(sum));
            })),
            ("normalize", Natives.Function("normalize", args =>
            {
                Span<float> a = stackalloc float[4];
                int count = args.Components(0, a);

                float sum = 0.0f;
                for (int i = 0; i < count; i++) sum += a[i] * a[i];
                float length = MathF.Sqrt(sum);

                // Nothing points nowhere
                if (length > 0.0f)
                {
                    for (int i = 0; i < count; i++) a[i] /= length;
                }

                return Values.Vector(a[..count]);
            })),
            ("dot", Natives.Function("dot", args =>
            {
                Span<float> a = stackalloc float[4], b = stackalloc float[4];
                int count = args.Components(0, a);
                if (args.Components(1, b) != count) throw args.Wrong("the two vectors have to be the same size.");

                float sum = 0.0f;
                for (int i = 0; i < count; i++) sum += a[i] * b[i];
                return new NumberValue(sum);
            })),

            // random() is 0 up to 1, random(n) is 0 up to n, random(a, b) is a up to b. Whole numbers if both ends are whole
            ("random", Natives.Function("random", args =>
            {
                if (!args.Has(0)) return new NumberValue(random.NextSingle());

                float low = args.Has(1) ? args.Number(0) : 0.0f;
                float high = args.Has(1) ? args.Number(1) : args.Number(0);

                bool whole = low == MathF.Floor(low) && high == MathF.Floor(high);
                return whole
                    ? new NumberValue(random.Next((int)low, (int)high))
                    : new NumberValue(low + random.NextSingle() * (high - low));
            })),
            ("chance", Natives.Function("chance", args => Values.Bool(random.NextSingle() < args.Number(0)))),
            ("pick", Natives.Function("pick", args =>
            {
                // One item out of a list, or one out of the arguments
                if (args.Count == 1 && args[0] is ListValue list)
                    return list.Count > 0 ? list[random.Next(list.Count)] : Values.Null;

                return args.Count > 0 ? args[random.Next(args.Count)] : Values.Null;
            }))
        ), true);
    }

    /// <summary>
    /// A function of one number that works on a vector component by component too.
    /// </summary>
    private static NativeFunctionValue Each(string name, Func<float, float> function) =>
        Natives.Function(name, args => Apply(name, args[0], function));

    private static IRuntimeValue Apply(string name, IRuntimeValue value, Func<float, float> function)
    {
        if (value is NumberValue number)
            return new NumberValue(function(number.Value));

        Span<float> components = stackalloc float[4];
        if (!Values.TryGetComponents(value, components, out int count))
            throw new HidlRuntimeException($"math.{name} takes a number or a vector, not {Values.TypeName(value)}.");

        for (int i = 0; i < count; i++) components[i] = function(components[i]);
        return Values.Vector(components[..count]);
    }

    private static IRuntimeValue Combine(string name, IRuntimeValue first, IRuntimeValue second, Func<float, float, float> function)
    {
        if (first is NumberValue a && second is NumberValue b)
            return new NumberValue(function(a.Value, b.Value));

        Span<float> x = stackalloc float[4], y = stackalloc float[4];
        if (!Values.TryGetComponents(first, x, out int count) || !Values.TryGetComponents(second, y, out int other) || count != other)
            throw new HidlRuntimeException($"math.{name} takes two numbers or two vectors of the same size.");

        for (int i = 0; i < count; i++) x[i] = function(x[i], y[i]);
        return Values.Vector(x[..count]);
    }

    /// <summary>
    /// min and max of any number of numbers, or of two vectors component by component.
    /// </summary>
    private static IRuntimeValue Fold(string name, Arguments args, Func<float, float, float> function)
    {
        if (args.Count == 0) throw args.Wrong("give it something to compare.");

        // A single list is folded over its items
        if (args.Count == 1 && args[0] is ListValue list)
        {
            if (list.Count == 0) return Values.Null;

            IRuntimeValue folded = list[0];
            for (int i = 1; i < list.Count; i++) folded = Combine(name, folded, list[i], function);
            return folded;
        }

        IRuntimeValue result = args[0];
        for (int i = 1; i < args.Count; i++) result = Combine(name, result, args[i], function);
        return result;
    }
}

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

using Horizon.Core;

namespace Horizon.Engine.Debugging;

/// <summary>
/// One thing about an object the inspector shows a row for, a field or a property, with the way to read it and
/// (where it can be changed) to write it.
/// </summary>
internal sealed record InspectedMember(string Name, Type Type, Type DeclaredBy, Func<object, object?> Get, Action<object, object?>? Set, InspectAttribute? Hint)
{
    public bool CanWrite => Set is not null;
}

/// <summary>
/// Finds what there is to show of an object. Every public field and property it has, its own first and then what
/// it got from what it is derived from, plus whatever isn't public and asked to be shown with <see cref="InspectAttribute"/>,
/// less what said it wants no part of this (<see cref="HideInInspectorAttribute"/>). Looked up once a type and kept.
/// </summary>
#pragma warning disable IL2070, IL2075, IL2026, IL2067, IL2072
internal static class Inspectable
{
    private const BindingFlags DECLARED = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, InspectedMember[]> known = [];

    public static IReadOnlyList<InspectedMember> MembersOf(Type type)
    {
        lock (known)
        {
            if (known.TryGetValue(type, out var found))
                return found;

            var members = new List<InspectedMember>();
            var names = new HashSet<string>();

            for (Type? at = type; at is not null && at != typeof(object) && at != typeof(ValueType); at = at.BaseType)
            {
                foreach (PropertyInfo property in at.GetProperties(DECLARED))
                {
                    if (Describe(property, at) is { } member && names.Add(member.Name))
                        members.Add(member);
                }

                foreach (FieldInfo field in at.GetFields(DECLARED))
                {
                    if (Describe(field, at) is { } member && names.Add(member.Name))
                        members.Add(member);
                }
            }

            return known[type] = [.. members];
        }
    }

    private static InspectedMember? Describe(PropertyInfo property, Type declaredBy)
    {
        if (property.GetMethod is not { } getter || getter.IsStatic || property.GetIndexParameters().Length > 0)
            return null;

        var hint = property.GetCustomAttribute<InspectAttribute>();
        if ((!getter.IsPublic && hint is null) || property.IsDefined(typeof(HideInInspectorAttribute)) || !Showable(property.PropertyType))
            return null;

        // An init only setter is for whoever makes the thing, not for whoever comes by later
        MethodInfo? setter = property.SetMethod;
        bool writable = setter is not null && (setter.IsPublic || hint is not null) &&
            !setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

        return new InspectedMember(
            hint is { Name.Length: > 0 } ? hint.Name : property.Name,
            property.PropertyType,
            declaredBy,
            property.GetValue,
            writable ? property.SetValue : null,
            hint);
    }

    private static InspectedMember? Describe(FieldInfo field, Type declaredBy)
    {
        if (field.IsStatic || field.IsDefined(typeof(CompilerGeneratedAttribute)))
            return null;

        var hint = field.GetCustomAttribute<InspectAttribute>();
        if ((!field.IsPublic && hint is null) || field.IsDefined(typeof(HideInInspectorAttribute)) || !Showable(field.FieldType))
            return null;

        return new InspectedMember(
            hint is { Name.Length: > 0 } ? hint.Name : field.Name,
            field.FieldType,
            declaredBy,
            field.GetValue,
            field.IsInitOnly || field.IsLiteral ? null : field.SetValue,
            hint);
    }

    /// <summary>Helper method for whether a value of a type can be read into a box at all, and means anything once it is.</summary>
    private static bool Showable(Type type) =>
        !type.IsByRefLike && !type.IsPointer && !type.IsByRef && !typeof(Delegate).IsAssignableFrom(type);

    /// <summary>What a nullable is a nullable of, the type itself for everything else.</summary>
    public static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    public static bool IsNumber(Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(uint) || type == typeof(long) ||
        type == typeof(ulong) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(nint) || type == typeof(nuint);

    public static bool IsWhole(Type type) => IsNumber(type) && type != typeof(float) && type != typeof(double);

    /// <summary>
    /// A number out of a box turned into the kind of number a field wants, kept inside of what that kind can hold,
    /// a byte doesn't go to 300 and nothing unsigned goes under zero.
    /// </summary>
    public static object ToNumber(float value, Type type)
    {
        if (type == typeof(float)) return value;
        if (type == typeof(double)) return (double)value;

        double whole = Math.Round(value);
        if (type == typeof(int)) return (int)Math.Clamp(whole, int.MinValue, int.MaxValue);
        if (type == typeof(uint)) return (uint)Math.Clamp(whole, 0.0, uint.MaxValue);
        if (type == typeof(long)) return (long)whole;
        if (type == typeof(ulong)) return (ulong)Math.Max(whole, 0.0);
        if (type == typeof(short)) return (short)Math.Clamp(whole, short.MinValue, short.MaxValue);
        if (type == typeof(ushort)) return (ushort)Math.Clamp(whole, 0.0, ushort.MaxValue);
        if (type == typeof(byte)) return (byte)Math.Clamp(whole, 0.0, byte.MaxValue);
        if (type == typeof(sbyte)) return (sbyte)Math.Clamp(whole, sbyte.MinValue, sbyte.MaxValue);
        if (type == typeof(nint)) return (nint)whole;
        if (type == typeof(nuint)) return (nuint)Math.Max(whole, 0.0);

        return value;
    }

    /// <summary>Whether three or four numbers by that name are a colour.</summary>
    public static bool IsColour(string name, InspectAttribute? hint) =>
        hint is { Color: true } ||
        name.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Colour", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Tint", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Ambient", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether there is anything inside of a value to open it up for, which a number or a word hasn't got.</summary>
    public static bool HasInside(Type type) =>
        !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal) &&
        (typeof(IEnumerable).IsAssignableFrom(type) || MembersOf(type).Count > 0);

    /// <summary>What a type is called by somebody who writes C#, List&lt;Light2D&gt; rather than List`1.</summary>
    public static string NameOf(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
            return NameOf(inner) + "?";
        if (type.IsArray)
            return NameOf(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.Name;

        string name = type.Name;
        int tick = name.IndexOf('`');
        return $"{(tick > 0 ? name[..tick] : name)}<{string.Join(", ", type.GetGenericArguments().Select(NameOf))}>";
    }
}
#pragma warning restore IL2070, IL2075, IL2026, IL2067, IL2072

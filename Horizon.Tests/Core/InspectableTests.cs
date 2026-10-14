#if DEBUG
using System.Numerics;

using Horizon.Core;
using Horizon.Engine.Debugging;

namespace Horizon.Tests;

/// <summary>
/// What the inspector of the Skyline debugger finds on an object, which is all reflection and so the part of it that
/// can be checked without a window. Only in Debug, a Release build hasn't got the suite.
/// </summary>
public class InspectableTests
{
    private class Creature
    {
        public float Health = 10.0f;
        public string Name { get; set; } = "blob";
        public Vector2 Size { get; } = new(8.0f, 6.0f);
        public int Made { get; init; } = 3;

        [Inspect(0.0f, 400.0f)] private float walkSpeed = 120.0f;
        [HideInInspector] public int Plumbing;

        private int secret = 7;
        public static int Count;
        public Action? Died;
        public int this[int index] => index;

        public float WalkSpeed => walkSpeed + secret * 0.0f;
    }

    private sealed class Fighter : Creature
    {
        public bool Blocking { get; set; }
        [Inspect(Name = "combo")] private int hits = 2;

        public int Hits => hits;
    }

    private struct Knockback
    {
        public Vector2 Push;
        public float Stun;
    }

    private static InspectedMember Member<T>(string name) => Inspectable.MembersOf(typeof(T)).Single(member => member.Name == name);

    [Fact]
    public void Public_fields_and_properties_are_found_and_the_plumbing_is_not()
    {
        string[] names = [.. Inspectable.MembersOf(typeof(Creature)).Select(member => member.Name)];

        Assert.Contains("Health", names);
        Assert.Contains("Name", names);
        Assert.Contains("Size", names);
        Assert.Contains("walkSpeed", names);

        // Hidden on purpose, private without asking, static, a delegate, an indexer
        Assert.DoesNotContain("Plumbing", names);
        Assert.DoesNotContain("secret", names);
        Assert.DoesNotContain("Count", names);
        Assert.DoesNotContain("Died", names);
        Assert.DoesNotContain("Item", names);
    }

    [Fact]
    public void What_can_be_set_says_so()
    {
        Assert.True(Member<Creature>("Health").CanWrite);
        Assert.True(Member<Creature>("Name").CanWrite);
        Assert.True(Member<Creature>("walkSpeed").CanWrite);

        // No setter, and one that is only for whoever makes the thing
        Assert.False(Member<Creature>("Size").CanWrite);
        Assert.False(Member<Creature>("Made").CanWrite);
    }

    [Fact]
    public void A_private_field_that_asks_is_read_and_written()
    {
        var creature = new Creature();
        InspectedMember speed = Member<Creature>("walkSpeed");

        Assert.Equal(120.0f, speed.Get(creature));
        Assert.True(speed.Hint is { HasRange: true, Min: 0.0f, Max: 400.0f });

        speed.Set!(creature, 200.0f);
        Assert.Equal(200.0f, creature.WalkSpeed);
    }

    [Fact]
    public void Its_own_come_before_what_it_was_derived_from_and_a_name_can_be_given()
    {
        var members = Inspectable.MembersOf(typeof(Fighter));

        Assert.Equal(typeof(Fighter), members[0].DeclaredBy);
        Assert.Equal(typeof(Creature), members[^1].DeclaredBy);
        Assert.Contains(members, member => member.Name == "combo");

        var fighter = new Fighter();
        Member<Fighter>("combo").Set!(fighter, 5);
        Assert.Equal(5, fighter.Hits);
    }

    [Fact]
    public void A_struct_is_changed_in_its_box()
    {
        // Which is why the inspector writes the box back to wherever it came from afterwards
        object boxed = new Knockback { Stun = 0.2f };
        Member<Knockback>("Stun").Set!(boxed, 0.5f);

        Assert.Equal(0.5f, ((Knockback)boxed).Stun);
    }

    [Theory]
    [InlineData(300.0f, typeof(byte), (byte)255)]
    [InlineData(-4.0f, typeof(uint), 0u)]
    [InlineData(2.6f, typeof(int), 3)]
    [InlineData(2.6f, typeof(float), 2.6f)]
    public void A_number_out_of_a_box_becomes_the_kind_the_field_wants(float typed, Type kind, object expected) =>
        Assert.Equal(expected, Inspectable.ToNumber(typed, kind));

    [Fact]
    public void Colours_are_told_by_their_names_and_types_are_called_what_they_are()
    {
        Assert.True(Inspectable.IsColour("ClearColor", null));
        Assert.True(Inspectable.IsColour("tint", null));
        Assert.False(Inspectable.IsColour("Position", null));
        Assert.True(Inspectable.IsColour("Glow", new InspectAttribute { Color = true }));

        Assert.Equal("List<Vector2>", Inspectable.NameOf(typeof(List<Vector2>)));
        Assert.Equal("Single?", Inspectable.NameOf(typeof(float?)));
        Assert.Equal("Int32[]", Inspectable.NameOf(typeof(int[])));

        Assert.True(Inspectable.HasInside(typeof(Knockback)));
        Assert.True(Inspectable.HasInside(typeof(List<int>)));
        Assert.False(Inspectable.HasInside(typeof(string)));
        Assert.False(Inspectable.HasInside(typeof(float)));
    }
}
#endif

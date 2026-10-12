using System.Numerics;

using Horizon.Graphics;
using Horizon.Physics;
using Horizon.Physics.Simulation;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;

namespace Horizon.Testing.Examples.Showcase;

// Who lives in the village. The hero, who is a body steered from above with a sprite put on top of it, and the
// villagers, who walk a tile at a time the way they did on a Game Boy and have a line each.
public partial class VillageExample
{
    /// <summary>
    /// Somebody who lives here. Where the map put them is home, they wander a few tiles from it if they wander at
    /// all, and they are a body in the world only while the hero is in their room, a wall he can talk to.
    /// </summary>
    private sealed class Villager
    {
        public required Sprite Sprite;
        public required TileMapObject Object;
        public required TileMapRoom Room;
        public required TileMapPathfinder Paths;
        public required PhysicsBodyComponent2D Body;
        public required string Says;
        public required bool Wanders;

        public Vector2 Position, Facing, From, To;
        public float Walked = 1.0f, Rest;
        public bool Walking, Awake;
    }

    // The hero's sheet has a row a way he faces and three frames of walking on each
    private static readonly string[] Ways = ["down", "up", "right", "left"];

    // How long a step from one tile to the next takes a villager, and how long they stand about between steps
    private const float STEP_TIME = 0.34f;
    private const float REST_LEAST = 0.6f, REST_MOST = 2.8f;

    // How far from home a villager wanders, in tiles
    private const int LEASH = 3;

    private readonly List<Villager> _villagers = [];
    private readonly Dictionary<TileMapRoom, TileMapPathfinder> _paths = [];

    private Sprite _hero = null!;
    private CharacterController2D _walker = null!;
    private Vector2 _heroFacing = -Vector2.UnitY;
    private string _heroAnimation = string.Empty;

    private void MakePeople()
    {
        SpriteSheet heroes = Sheet(HERO), villagers = Sheet(VILLAGERS);

        /* The hero, a body in the world with somebody steering it from above */

        _hero = _actors.AddEntity(new Sprite(new Vector2(CELL)));
        _hero.Transform.Origin = Horizon.Rendering.Origin.Bottom;
        _hero.ConfigureSpriteSheet(heroes, "stand_down");
        for (int row = 0; row < Ways.Length; row++)
        {
            _hero.AddAnimation($"walk_{Ways[row]}", new Vector2(0, row), 3, 0.14f);
            _hero.AddAnimation($"stand_{Ways[row]}", new Vector2(1, row), 1);
        }
        _hero.CastsShadows = true;
        _actors.Add(_hero);

        _walker = AddComponent(new CharacterController2D(_physics, _spawn, HeroBox, topDown: true)
        {
            MaxSpeed = WALK_SPEED,
            Acceleration = 900.0f,
            Deceleration = 1400.0f
        });

        /* The villagers, wherever any of the maps say one stands */

        foreach (TileMapRoom room in _rooms.Rooms)
        {
            // Where a villager can and can't step, which is what the room's colliders say
            var paths = room.Map.CreatePathfinder();
            _paths[room] = paths;

            room.Map.DispatchObjects(objects => objects.OfClass("npc", found =>
            {
                int look = Math.Clamp(found.Properties.GetInt("look"), 0, 3);

                // One picture a way they face, the sideways one is turned over for the other side
                var sprite = _actors.AddEntity(new Sprite(new Vector2(CELL)));
                sprite.Transform.Origin = Horizon.Rendering.Origin.Bottom;
                sprite.ConfigureSpriteSheet(villagers, "down");
                sprite.AddAnimation("down", new Vector2(0, look), 1);
                sprite.AddAnimation("side", new Vector2(1, look), 1);
                sprite.AddAnimation("up", new Vector2(2, look), 1);
                sprite.CastsShadows = true;
                _actors.Add(sprite);

                // In the way, like anybody, once they are in the world at all
                var body = new PhysicsBodyComponent2D(found.Position) { SimulationType = PhysicsBodySimulationType.Static };
                body.CreateRectangularFixture(-HeroBox / 2.0f, HeroBox, tag: "villager");

                var villager = new Villager
                {
                    Sprite = sprite,
                    Object = found,
                    Room = room,
                    Paths = paths,
                    Body = body,
                    Says = found.Properties.GetString("says", "..."),
                    Wanders = found.Properties.GetBool("wanders", true),
                    Position = found.Position,
                    Facing = TileMapRooms.DirectionOf(found.Properties.GetString("faces", "down")),
                    Rest = Random.Shared.NextSingle() * REST_MOST
                };
                if (villager.Facing == Vector2.Zero) villager.Facing = -Vector2.UnitY;

                _villagers.Add(villager);
                if (room.IsCurrent) Wake(villager);
            }));
        }
    }

    /// <summary>Helper method to read a sheet of 16 by 16 cells off a file. Render thread, it is a texture.</summary>
    private SpriteSheet Sheet(string path)
    {
        if (!Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription { Paths = [path], Definition = TextureDefinition.RgbaUnsignedByteNearest },
                out var texture))
        {
            throw new Exception($"Couldn't make a texture out of {path}: {texture.Message}");
        }

        return SpriteSheet.FromTexture(texture.Asset, new Vector2(CELL));
    }

    /// <summary>Helper method to put the sprite where the body is, facing the way he was last steered, walking if he is.</summary>
    private void ShowHero()
    {
        Vector2 middle = _walker.Position;
        _hero.Transform.Position = middle + Feet;

        // Four ways and no more, whichever of the two the stick leans to most
        Vector2 steer = _walker.Move;
        if (steer.LengthSquared() > 0.01f)
            _heroFacing = MathF.Abs(steer.X) > MathF.Abs(steer.Y) ? new Vector2(MathF.Sign(steer.X), 0.0f) : new Vector2(0.0f, MathF.Sign(steer.Y));

        bool walking = _walker.Velocity.LengthSquared() > 64.0f;
        string animation = (walking ? "walk_" : "stand_") + WayOf(_heroFacing);
        if (!ReferenceEquals(animation, _heroAnimation) && animation != _heroAnimation)
        {
            _heroAnimation = animation;
            _hero.SetAnimation(animation);
        }
    }

    private static string WayOf(Vector2 facing) =>
        facing.Y < -0.5f ? Ways[0] : facing.Y > 0.5f ? Ways[1] : facing.X > 0.5f ? Ways[2] : Ways[3];

    /// <summary>
    /// Helper method to have somebody who was spoken to turn round and say their line, if there is anybody within
    /// earshot. They stay put for a moment after, it is rude to walk off.
    /// </summary>
    private void Talk()
    {
        Villager? nearest = null;
        float best = EARSHOT;
        foreach (Villager villager in _villagers)
        {
            if (!villager.Awake) continue;
            float distance = Vector2.Distance(villager.Position, _walker.Position);
            if (distance < best) (best, nearest) = (distance, villager);
        }

        if (nearest is null)
            return;

        Vector2 to = _walker.Position - nearest.Position;
        nearest.Facing = MathF.Abs(to.X) > MathF.Abs(to.Y) ? new Vector2(MathF.Sign(to.X), 0.0f) : new Vector2(0.0f, MathF.Sign(to.Y));
        nearest.Rest = MathF.Max(nearest.Rest, 3.0f);
        Say(nearest.Says, SAID_FOR);
    }

    /// <summary>A villager steps into the physics world, somebody is in their room to bump into them.</summary>
    private void Wake(Villager villager)
    {
        if (villager.Awake) return;
        villager.Awake = true;

        // Where the map has them now, the room may have been moved since they were made
        if (!villager.Walking)
            villager.Position = villager.Object.Position;

        villager.Body.Position = villager.Position;
        _physics.StaticBodies.Add(villager.Body);
    }

    /// <summary>And out of it again, there is nobody in the room to bump into them and they are not in the way of whoever is in the room over it.</summary>
    private void Park(Villager villager)
    {
        if (!villager.Awake) return;
        villager.Awake = false;
        _physics.StaticBodies.Remove(villager.Body);
    }

    /// <summary>
    /// Helper method to walk the villagers. Every so often one that wanders picks a way and takes a step of one
    /// tile that way, if the tile is free, the way nobody has ever had to think about since 1996. A step is a slide
    /// from the middle of one tile to the middle of the next with a little hop in it, since the sheet has no walk
    /// frames, and their body goes along underneath. They only show as much as their room does, so the ones in a
    /// room that is fading out fade with it, and the ones in the street go with the street when it dims.
    /// </summary>
    private void MovePeople(float dt)
    {
        foreach (Villager villager in _villagers)
        {
            float shown = villager.Room.Map.Opacity;
            if (villager.Room != _rooms.Current)
            {
                // The street under a room is dimmed and not gone, but anybody walking it would walk over the room
                shown = Math.Clamp((shown - _rooms.Dim) / (1.0f - _rooms.Dim), 0.0f, 1.0f);
            }
            villager.Sprite.Tint = new Vector4(1.0f, 1.0f, 1.0f, shown);
            villager.Sprite.Enabled = shown > 0.0f;

            if (villager.Walking)
            {
                villager.Walked += dt / STEP_TIME;
                if (villager.Walked >= 1.0f)
                {
                    villager.Walked = 1.0f;
                    villager.Walking = false;
                    villager.Rest = REST_LEAST + Random.Shared.NextSingle() * (REST_MOST - REST_LEAST);
                }

                villager.Position = Vector2.Lerp(villager.From, villager.To, villager.Walked);
                villager.Body.Position = villager.Position;
            }
            else if (villager.Wanders && shown > 0.0f && (villager.Rest -= dt) <= 0.0f)
            {
                villager.Rest = REST_LEAST;
                TryStep(villager);
            }

            // A hop a step, and which of the three pictures by the way they are going
            float hop = villager.Walking ? MathF.Sin(villager.Walked * MathF.PI) * 1.5f : 0.0f;
            villager.Sprite.Transform.Position = villager.Position + Feet + new Vector2(0.0f, hop);
            villager.Sprite.SetAnimation(villager.Facing.Y < -0.5f ? "down" : villager.Facing.Y > 0.5f ? "up" : "side");
            villager.Sprite.Flipped = villager.Facing.X < -0.5f;
        }
    }

    /// <summary>Helper method to have a villager take a step a random way, if there is a free tile that way, and face it either way.</summary>
    private void TryStep(Villager villager)
    {
        int pick = Random.Shared.Next(4);
        Vector2 way = pick switch { 0 => Vector2.UnitX, 1 => -Vector2.UnitX, 2 => Vector2.UnitY, _ => -Vector2.UnitY };
        villager.Facing = way;

        TileMap map = villager.Room.Map;
        Vector2 target = villager.Position + way * map.TileSize;

        // On the map and not in a wall, not too far from home, not in a doorway, and nobody standing there already
        var (x, y) = map.WorldToTile(target);
        if (!map.Contains(x, y) || villager.Paths.IsSolid(x, y)) return;

        Vector2 home = villager.Object.Position;
        if (MathF.Abs(target.X - home.X) > LEASH * map.TileSize.X || MathF.Abs(target.Y - home.Y) > LEASH * map.TileSize.Y) return;

        // By index, a foreach over the doors as an interface makes an enumerator on the heap every step
        IReadOnlyList<TileMapDoor> doors = villager.Room.Doors;
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors[i].Contains(target)) return;
        }

        if (villager.Awake && Vector2.Distance(target, _walker.Position) < CELL) return;

        foreach (Villager other in _villagers)
        {
            if (other == villager || other.Room != villager.Room) continue;
            if (Vector2.Distance(other.Walking ? other.To : other.Position, target) < CELL * 0.75f) return;
        }

        villager.From = villager.Position;
        villager.To = target;
        villager.Walked = 0.0f;
        villager.Walking = true;
    }
}

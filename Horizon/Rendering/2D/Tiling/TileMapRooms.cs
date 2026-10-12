using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Rendering.Lighting;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// A map and the maps behind its doors, walked between without a cut. A town is one map, the inside of every
/// building another, and a door is an object of either that names the map behind it. Step into one and the map
/// behind it is laid over the one you are in, placed so that you are standing just inside its own door back,
/// and faded up while the one you were in fades down to a shadow of itself underneath. Step onto the doormat
/// inside and it all goes back the other way. The camera never cuts and nobody is loaded in, the maps are all
/// read as this is made.
/// <code>
/// rooms = new TileMapRooms("Assets/maps/town.tmx") { Lighting = world, Ground = ground };
/// world.AddEntity(rooms);
/// world.AddEntity(actors);
/// world.AddEntity(rooms.Foreground);
///
/// rooms.Entered += (room, left, door) => hero.Teleport(door.Other!.Landing);
///
/// // every update
/// rooms.Walk(hero.Position);
/// </code>
/// A door is any object of a map with a <c>door</c> property that is a file (the map it leads to), which is how
/// Tiled's own file property reads, and a game is free to give it a class and whatever else as well.
/// <list type="bullet">
/// <item><c>door</c> (file) the map behind it, from the folder of the map the door is in (<c>house.tmx</c>, <c>../inside/house.tmx</c>), never an absolute path.</item>
/// <item><c>arrive</c> (string) the name of the door of that map to come out of. Left out, it is the first door there that leads back to this map, which is the usual thing.</item>
/// <item><c>direction</c> (string) which way one walks through it, <c>up</c>, <c>down</c>, <c>left</c> or <c>right</c>, so that brushing past a door sideways doesn't go through it. Left out, any way does.</item>
/// </list>
/// The box of the door (a rectangle, or a cell of the map round a point) is what has to be stepped into, and
/// stepping into it counts once, until it is left and stepped into again. Make it the door and the step in front
/// of it, somebody walking up a path that is a tile wide into a doorway that is a tile wide bumps the door frame
/// as often as not and never gets their middle into the one tile. Whoever comes out of a door stands a step
/// clear of it (<see cref="TileMapDoor.Landing"/>), so stepping back in is going back. See
/// <see cref="TileMapDoor"/> and <see cref="TileMapRoom"/> for what there is to ask.
/// <para>
/// What is given to it is done for every room as it is entered and left. The colliders of the room entered are
/// put onto <see cref="Ground"/> and those of the room left taken off (the walls of the house are still there
/// underneath, and they are not what a room wants to bump into), and the occlusion of the room entered becomes
/// the one of <see cref="Lighting"/>, so its walls are what its lamps are blocked by. What a room has in it that
/// isn't the map (its lamps, whoever lives there) is the game's, and <see cref="Entered"/> is when to see to it.
/// All of it on the simulation thread, the fades are tweens of this entity.
/// </para>
/// </summary>
public sealed class TileMapRooms : GameObject
{
    /// <summary>The file property of an object that makes it a door, the map behind it.</summary>
    public const string DOOR_PROPERTY = "door";
    public const string ARRIVE_PROPERTY = "arrive";
    public const string DIRECTION_PROPERTY = "direction";

    private readonly List<TileMapRoom> rooms = [];

    // Where whoever walks the doors was last seen, for telling stepping into a door from standing in it
    private Vector2 lastWalked;
    private bool walked;

    // Whether the colliders and the occlusion of the room everybody starts in have been handed over
    private bool settled;

    /// <summary>The first map, the one everything else is behind a door of.</summary>
    public TileMapRoom Outside { get; }

    /// <summary>The room whoever walks the doors is in right now.</summary>
    public TileMapRoom Current { get; private set; }

    /// <summary>Every room, the first first and then in the order they were found behind doors.</summary>
    public IReadOnlyList<TileMapRoom> Rooms => rooms;

    /// <summary>How long (in seconds) a room takes to fade up, and the one left to fade down.</summary>
    public float FadeTime { get; set; } = 0.4f;

    /// <summary>
    /// How much of the room that was left shows through underneath the one entered, 0 for none of it and 1 for all
    /// of it. A quarter leaves the street there in the dark round the edges of a room, which is what says where you are.
    /// </summary>
    public float Dim { get; set; } = 0.25f;

    /// <summary>The renderer whose occlusion follows the room, null to leave the lighting alone.</summary>
    public DeferredRenderer2D? Lighting { get; set; }

    /// <summary>The body the colliders of the room go on, null for no colliders at all.</summary>
    public PhysicsBodyComponent2D? Ground { get; set; }

    /// <summary>
    /// Draws the foreground layers of every room that is showing. Add it after whatever walks about the rooms,
    /// the way <see cref="TileMap.Foreground"/> is added. Left out, the rooms draw those layers in their place.
    /// </summary>
    public TileMapRoomsForeground Foreground { get; }

    /// <summary>
    /// Called as a door is gone through, on the simulation thread, with the room entered, the one left and the door
    /// gone through. <see cref="TileMapDoor.Other"/> of that is the door come out of, and its
    /// <see cref="TileMapDoor.Landing"/> is where whoever walked through stands now (which is where they were,
    /// unless the room was showing already and so lay where it lay), put them there.
    /// </summary>
    public event Action<TileMapRoom, TileMapRoom, TileMapDoor>? Entered;

    /// <param name="path">The first map, read along with every map a door of it leads to, and theirs.</param>
    /// <param name="origin">Where the bottom left corner of the first map goes in the world.</param>
    public TileMapRooms(string path, Vector2 origin = default)
    {
        Name = "Tile Map Rooms";
        Foreground = new TileMapRoomsForeground(this);

        Outside = Current = Load(path, origin);
        Outside.Map.Opacity = 1.0f;

        // Every door leads somewhere, and that somewhere has doors of its own. Maps are read once each, a house
        // with two doors to the street is one house. A map is known by its path from where the game runs, the
        // way it was asked for, never by an absolute one, so a save file or a log that names a room is good on
        // any machine
        for (int i = 0; i < rooms.Count; i++)
        {
            foreach (TileMapDoor door in rooms[i].Doors)
            {
                TileMapRoom? behind = rooms.Find(room => string.Equals(room.Path, door.TargetPath, StringComparison.OrdinalIgnoreCase));
                if (behind is null)
                {
                    behind = Load(door.TargetPath, origin);
                    behind.Map.Opacity = 0.0f;
                }

                door.Leads = behind;
            }
        }

        // Now that every room is there, every door can find the one at its far end
        foreach (TileMapRoom room in rooms)
        {
            foreach (TileMapDoor door in room.Doors)
                door.Other = door.FindOther();
        }
    }

    private TileMapRoom Load(string path, Vector2 origin)
    {
        path = Tidy(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"A door leads to '{path}', which isn't there.");

        var room = new TileMapRoom(this, TileMap.Load(path, origin: origin));
        rooms.Add(room);

        // The maps are drawn in the order they were found, the first underneath. Their foregrounds go to the
        // foreground, which draws them in the same order, and a map whose foreground is taken draws without it
        AddEntity(room.Map);
        Foreground.AddEntity(room.Map.Foreground);
        return room;
    }

    /// <summary>
    /// The door of the room that is current which a point is in, null for none. Whether it is being stepped into or
    /// stood in is not asked here, see <see cref="Walk"/> for that.
    /// </summary>
    public TileMapDoor? DoorAt(Vector2 position)
    {
        // By index, a foreach over the list as an interface boxes its enumerator, and this is asked every tick
        List<TileMapDoor> doors = Current.DoorList;
        for (int i = 0; i < doors.Count; i++)
        {
            if (doors[i].Contains(position)) return doors[i];
        }

        return null;
    }

    /// <summary>
    /// Tells the rooms where whoever walks their doors is, every update. The moment they step into a door of the
    /// room they are in, going the way it is walked through, they go through it (see <see cref="Enter"/>).
    /// </summary>
    /// <returns>The door gone through this update, null for none.</returns>
    public TileMapDoor? Walk(Vector2 position)
    {
        Vector2 moved = walked ? position - lastWalked : Vector2.Zero;
        lastWalked = position;
        walked = true;

        TileMapDoor? through = null;
        List<TileMapDoor> doors = Current.DoorList;
        for (int i = 0; i < doors.Count; i++)
        {
            TileMapDoor door = doors[i];
            door.StoodIn = door.Contains(position);
            if (!door.StoodIn)
            {
                door.Fired = false;
                continue;
            }

            // In it, and going the way it is walked through if it minds, which may be a while after stepping
            // into its box from the side. Once, until it is left and come back to
            if (door.Fired || through is not null) continue;
            if (door.Direction != Vector2.Zero && Vector2.Dot(moved, door.Direction) <= 0.02f) continue;

            door.Fired = true;
            through = door;
        }

        if (through is not null)
            Enter(through, position);

        return through;
    }

    /// <summary>
    /// Goes through a door of the room that is current, from where whoever goes through it is standing. The room
    /// behind it is placed so that the landing of the door come out of is that very spot, unless the room is
    /// showing already and so lies where it lies, and faded up, the room left fades down. Where to stand now is
    /// the <see cref="TileMapDoor.Landing"/> of <see cref="TileMapDoor.Other"/>, which is where they were in the
    /// first case and somewhere else in the second.
    /// </summary>
    /// <param name="at">Where whoever goes through the door is, the middle of the door if nobody says.</param>
    public void Enter(TileMapDoor door, Vector2? at = null)
    {
        if (door.Leads is not { } into || door.Other is not { } arrival)
            return;

        TileMapRoom left = Current;
        if (into == left)
            return;

        // Going back out, the room on top goes and the one it lay over is the room again. Going in, the room
        // entered goes on top of the one left, placed first if it isn't showing already (going out of a house
        // by another door than the one in, the street is where it was and whoever walked out has to be told where)
        if (left.Under == into)
        {
            left.Under = null;
        }
        else
        {
            if (!into.IsShowing)
                into.PlaceDoorAt(arrival, (at ?? door.Position) + arrival.Position - arrival.Landing);

            into.Under = left;
        }

        // The room entered fades up, the ones it lies over (the one left, the street under that) fade down to
        // what shows through, and anything else still showing, a room left a moment ago, halfway out, goes
        foreach (TileMapRoom room in rooms)
        {
            if (room == into) Fade(room, 1.0f);
            else if (into.LiesOver(room)) Fade(room, Dim);
            else if (room.IsShowing) Fade(room, 0.0f);
        }

        // Standing a step clear of the far door, and not going straight back through it
        arrival.StoodIn = false;
        arrival.Fired = false;
        lastWalked = arrival.Landing;

        Current = into;
        Settle(left, into);
        Entered?.Invoke(into, left, door);
    }

    private void Fade(TileMapRoom room, float to)
    {
        TileMap map = room.Map;
        if (FadeTime <= 0.0f)
        {
            map.Opacity = to;
            return;
        }

        Tweens.Play(Tween.To(() => map.Opacity, value => map.Opacity = value, to, FadeTime).SetEasing(Easing.OutQuad), room);
    }

    /// <summary>Helper method to hand the colliders and the occlusion of the room entered over, and take the room left's back.</summary>
    private void Settle(TileMapRoom? left, TileMapRoom into)
    {
        if (Ground is { } ground)
        {
            if (left is not null)
            {
                foreach (IPhysicsFixture fixture in left.Fixtures)
                    ground.DynamicFixtures.Remove(fixture);
                left.Fixtures.Clear();
            }

            foreach (TileMapBox box in into.Map.BuildColliders())
                into.Fixtures.Add(ground.CreateRectangularFixture(box.Min, box.Size, tag: "room"));
        }

        if (Lighting is { } lighting)
            lighting.Occlusion = into.Occlusion;
    }

    public override void UpdateState(float dt)
    {
        // The room everybody starts in gets its colliders and its occlusion once there is something to give them to
        if (!settled && (Ground is not null || Lighting is not null))
        {
            settled = true;
            Settle(null, Current);
        }

        base.UpdateState(dt);
    }

    protected override void DisposeOther()
    {
        foreach (TileMapRoom room in rooms)
            room.Release();

        base.DisposeOther();
    }

    /// <summary>
    /// Where a file a map names is, from where the game runs. The file is named from the map's own folder, the
    /// way Tiled writes a file property, and the map from wherever it was asked for, so the two are put together
    /// and the dots and double dots folded up, with nothing made absolute along the way. Forward slashes, every
    /// platform reads those.
    /// </summary>
    public static string Resolve(string mapPath, string file)
    {
        string directory = System.IO.Path.GetDirectoryName(mapPath) ?? string.Empty;
        return Tidy(directory.Length > 0 ? directory + "/" + file : file);
    }

    /// <summary>Helper method to fold the dots out of a path and have it read with forward slashes, without asking the file system anything.</summary>
    private static string Tidy(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        // A path that started at the root (or a drive) keeps its first slash
        string tidy = string.Join('/', parts);
        return path.StartsWith('/') || path.StartsWith('\\') ? "/" + tidy : tidy;
    }

    /// <summary>What a direction property says, as a unit vector in the world, or nothing for a door that doesn't mind.</summary>
    public static Vector2 DirectionOf(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "up" or "north" => Vector2.UnitY,
        "down" or "south" => -Vector2.UnitY,
        "left" or "west" => -Vector2.UnitX,
        "right" or "east" => Vector2.UnitX,
        _ => Vector2.Zero
    };
}

/// <summary>
/// One map of a <see cref="TileMapRooms"/>, with its doors, and what is made of it as it is entered.
/// </summary>
public sealed class TileMapRoom
{
    private readonly List<TileMapDoor> doors = [];
    private OcclusionMap2D? occlusion;

    internal TileMapRoom(TileMapRooms rooms, TileMap map)
    {
        Rooms = rooms;
        Map = map;

        foreach (TileMapObject found in map.Objects)
        {
            if (found.Properties.Has(TileMapRooms.DOOR_PROPERTY))
                doors.Add(new TileMapDoor(this, found));
        }
    }

    public TileMapRooms Rooms { get; }

    public TileMap Map { get; }

    /// <summary>The file the map is, from where the game runs, the way it was asked for.</summary>
    public string Path => Map.Path;

    /// <summary>What the map calls itself (a <c>name</c> property of the map), or the name of its file.</summary>
    public string Name => Map.Properties.GetString("name", System.IO.Path.GetFileNameWithoutExtension(Map.Path));

    /// <summary>The doors of the map, in the order its objects come.</summary>
    public IReadOnlyList<TileMapDoor> Doors => doors;

    /// <summary>The same as the list it is, for the walks that go over it every tick without boxing an enumerator.</summary>
    internal List<TileMapDoor> DoorList => doors;

    /// <summary>The room this one was entered from and lies over, null for one nobody is in or the first.</summary>
    public TileMapRoom? Under { get; internal set; }

    /// <summary>Whether any of the map is on screen, fading or not.</summary>
    public bool IsShowing => Map.Opacity > 0.0f;

    /// <summary>Whether it is the room whoever walks the doors is in.</summary>
    public bool IsCurrent => Rooms.Current == this;

    /// <summary>
    /// What blocks light in the room, by the shape of its tiles (<see cref="TileMap.CreateOcclusion"/>), made the
    /// first time it is asked for and again if the room is moved. The rooms dispose of it.
    /// </summary>
    public OcclusionMap2D Occlusion => occlusion ??= Map.CreateOcclusion();

    /// <summary>The colliders the room has on the ground right now, see <see cref="TileMapRooms.Ground"/>.</summary>
    internal List<IPhysicsFixture> Fixtures { get; } = [];

    /// <summary>Whether another room is one of those this one lies over, the one it was entered from, the one that was entered from, and so on down.</summary>
    public bool LiesOver(TileMapRoom other)
    {
        for (TileMapRoom? under = Under; under is not null; under = under.Under)
        {
            if (under == other) return true;
        }

        return false;
    }

    /// <summary>A door of the room by its name (the case doesn't matter), null for none.</summary>
    public TileMapDoor? FindDoor(string name) =>
        doors.Find(door => string.Equals(door.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moves the map so that one of its doors has its middle at a point of the world. Everything the map is
    /// asked (where its objects are, its colliders) follows, and the occlusion is made again next time.
    /// </summary>
    public void PlaceDoorAt(TileMapDoor door, Vector2 position)
    {
        Vector2 shift = position - door.Position;
        Map.Origin += shift;
        Map.ParallaxOrigin += shift;

        occlusion?.Dispose();
        occlusion = null;
    }

    internal void Release()
    {
        occlusion?.Dispose();
        occlusion = null;
    }

    public override string ToString() => Name;
}

/// <summary>
/// A door of a <see cref="TileMapRoom"/>, an object of its map with a <c>door</c> property. Where it is follows
/// the room about.
/// </summary>
public sealed class TileMapDoor
{
    internal TileMapDoor(TileMapRoom room, TileMapObject found)
    {
        Room = room;
        Object = found;
        Target = found.Properties.GetString(TileMapRooms.DOOR_PROPERTY);
        TargetPath = TileMapRooms.Resolve(room.Path, Target);
        ArriveAt = found.Properties.GetString(TileMapRooms.ARRIVE_PROPERTY);
        Direction = TileMapRooms.DirectionOf(found.Properties.GetString(TileMapRooms.DIRECTION_PROPERTY));
    }

    /// <summary>The room the door is in.</summary>
    public TileMapRoom Room { get; }

    /// <summary>The object of the map the door is.</summary>
    public TileMapObject Object { get; }

    public string Name => Object.Name;

    /// <summary>The map behind the door, as the map wrote it, which is from the map's own folder.</summary>
    public string Target { get; }

    /// <summary>The same from where the game runs, see <see cref="TileMapRooms.Resolve"/>, which is what the room behind it goes by.</summary>
    public string TargetPath { get; }

    /// <summary>The name of the door of that map to come out of, empty for the first one that leads back.</summary>
    public string ArriveAt { get; }

    /// <summary>Which way the door is walked through, a unit vector, or zero for any way.</summary>
    public Vector2 Direction { get; }

    /// <summary>The room behind the door.</summary>
    public TileMapRoom? Leads { get; internal set; }

    /// <summary>The door of that room to come out of, which is where whoever goes through this one stands afterwards.</summary>
    public TileMapDoor? Other { get; internal set; }

    /// <summary>Whether whoever walks the doors is standing in it, as of the last time the rooms were told where they are.</summary>
    public bool StoodIn { get; internal set; }

    /// <summary>Whether it has been gone through since it was last stepped into, so that it isn't gone through twice.</summary>
    internal bool Fired { get; set; }

    /// <summary>The middle of the door in the world.</summary>
    public Vector2 Position => Object.Position;

    /// <summary>
    /// Where whoever comes out of the door stands, a step clear of it against the way it is walked through (in
    /// front of a door that is walked up into, above a doormat that is walked down onto), so that stepping into
    /// it again is going back the way they came. The middle of a door that doesn't mind which way.
    /// </summary>
    public Vector2 Landing
    {
        get
        {
            if (Direction == Vector2.Zero) return Position;

            // Half the box along the way through, and a third of a tile more to be clear of it
            var (_, size) = Box;
            float along = MathF.Abs(Direction.X) * size.X + MathF.Abs(Direction.Y) * size.Y;
            float clear = MathF.Abs(Direction.X) * Room.Map.TileSize.X + MathF.Abs(Direction.Y) * Room.Map.TileSize.Y;
            return Position - Direction * (along * 0.5f + clear * 0.35f);
        }
    }

    /// <summary>The box of the door in the world, a cell of the map round a point. Its bottom left corner and its size.</summary>
    public (Vector2 Min, Vector2 Size) Box
    {
        get
        {
            Vector2 size = Object.Size;
            if (size.X <= 0.0f || size.Y <= 0.0f) size = Room.Map.TileSize;
            return (Object.Position - size / 2.0f, size);
        }
    }

    /// <summary>Whether a point of the world is in the door.</summary>
    public bool Contains(Vector2 position)
    {
        var (min, size) = Box;
        return position.X >= min.X && position.Y >= min.Y && position.X < min.X + size.X && position.Y < min.Y + size.Y;
    }

    /// <summary>Helper method to find the door at the far end, by its name if this one says, else the first one over there that leads back here.</summary>
    internal TileMapDoor? FindOther()
    {
        if (Leads is not { } behind)
            return null;

        if (ArriveAt.Length > 0)
            return behind.FindDoor(ArriveAt);

        foreach (TileMapDoor door in behind.Doors)
        {
            if (door.Leads == Room) return door;
        }

        return null;
    }

    public override string ToString() => $"{Name} of {Room.Name}, to {Leads?.Name ?? Target}";
}

/// <summary>
/// The foreground layers of the rooms that are showing, see <see cref="TileMapRooms.Foreground"/>. The maps'
/// own foregrounds are its children, drawn in the order the maps are.
/// </summary>
public sealed class TileMapRoomsForeground : GameObject
{
    internal TileMapRoomsForeground(TileMapRooms rooms)
    {
        Name = "Tile Map Rooms Foreground";
        Rooms = rooms;
    }

    public TileMapRooms Rooms { get; }
}

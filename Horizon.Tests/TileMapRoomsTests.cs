using System.Numerics;

using Horizon.Rendering.Tiling;

namespace Horizon.Tests;

/// <summary>
/// Two tiny maps in a temp folder, a street with a door up into a house and the house with its doormat back
/// down to the street, and the rooms walked between. No window and no GPU, the maps are read and placed and
/// the fades are tweens ticked by hand.
/// </summary>
public class TileMapRoomsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rooms-tests-" + Guid.NewGuid().ToString("N"));

    public TileMapRoomsTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "tiles.tsx"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <tileset version="1.10" name="tiles" tilewidth="16" tileheight="16" tilecount="4" columns="2">
             <image source="tiles.png" width="32" height="32"/>
            </tileset>
            """);

        // The street, 10 by 10, a wall of tiles along the top row and a door in the middle of the second
        File.WriteAllText(Path.Combine(folder, "street.tmx"), Map(10, 10, "Street", Rows(10, 10, (x, y) => y == 0 ? 1 : 0), """
              <object id="1" name="house" type="door" x="64" y="16" width="16" height="16">
               <properties>
                <property name="door" type="file" value="house.tmx"/>
                <property name="arrive" value="out"/>
                <property name="direction" value="up"/>
               </properties>
              </object>
              <object id="2" name="shed" type="door" x="128" y="48" width="16" height="16">
               <properties>
                <property name="door" type="file" value="house.tmx"/>
               </properties>
              </object>
            """));

        // The house, 6 by 4, walls round the top and the doormat in the bottom row
        File.WriteAllText(Path.Combine(folder, "house.tmx"), Map(6, 4, "House", Rows(6, 4, (x, y) => y == 0 ? 2 : 0), """
              <object id="1" name="out" type="door" x="32" y="48" width="16" height="16">
               <properties>
                <property name="door" type="file" value="street.tmx"/>
                <property name="direction" value="down"/>
               </properties>
              </object>
            """));
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    private static string Rows(int width, int height, Func<int, int, int> tile)
    {
        var rows = new List<string>();
        for (int y = 0; y < height; y++)
            rows.Add(string.Join(",", Enumerable.Range(0, width).Select(x => tile(x, y))));
        return string.Join(",\n", rows);
    }

    private static string Map(int width, int height, string name, string tiles, string objects) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <map version="1.10" orientation="orthogonal" renderorder="right-down" width="{width}" height="{height}" tilewidth="16" tileheight="16" infinite="0" nextlayerid="3" nextobjectid="3">
         <properties>
          <property name="name" value="{name}"/>
         </properties>
         <tileset firstgid="1" source="tiles.tsx"/>
         <layer id="1" name="walls" width="{width}" height="{height}">
          <properties>
           <property name="IsCollidable" type="bool" value="true"/>
          </properties>
          <data encoding="csv">
        {tiles}
        </data>
         </layer>
         <objectgroup id="2" name="things">
        {objects}
         </objectgroup>
        </map>
        """;

    private TileMapRooms Rooms() => new(Path.Combine(folder, "street.tmx")) { FadeTime = 0.2f };

    [Fact]
    public void Every_map_behind_a_door_is_read_and_every_door_knows_its_far_end()
    {
        var rooms = Rooms();

        Assert.Equal(2, rooms.Rooms.Count);
        Assert.Equal("Street", rooms.Outside.Name);
        Assert.Same(rooms.Outside, rooms.Current);

        TileMapRoom house = rooms.Rooms[1];
        Assert.Equal("House", house.Name);
        Assert.Equal(2, rooms.Outside.Doors.Count);
        Assert.Single(house.Doors);

        // Named, and not named, both find the doormat. The doormat finds the first door back, which is the house's
        TileMapDoor front = rooms.Outside.FindDoor("house")!;
        TileMapDoor shed = rooms.Outside.FindDoor("shed")!;
        TileMapDoor mat = house.Doors[0];
        Assert.Same(house, front.Leads);
        Assert.Same(mat, front.Other);
        Assert.Same(mat, shed.Other);
        Assert.Same(front, mat.Other);

        Assert.Equal(Vector2.UnitY, front.Direction);
        Assert.Equal(-Vector2.UnitY, mat.Direction);
        Assert.Equal(Vector2.Zero, shed.Direction);

        // The house starts out not showing, the street showing
        Assert.Equal(1.0f, rooms.Outside.Map.Opacity);
        Assert.Equal(0.0f, house.Map.Opacity);
    }

    [Fact]
    public void Stepping_up_into_the_door_puts_the_house_over_it_with_the_doormat_where_you_stand()
    {
        var rooms = Rooms();
        TileMapRoom house = rooms.Rooms[1];
        TileMapDoor front = rooms.Outside.FindDoor("house")!;

        var entered = new List<(TileMapRoom Into, TileMapRoom Left, TileMapDoor Through)>();
        rooms.Entered += (into, left, through) => entered.Add((into, left, through));

        // Walking up the street towards the door, a step a tick, from below it
        Vector2 under = front.Position - new Vector2(0.0f, 20.0f);
        Assert.Null(rooms.Walk(under));
        Assert.Null(rooms.Walk(under + new Vector2(0.0f, 8.0f)));
        TileMapDoor? through = rooms.Walk(front.Position);

        Assert.Same(front, through);
        Assert.Same(house, rooms.Current);
        Assert.Single(entered);
        Assert.Same(house, entered[0].Into);
        Assert.Same(rooms.Outside, entered[0].Left);

        // The house was moved so that whoever walked in is standing just inside its doormat, which is a step up
        // from it (the mat is walked down onto), and hasn't moved at all
        TileMapDoor mat = house.Doors[0];
        Assert.Equal(front.Position.X, mat.Landing.X, 3);
        Assert.Equal(front.Position.Y, mat.Landing.Y, 3);
        Assert.True(mat.Position.Y < mat.Landing.Y);
        Assert.Same(rooms.Outside, house.Under);

        // And standing there doesn't go straight back out, nor does shuffling about
        Assert.Null(rooms.Walk(mat.Landing));
        Assert.Null(rooms.Walk(mat.Landing + new Vector2(2.0f, 0.0f)));
        Assert.Same(house, rooms.Current);

        // Stepping down onto the mat does
        Assert.Same(mat, rooms.Walk(mat.Position));
        Assert.Same(rooms.Outside, rooms.Current);
    }

    [Fact]
    public void The_fades_go_the_way_they_should_and_back()
    {
        var rooms = Rooms();
        TileMapRoom street = rooms.Outside, house = rooms.Rooms[1];
        TileMapDoor front = street.FindDoor("house")!;

        rooms.Walk(front.Position - new Vector2(0.0f, 10.0f));
        rooms.Walk(front.Position);

        // Partway through the fade the house is coming up and the street going down, at the end the street is
        // what shows through and no more
        Tick(rooms, 0.1f);
        Assert.InRange(house.Map.Opacity, 0.01f, 0.99f);
        Assert.InRange(street.Map.Opacity, rooms.Dim + 0.01f, 0.99f);

        Tick(rooms, 0.3f);
        Assert.Equal(1.0f, house.Map.Opacity, 3);
        Assert.Equal(rooms.Dim, street.Map.Opacity, 3);

        // Off the doormat and back down onto it, which is the way out, and the house goes altogether
        TileMapDoor mat = house.Doors[0];
        rooms.Walk(mat.Position + new Vector2(0.0f, 20.0f));
        rooms.Walk(mat.Position + new Vector2(0.0f, 12.0f));
        Assert.Same(mat, rooms.Walk(mat.Position));
        Assert.Same(street, rooms.Current);
        Assert.Null(house.Under);

        Tick(rooms, 0.4f);
        Assert.Equal(1.0f, street.Map.Opacity, 3);
        Assert.Equal(0.0f, house.Map.Opacity, 3);
    }

    [Fact]
    public void A_door_that_is_walked_through_one_way_is_not_gone_through_sideways()
    {
        var rooms = Rooms();
        TileMapDoor front = rooms.Outside.FindDoor("house")!;

        // Along the street, through the door's box from the side, is not going in, and standing in it isn't either
        rooms.Walk(front.Position - new Vector2(20.0f, 0.0f));
        Assert.Null(rooms.Walk(front.Position));
        Assert.Null(rooms.Walk(front.Position));
        Assert.Same(rooms.Outside, rooms.Current);

        // Turning and walking up while in it is, nobody lines themselves up with a doorway before they step in
        Assert.Same(front, rooms.Walk(front.Position + new Vector2(0.0f, 2.0f)));
        Assert.Same(rooms.Rooms[1], rooms.Current);

        // Back out, and up into it from below is as well
        TileMapDoor mat = rooms.Rooms[1].Doors[0];
        rooms.Walk(mat.Position);
        Assert.Same(rooms.Outside, rooms.Current);
        rooms.Walk(front.Position - new Vector2(0.0f, 20.0f));
        Assert.Same(front, rooms.Walk(front.Position));

        // The shed door doesn't mind which way, and lands on the same doormat (named by nobody, found as the door back)
        var again = Rooms();
        TileMapDoor shed = again.Outside.FindDoor("shed")!;
        again.Walk(shed.Position - new Vector2(20.0f, 0.0f));
        Assert.Same(shed, again.Walk(shed.Position));
        Assert.Equal(shed.Position.X, again.Rooms[1].Doors[0].Landing.X, 3);
        Assert.Equal(shed.Position.Y, again.Rooms[1].Doors[0].Landing.Y, 3);
    }

    [Fact]
    public void A_door_names_its_map_from_its_own_folder_and_nothing_is_made_absolute()
    {
        Assert.Equal("Assets/maps/house.tmx", TileMapRooms.Resolve("Assets/maps/town.tmx", "house.tmx"));
        Assert.Equal("Assets/inside/house.tmx", TileMapRooms.Resolve("Assets/maps/town.tmx", "../inside/house.tmx"));
        Assert.Equal("Assets/maps/house.tmx", TileMapRooms.Resolve("Assets\\maps\\town.tmx", ".\\house.tmx"));
        Assert.Equal("house.tmx", TileMapRooms.Resolve("town.tmx", "house.tmx"));
        Assert.Equal("../house.tmx", TileMapRooms.Resolve("town.tmx", "../house.tmx"));

        // What the rooms go by is what the maps say, from where the game runs
        var rooms = Rooms();
        Assert.Equal("house.tmx", rooms.Outside.FindDoor("house")!.Target);
        Assert.EndsWith("/house.tmx", rooms.Outside.FindDoor("house")!.TargetPath);
        Assert.Equal(rooms.Outside.FindDoor("house")!.TargetPath, rooms.Rooms[1].Path);
    }

    [Fact]
    public void Directions_read_as_they_are_written()
    {
        Assert.Equal(Vector2.UnitY, TileMapRooms.DirectionOf("Up"));
        Assert.Equal(-Vector2.UnitY, TileMapRooms.DirectionOf(" down "));
        Assert.Equal(-Vector2.UnitX, TileMapRooms.DirectionOf("left"));
        Assert.Equal(Vector2.UnitX, TileMapRooms.DirectionOf("east"));
        Assert.Equal(Vector2.Zero, TileMapRooms.DirectionOf(""));
        Assert.Equal(Vector2.Zero, TileMapRooms.DirectionOf("sideways"));
    }

    /// <summary>Helper method to move the fades along, a tick at a time the way the engine would.</summary>
    private static void Tick(TileMapRooms rooms, float seconds)
    {
        const float step = 1.0f / 120.0f;
        for (float t = 0.0f; t < seconds; t += step)
            rooms.UpdateState(step);
    }
}

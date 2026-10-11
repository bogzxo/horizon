using System.Numerics;

namespace Horizon.Rendering.Tiling;

/// <summary>How a stop of a path is got to from the one before it.</summary>
public enum TileMapMove
{
    /// <summary>Along the floor.</summary>
    Walk,

    /// <summary>Up or across a gap, with a jump taken from the stop before.</summary>
    Jump,

    /// <summary>Down off a ledge, letting gravity do the work.</summary>
    Drop
}

/// <summary>
/// Somebody who finds their way across a map, as far as the tiles are concerned, how big they are and what they can do.
/// </summary>
/// <param name="Size">How wide and high they are, in the world's units. They need that much room to stand somewhere.</param>
/// <param name="Walks">Whether they walk along the floor at all (something that flies finds a way through the air instead).</param>
/// <param name="JumpHeight">How high (in the world's units) a jump takes their feet, 0 for somebody who can't jump.</param>
/// <param name="JumpDistance">How far across a jump carries them before they are back on the ground, in the world's units.</param>
public readonly record struct TileMapAgent(Vector2 Size, bool Walks = true, float JumpHeight = 0.0f, float JumpDistance = 0.0f);

/// <summary>
/// One stop of a path across a map, where to stand (the feet, on the floor in the middle of the tile) and how to get there.
/// </summary>
/// <param name="Position">Where the feet go, in the world.</param>
/// <param name="X">The column of the tile, the way Tiled counts them.</param>
/// <param name="Y">The row of the tile, the way Tiled counts them.</param>
/// <param name="Move">How this stop is got to from the one before it.</param>
public readonly record struct TileMapPathStep(Vector2 Position, int X, int Y, TileMapMove Move);

/// <summary>
/// Finds the way across a <see cref="TileMap"/> for whoever walks it without a player steering them, a dummy going
/// after the player, a character strolling over to somewhere. The floor is every tile somebody can stand on (one that
/// isn't solid with a solid one under it, and room above for whoever is asking, see <see cref="TileMapAgent"/>) and
/// the way is found over that floor with walks along it, jumps up and across whatever a jump can clear, and drops off
/// its ledges. What is solid is what <see cref="TileMap.BuildColliders"/> makes solid, worked out once when the
/// pathfinder is made (<see cref="TileMap.CreatePathfinder"/>), so a map whose tiles change wants a new one.
/// <code>
/// var paths = map.CreatePathfinder();
/// var walker = new TileMapAgent(new Vector2(28, 70), JumpHeight: 100, JumpDistance: 90);
/// if (paths.TryFindPath(feet, target, walker, steps))
///     // walk to steps[0].Position, jump if steps[0].Move is a Jump, and so on
/// </code>
/// From any thread, once it is made.
/// </summary>
public sealed class TileMapPathfinder
{
    // How far (in tiles) around a spot that isn't on the floor to look for the nearest bit of floor
    private const int SEARCH_RADIUS = 8;

    // How far down (in tiles) a drop is followed before it is given up on
    private const int LONGEST_DROP = 64;

    // A jump costs more than the same way walked, so a flat way is preferred when there is one
    private const float JUMP_COST = 2.0f;
    private const float DROP_COST = 1.25f;

    private readonly bool[] solid;
    private readonly int width, height, left, top;

    /// <summary>The map the ways are found on.</summary>
    public TileMap Map { get; }

    /// <summary>Makes a pathfinder from the solid tiles of a map as they are right now. See <see cref="TileMap.CreatePathfinder"/>.</summary>
    /// <param name="isSolid">Decides for every tile instead, the way <see cref="TileMap.BuildColliders"/> takes one.</param>
    public TileMapPathfinder(TileMap map, Func<TileMapCell, bool>? isSolid = null)
    {
        Map = map;
        width = map.Width;
        height = map.Height;
        left = map.Left;
        top = map.Top;
        solid = new bool[width * height];

        foreach (TileMapLayer layer in map.Layers)
        {
            if (layer.Kind != TileMapLayerKind.Tiles)
                continue;

            foreach (TileMapCell cell in layer.Tiles())
            {
                bool wanted = isSolid?.Invoke(cell) ?? (layer.IsCollidable || cell.Tile.Properties.GetBool("collidable") || cell.Tile.Collision.Count > 0);
                if (wanted)
                    solid[(cell.Y - top) * width + (cell.X - left)] = true;
            }
        }
    }

    /// <summary>Whether a tile is solid. Everything off the map is open air.</summary>
    public bool IsSolid(int x, int y)
    {
        int column = x - left, row = y - top;
        return column >= 0 && column < width && row >= 0 && row < height && solid[row * width + column];
    }

    /// <summary>
    /// Whether somebody can stand on a tile. It isn't solid, the one under it is, and there is room above for them.
    /// </summary>
    public bool CanStand(int x, int y, in TileMapAgent agent)
    {
        if (!Map.Contains(x, y) || IsSolid(x, y) || !IsSolid(x, y + 1))
            return false;

        return HasRoom(x, y, agent);
    }

    /// <summary>Whether there is room for somebody standing in a tile, the tiles they take up are all open.</summary>
    private bool HasRoom(int x, int y, in TileMapAgent agent)
    {
        int rows = Math.Max(1, (int)MathF.Ceiling(agent.Size.Y / Map.TileSize.Y));
        int reach = Math.Max(0, (int)MathF.Ceiling(agent.Size.X * 0.5f / Map.TileSize.X) - 1);

        for (int row = 0; row < rows; row++)
        {
            for (int column = -reach; column <= reach; column++)
            {
                if (IsSolid(x + column, y - row))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Where the feet of somebody standing in a tile are, the middle of its bottom edge.</summary>
    public Vector2 FeetOf(int x, int y) => Map.TileToWorld(x, y) - new Vector2(0.0f, Map.TileSize.Y * 0.5f);

    /// <summary>
    /// The tile of the floor nearest to a spot of the world that somebody can stand on, the floor under the spot
    /// first, then whatever is nearest around it. Null if there is none anywhere near.
    /// </summary>
    public (int X, int Y)? NearestFloor(Vector2 world, in TileMapAgent agent)
    {
        var (x, y) = Map.WorldToTile(world);

        // Straight down first, which is where somebody in the air is going to end up
        for (int drop = 0; drop <= LONGEST_DROP; drop++)
        {
            if (CanStand(x, y + drop, agent)) return (x, y + drop);
            if (IsSolid(x, y + drop)) break;
        }

        // Then the nearest around, ring by ring
        (int X, int Y)? best = null;
        float bestDistance = float.MaxValue;
        for (int radius = 0; radius <= SEARCH_RADIUS && best is null; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius || !CanStand(x + dx, y + dy, agent))
                        continue;

                    float distance = Vector2.DistanceSquared(FeetOf(x + dx, y + dy), world);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (x + dx, y + dy);
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Finds the way from a spot to another over the floor, for somebody of a size who walks and jumps the way the
    /// agent says. Both spots are taken to the nearest floor first (the one under them, if any), so somebody in the
    /// air starts from where they are going to land and a goal in the air is the floor under it.
    /// </summary>
    /// <param name="from">Where the feet are now.</param>
    /// <param name="to">Where they want to be.</param>
    /// <param name="path">Filled in with the stops on the way, the first move first and the goal last. Emptied first.</param>
    /// <returns>False if there is no way, or no floor near either end. The path is empty then.</returns>
    public bool TryFindPath(Vector2 from, Vector2 to, in TileMapAgent agent, List<TileMapPathStep> path)
    {
        path.Clear();

        if (NearestFloor(from, agent) is not { } start || NearestFloor(to, agent) is not { } goal)
            return false;

        if (start == goal)
        {
            path.Add(new TileMapPathStep(FeetOf(goal.X, goal.Y), goal.X, goal.Y, TileMapMove.Walk));
            return true;
        }

        // A* over the floor tiles, the straight distance to the goal for the guess
        var open = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int, int), ((int X, int Y) From, TileMapMove Move)>();
        var cost = new Dictionary<(int, int), float> { [start] = 0.0f };
        var closed = new HashSet<(int, int)>();
        var neighbours = new List<((int X, int Y) Tile, TileMapMove Move, float Cost)>(16);

        open.Enqueue(start, Heuristic(start, goal));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                Unwind(goal, start, cameFrom, path);
                return true;
            }

            if (!closed.Add(current))
                continue;

            neighbours.Clear();
            Neighbours(current.X, current.Y, agent, neighbours);

            float soFar = cost[current];
            foreach (var (tile, move, stepCost) in neighbours)
            {
                if (closed.Contains(tile))
                    continue;

                float through = soFar + stepCost;
                if (cost.TryGetValue(tile, out float known) && known <= through)
                    continue;

                cost[tile] = through;
                cameFrom[tile] = (current, move);
                open.Enqueue(tile, through + Heuristic(tile, goal));
            }
        }

        return false;
    }

    private float Heuristic((int X, int Y) a, (int X, int Y) b) =>
        Vector2.Distance(new Vector2(a.X, a.Y) * Map.TileSize, new Vector2(b.X, b.Y) * Map.TileSize);

    /// <summary>
    /// Helper method for everywhere one can get to from a tile of the floor in one move, the tiles beside it along the
    /// floor, what a jump reaches up and across, and the floor below its ledges.
    /// </summary>
    private void Neighbours(int x, int y, in TileMapAgent agent, List<((int X, int Y) Tile, TileMapMove Move, float Cost)> into)
    {
        Vector2 tile = Map.TileSize;

        int jumpRows = agent.JumpHeight > 0.0f ? (int)MathF.Floor(agent.JumpHeight / tile.Y) : 0;
        int jumpColumns = agent.JumpDistance > 0.0f ? (int)MathF.Floor(agent.JumpDistance / tile.X) : 0;

        foreach (int side in stackalloc int[] { -1, 1 })
        {
            int beside = x + side;

            if (agent.Walks)
            {
                // Along the floor
                if (CanStand(beside, y, agent))
                {
                    into.Add(((beside, y), TileMapMove.Walk, tile.X));
                    continue;
                }

                // Off the ledge, down to whatever is below
                if (!IsSolid(beside, y) && HasRoom(beside, y, agent))
                {
                    for (int drop = 1; drop <= LONGEST_DROP; drop++)
                    {
                        if (IsSolid(beside, y + drop))
                        {
                            if (CanStand(beside, y + drop - 1, agent))
                                into.Add(((beside, y + drop - 1), TileMapMove.Drop, (tile.X + drop * tile.Y) * DROP_COST));
                            break;
                        }
                    }
                }
            }
        }

        if (jumpRows <= 0 && jumpColumns <= 0)
            return;

        // Up and across, every tile of the floor a jump reaches whose way isn't blocked. Straight up is the tile
        // above a ledge, the rest are reached across the gap in between
        for (int dy = -jumpRows; dy <= 0; dy++)
        {
            for (int dx = -jumpColumns; dx <= jumpColumns; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                // Along the floor is walked, not jumped, and a step to the side on the same row that can be walked isn't a jump either
                if (dy == 0 && Math.Abs(dx) == 1 && CanStand(x + dx, y, agent))
                    continue;

                int targetX = x + dx, targetY = y + dy;
                if (!CanStand(targetX, targetY, agent) || !ClearArc(x, y, targetX, targetY, agent))
                    continue;

                float distance = new Vector2(dx * tile.X, dy * tile.Y).Length();
                into.Add(((targetX, targetY), TileMapMove.Jump, distance * JUMP_COST));
            }
        }
    }

    /// <summary>
    /// Helper method to say whether a jump from one tile of the floor to another has the room it needs, the tiles
    /// along the way (over the top of the two, as a jump goes up before it comes down) are open.
    /// </summary>
    private bool ClearArc(int fromX, int fromY, int toX, int toY, in TileMapAgent agent)
    {
        // The jump peaks a tile above the higher of the two ends
        int peak = Math.Min(fromY, toY) - 1;

        int steps = Math.Max(Math.Abs(toX - fromX), 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int x = fromX + (int)MathF.Round((toX - fromX) * t);

            // The arc. From the start up to the peak and down to the end, every column has to be open from where the
            // feet are at that moment up to the head
            float arc = fromY + (toY - fromY) * t - (1.0f - MathF.Abs(t * 2.0f - 1.0f)) * (fromY - peak + 0.0f) * 0.5f;
            int feet = (int)MathF.Floor(arc);

            if (!HasRoom(x, Math.Min(feet, Math.Max(fromY, toY)), agent))
                return false;
        }

        return true;
    }

    private void Unwind((int X, int Y) goal, (int X, int Y) start, Dictionary<(int, int), ((int X, int Y) From, TileMapMove Move)> cameFrom, List<TileMapPathStep> path)
    {
        var at = goal;
        while (at != start)
        {
            var (from, move) = cameFrom[at];
            path.Add(new TileMapPathStep(FeetOf(at.X, at.Y), at.X, at.Y, move));
            at = from;
        }

        path.Reverse();
    }
}

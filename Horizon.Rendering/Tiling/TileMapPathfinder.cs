using System.Numerics;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// How a step of a path is taken.
/// </summary>
public enum TileMapMove
{
    /// <summary>Along the ground (or in any direction, for somebody gravity doesn't pull on).</summary>
    Walk,

    /// <summary>Up and over, to somewhere that can't be walked to.</summary>
    Jump,

    /// <summary>Off an edge and down to whatever is underneath.</summary>
    Fall
}

/// <summary>
/// One stop along a path: where to get to, and how to get there from the stop before.
/// </summary>
/// <param name="Position">Where the stop is in the world. For somebody who walks that is where their feet go, the middle of the top of the tile they end up standing on. For somebody who doesn't it is the middle of the cell.</param>
/// <param name="X">The column of the cell, the way Tiled counts them.</param>
/// <param name="Y">The row of the cell. For somebody who walks it is the row their feet are in, the one above the floor.</param>
public readonly record struct TileMapPathStep(Vector2 Position, int X, int Y, TileMapMove Move);

/// <summary>
/// Who a path is for, which decides where they fit and how they get about.
/// </summary>
/// <param name="Size">How big they are in the world. They only go where all of that is clear of anything solid.</param>
/// <param name="Walks">
/// Whether gravity pulls on them, which is a map seen from the side: they only ever stop where there is floor under
/// them, and get from one bit of floor to another by walking, dropping off an edge or jumping. Without it the map is
/// seen from above and they go wherever nothing is in the way.
/// </param>
/// <param name="JumpHeight">How high somebody who walks can jump, in units of the world. 0 for somebody who can't.</param>
/// <param name="JumpDistance">How far across a jump carries them, in units of the world.</param>
/// <param name="MaxFall">How far they are willing to drop, in units of the world. 0 for as far as the map goes.</param>
/// <param name="Diagonals">Whether somebody gravity doesn't pull on can cut across from corner to corner, never through the corner of anything solid.</param>
public readonly record struct TileMapAgent(
    Vector2 Size, bool Walks = false, float JumpHeight = 0.0f, float JumpDistance = 0.0f, float MaxFall = 0.0f, bool Diagonals = true);

/// <summary>
/// Finds the way from one place of a <see cref="TileMap"/> to another with A*, around whatever is solid: the same tiles
/// <see cref="TileMap.BuildColliders"/> makes boxes of. Made with <see cref="TileMap.CreatePathfinder"/>.
/// <para>
/// It works on the grid of the map. Tiles that are only solid in part (a shape drawn onto them in Tiled's collision
/// editor) count as solid all over, and so does every cell a rectangle of a collidable object layer reaches into.
/// What is solid is read off the map once, when this is made: call <see cref="Refresh"/> after changing its tiles.
/// </para>
/// Nothing in here touches the GPU or anything else of the engine, paths can be found from any thread as long as nobody
/// refreshes at the same time.
/// </summary>
public sealed class TileMapPathfinder
{
    // What a step costs. Walking is the cheapest way to get anywhere, a path only leaves the ground when it has to
    private const float WALK_COST = 1.0f;
    private const float DIAGONAL_COST = 1.4142f;
    private const float FALL_COST = 0.6f;
    private const float JUMP_COST = 2.5f;
    private const float JUMP_HEIGHT_COST = 0.75f;

    // Every cell a jump goes across costs more than walking it would, so a jump starts as late as it can: at the edge of
    // the gap or the foot of the step, not from as far back as it reaches
    private const float JUMP_DISTANCE_COST = 1.5f;

    // How far around a point (in cells) somewhere to stand is looked for, a point is rarely right on the floor
    private const int SNAP_REACH = 6;

    // The most cells a search looks at before it gives up, a map is never so big that a way takes longer to find
    private const int MAX_VISITED = 32768;

    private readonly TileMap _map;
    private readonly Func<TileMapCell, bool>? _isSolid;
    private bool[] _solid = [];

    /// <summary>The map the paths are found on.</summary>
    public TileMap Map => _map;

    internal TileMapPathfinder(TileMap map, Func<TileMapCell, bool>? isSolid)
    {
        _map = map;
        _isSolid = isSolid;
        Refresh();
    }

    /// <summary>
    /// Reads what is solid off the map again, for a map whose tiles have changed since.
    /// </summary>
    public void Refresh() => _solid = _map.BuildSolidGrid(_isSolid);

    /// <summary>
    /// Test if a cell is solid, by its column and row the way Tiled counts them. Everything off the map is: nobody gets
    /// to walk around the outside of it.
    /// </summary>
    public bool IsSolid(int x, int y)
    {
        int index = _map.IndexOf(x, y);
        return index < 0 || _solid[index];
    }

    /// <summary>
    /// Finds the way from one point of the world to another.
    /// </summary>
    /// <param name="from">Where they are. Somebody who walks is put on the nearest floor under (or next to) the point.</param>
    /// <param name="to">Where they want to be, treated the same way.</param>
    /// <param name="path">
    /// Filled with the stops along the way in order, the last one being where they wanted to go (or as near to it as
    /// anybody can stand). Where they start isn't in it. Stretches walked in a straight line are one stop.
    /// </param>
    /// <returns>False if there is no way, the path is empty then.</returns>
    public bool TryFindPath(Vector2 from, Vector2 to, in TileMapAgent agent, List<TileMapPathStep> path)
    {
        path.Clear();

        var shape = new Shape(this, agent);
        if (!shape.TrySnap(from, out var start) || !shape.TrySnap(to, out var goal))
            return false;

        if (start == goal)
        {
            path.Add(new TileMapPathStep(shape.WorldOf(goal), goal.X, goal.Y, TileMapMove.Walk));
            return true;
        }

        var open = new PriorityQueue<(int X, int Y), float>();
        var cameFrom = new Dictionary<(int X, int Y), ((int X, int Y) From, TileMapMove Move)>();
        var cost = new Dictionary<(int X, int Y), float> { [start] = 0.0f };
        var neighbours = new List<((int X, int Y) Cell, float Cost, TileMapMove Move)>();

        open.Enqueue(start, Heuristic(start, goal));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
            {
                Unwind(shape, cameFrom, start, goal, path);
                return true;
            }

            if (cost.Count > MAX_VISITED) break;

            neighbours.Clear();
            shape.Neighbours(current, neighbours);

            float soFar = cost[current];
            foreach (var (cell, step, move) in neighbours)
            {
                float total = soFar + step;
                if (cost.TryGetValue(cell, out float known) && known <= total) continue;

                cost[cell] = total;
                cameFrom[cell] = (current, move);
                open.Enqueue(cell, total + Heuristic(cell, goal));
            }
        }

        return false;
    }

    // As the crow flies. It never guesses more than the way really costs (a fall is the cheapest a cell gets)
    private static float Heuristic((int X, int Y) from, (int X, int Y) to) =>
        (MathF.Abs(from.X - to.X) + MathF.Abs(from.Y - to.Y)) * FALL_COST;

    /// <summary>
    /// Helper method to turn where every cell was reached from into the stops of a path, start to goal. A run of
    /// walking that keeps going the same way is one stop, at its far end.
    /// </summary>
    private static void Unwind(
        in Shape shape,
        Dictionary<(int X, int Y), ((int X, int Y) From, TileMapMove Move)> cameFrom,
        (int X, int Y) start,
        (int X, int Y) goal,
        List<TileMapPathStep> path)
    {
        var cells = new List<((int X, int Y) Cell, (int X, int Y) From, TileMapMove Move)>();
        for (var at = goal; at != start;)
        {
            var (from, move) = cameFrom[at];
            cells.Add((at, from, move));
            at = from;
        }

        cells.Reverse();

        for (int i = 0; i < cells.Count; i++)
        {
            var (cell, from, move) = cells[i];

            // Still walking the same way as the step after this one, which is where the stop goes then
            if (move == TileMapMove.Walk && i + 1 < cells.Count && cells[i + 1].Move == TileMapMove.Walk)
            {
                var next = cells[i + 1].Cell;
                if ((next.X - cell.X, next.Y - cell.Y) == (cell.X - from.X, cell.Y - from.Y)) continue;
            }

            path.Add(new TileMapPathStep(shape.WorldOf(cell), cell.X, cell.Y, move));
        }
    }

    /// <summary>
    /// An agent measured in cells of the map, and everything that depends on how big it is: where it fits, where it can
    /// stand and where it gets to from there.
    /// </summary>
    private readonly struct Shape
    {
        private readonly TileMapPathfinder _finder;
        private readonly bool _walks, _diagonals;

        // How many cells it reaches to the left and right of the one it is in, and how many it is tall
        private readonly int _left, _right, _height;
        private readonly int _jumpHeight, _jumpDistance, _maxFall;

        public Shape(TileMapPathfinder finder, in TileMapAgent agent)
        {
            _finder = finder;
            _walks = agent.Walks;
            _diagonals = agent.Diagonals;

            Vector2 tile = finder._map.TileSize;

            // A hair under a whole number of tiles still fits between them, nobody is that exact about their size
            int width = Math.Max(1, (int)MathF.Ceiling(agent.Size.X / tile.X - 0.25f));
            _height = Math.Max(1, (int)MathF.Ceiling(agent.Size.Y / tile.Y - 0.25f));
            _left = (width - 1) / 2;
            _right = width / 2;

            _jumpHeight = (int)MathF.Floor(agent.JumpHeight / tile.Y);
            _jumpDistance = (int)MathF.Floor(agent.JumpDistance / tile.X);
            _maxFall = agent.MaxFall > 0.0f ? Math.Max(1, (int)MathF.Floor(agent.MaxFall / tile.Y)) : int.MaxValue;
        }

        /// <summary>
        /// Test if the agent fits with its feet (or for one that doesn't walk, its bottom row) in a cell.
        /// </summary>
        public bool Fits(int x, int y)
        {
            for (int row = y - _height + 1; row <= y; row++)
            {
                for (int column = x - _left; column <= x + _right; column++)
                {
                    if (_finder.IsSolid(column, row)) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Test if the agent can stop in a cell. Somebody who walks needs floor right under them for that.
        /// </summary>
        public bool CanStand(int x, int y) => Fits(x, y) && (!_walks || (_finder._map.Contains(x, y + 1) && _finder.IsSolid(x, y + 1)));

        /// <summary>
        /// Where the agent is in the world when it is in a cell. Its feet on the floor if it walks, the middle of the cell if not.
        /// </summary>
        public Vector2 WorldOf((int X, int Y) cell)
        {
            Vector2 middle = _finder._map.TileToWorld(cell.X, cell.Y);
            return _walks ? middle - new Vector2(0.0f, _finder._map.TileSize.Y / 2.0f) : middle;
        }

        /// <summary>
        /// Helper method to find the cell nearest a point that the agent can stop in. Straight down first, which is where
        /// somebody in the air ends up, then further and further to either side.
        /// </summary>
        public bool TrySnap(Vector2 point, out (int X, int Y) cell)
        {
            // A point right on the top edge of the floor belongs to the cell above it, not to the floor
            (int x, int y) = _finder._map.WorldToTile(point + new Vector2(0.0f, _finder._map.TileSize.Y * 0.25f));

            for (int sideways = 0; sideways <= SNAP_REACH; sideways++)
            {
                for (int down = 0; down <= (_walks ? _finder._map.Height : SNAP_REACH); down++)
                {
                    foreach (int column in sideways == 0 ? [x] : (ReadOnlySpan<int>)[x - sideways, x + sideways])
                    {
                        if (CanStand(column, y + down))
                        {
                            cell = (column, y + down);
                            return true;
                        }

                        if (down <= SNAP_REACH && down > 0 && CanStand(column, y - down))
                        {
                            cell = (column, y - down);
                            return true;
                        }
                    }
                }
            }

            cell = default;
            return false;
        }

        public void Neighbours((int X, int Y) from, List<((int X, int Y) Cell, float Cost, TileMapMove Move)> into)
        {
            if (_walks) Walking(from, into);
            else Free(from, into);
        }

        /// <summary>
        /// Helper method for somebody gravity doesn't pull on: the four cells next to them, and the four on the corners
        /// if they cut corners and neither cell they would brush past is solid.
        /// </summary>
        private void Free((int X, int Y) from, List<((int X, int Y) Cell, float Cost, TileMapMove Move)> into)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    bool diagonal = dx != 0 && dy != 0;
                    if (diagonal && (!_diagonals || !Fits(from.X + dx, from.Y) || !Fits(from.X, from.Y + dy))) continue;
                    if (!Fits(from.X + dx, from.Y + dy)) continue;

                    into.Add(((from.X + dx, from.Y + dy), diagonal ? DIAGONAL_COST : WALK_COST, TileMapMove.Walk));
                }
            }
        }

        /// <summary>
        /// Helper method for somebody who walks. A step to either side along the floor, off an edge and down to whatever
        /// is under it, or a jump to any floor within reach that there is a clear way to.
        /// </summary>
        private void Walking((int X, int Y) from, List<((int X, int Y) Cell, float Cost, TileMapMove Move)> into)
        {
            foreach (int direction in (ReadOnlySpan<int>)[-1, 1])
            {
                int x = from.X + direction;
                if (!Fits(x, from.Y)) continue;

                if (CanStand(x, from.Y))
                {
                    into.Add(((x, from.Y), WALK_COST, TileMapMove.Walk));
                    continue;
                }

                // Nothing under it, down they go
                if (TryLand(x, from.Y, out int landed))
                    into.Add(((x, landed), WALK_COST + (landed - from.Y) * FALL_COST, TileMapMove.Fall));
            }

            if (_jumpHeight < 1 && _jumpDistance < 1) return;

            // How high they can get from here before they bump their head
            int headroom = 0;
            while (headroom < _jumpHeight && Fits(from.X, from.Y - headroom - 1)) headroom++;

            for (int dx = -_jumpDistance; dx <= _jumpDistance; dx++)
            {
                if (dx == 0) continue;

                // Up to as high as they get, and down to anything they could have dropped onto from the top of the jump
                for (int rise = headroom; rise >= 0; rise--)
                {
                    if (!ClearAcross(from.X, from.X + dx, from.Y - rise)) continue;
                    if (!TryLand(from.X + dx, from.Y - rise, out int landed, allowHere: true)) continue;

                    // Anything a step or a drop gets to is got to that way, a jump is for what they don't reach
                    if (landed == from.Y && Math.Abs(dx) == 1) continue;

                    float price = JUMP_COST + Math.Abs(dx) * JUMP_DISTANCE_COST + rise * JUMP_HEIGHT_COST + Math.Max(0, landed - from.Y) * FALL_COST;
                    into.Add(((from.X + dx, landed), price, TileMapMove.Jump));
                    break;
                }
            }
        }

        /// <summary>
        /// Test if the agent fits in every cell of a row from one column to another.
        /// </summary>
        private bool ClearAcross(int fromX, int toX, int y)
        {
            int step = Math.Sign(toX - fromX);
            for (int x = fromX; x != toX + step; x += step)
            {
                if (!Fits(x, y)) return false;
            }

            return true;
        }

        /// <summary>
        /// Helper method to drop the agent from a cell and see where it lands. False if it falls off the map or further than it is willing to.
        /// </summary>
        /// <param name="allowHere">Whether the cell it starts in counts as somewhere to land, if there is floor under it.</param>
        private bool TryLand(int x, int y, out int landed, bool allowHere = false)
        {
            landed = y;
            for (int fallen = allowHere ? 0 : 1; fallen <= _maxFall; fallen++)
            {
                int row = y + fallen;
                if (!_finder._map.Contains(x, row) || !Fits(x, row)) return false;

                if (CanStand(x, row))
                {
                    landed = row;
                    return true;
                }
            }

            return false;
        }
    }
}

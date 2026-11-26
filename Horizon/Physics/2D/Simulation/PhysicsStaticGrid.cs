using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Physics.Fixtures;

namespace Horizon.Physics.Simulation;

/// <summary>
/// Sorts the fixtures of every static body into a grid, so that finding what is near a point doesn't mean testing the whole map.
/// A map is thousands of tiles and there can be thousands of particles, testing each against each is how a CPU gets set on fire.
/// </summary>
internal sealed class PhysicsStaticGrid
{
    public const float CELL_SIZE = 32.0f;
    private const float INV_CELL_SIZE = 1.0f / CELL_SIZE;

    private PhysicsShape[] _shapes = [];

    // What every shape was made from, for the bodies which test against the fixtures themselves
    private IPhysicsFixture[] _fixtures = [];
    private Vector2[] _bodyPositions = [];

    // The shapes touching cell i are _cellShapes[_cellStart[i].._cellStart[i + 1]]
    private int[] _cellStart = [0];
    private int[] _cellShapes = [];

    // Dense 1-cell dilation mask: 1 if this cell or any of its 8 neighbours has at least one static shape
    private byte[] _nearMask = [];

    private Vector2 _origin;
    private int _width, _height;
    private int _builtFrom = -1;

    // A shape spanning several cells would be found once in each, the stamps are how Query tells it has seen one already
    private int[] _stamps = [];
    private int[] _found = [];
    private int _stamp;

    /// <summary>
    /// Goes up every time the grid is rebuilt, for whoever remembers things about the map (sleeping particles).
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The corners of the box around everything in the grid.
    /// </summary>
    public Vector2 Min => _origin;
    public Vector2 Max { get; private set; }

    public bool IsEmpty => _shapes.Length == 0;

    /// <summary>
    /// How many cells the grid is across and up.
    /// </summary>
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Everything in the grid, and which of it touches which cell. The shapes touching cell i (y * Width + x) are
    /// CellShapes[CellStart[i]..CellStart[i + 1]]. None of these arrays is written to once the grid is built,
    /// a rebuild makes new ones, so they can be held on to by whoever needs the map as it was.
    /// </summary>
    public PhysicsShape[] Shapes => _shapes;
    public int[] CellStart => _cellStart;
    public int[] CellShapes => _cellShapes;
    internal ReadOnlySpan<byte> NearMask => _nearMask;

    /// <summary>
    /// Rebuilds the grid if static bodies or fixtures were added, removed, swapped, moved or resized since the last time.
    /// </summary>
    public void Refresh(List<PhysicsBodyComponent2D> staticBodies)
    {
        int index = 0;
        bool changed = false;

        for (int b = 0; b < staticBodies.Count; b++)
        {
            var body = staticBodies[b];
            Vector2 bodyPos = body.Position;
            var fixtures = body.DynamicFixtures;

            for (int f = 0; f < fixtures.Count; f++)
            {
                IPhysicsFixture fixture = fixtures[f];
                if (!PhysicsShape.TryCreate(fixture, bodyPos, Vector2.Zero, out var shape)) continue;

                if (!changed)
                {
                    if (index >= _shapes.Length ||
                        !ReferenceEquals(_fixtures[index], fixture) ||
                        _bodyPositions[index] != bodyPos ||
                        !SameShape(in _shapes[index], in shape))
                    {
                        changed = true;
                    }
                }

                index++;
            }
        }

        if (!changed && index == _shapes.Length && _builtFrom == index) return;
        _builtFrom = index;

        Build(staticBodies, index);
    }

    private static bool SameShape(in PhysicsShape a, in PhysicsShape b) =>
        a.Min == b.Min &&
        a.Max == b.Max &&
        a.Radius == b.Radius &&
        a.IsCircle == b.IsCircle &&
        a.OutlineCount == b.OutlineCount &&
        a.OutlineVersion == b.OutlineVersion &&
        ReferenceEquals(a.Outline, b.Outline);

    private void Build(List<PhysicsBodyComponent2D> staticBodies, int fixtureCount)
    {
        var shapes = new List<PhysicsShape>(fixtureCount);
        var fixtures = new List<IPhysicsFixture>(fixtureCount);
        var bodyPositions = new List<Vector2>(fixtureCount);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);

        foreach (var body in staticBodies)
        {
            foreach (var fixture in body.DynamicFixtures)
            {
                if (!PhysicsShape.TryCreate(fixture, body.Position, Vector2.Zero, out var shape)) continue;

                shapes.Add(shape);
                fixtures.Add(fixture);
                bodyPositions.Add(body.Position);
                min = Vector2.Min(min, shape.Min);
                max = Vector2.Max(max, shape.Max);
            }
        }

        _shapes = [.. shapes];
        _fixtures = [.. fixtures];
        _bodyPositions = [.. bodyPositions];
        _stamps = new int[_shapes.Length];
        _found = new int[_shapes.Length];
        _origin = min;
        Max = max;
        Version++;
        _width = _shapes.Length == 0 ? 0 : (int)((max.X - min.X) * INV_CELL_SIZE) + 1;
        _height = _shapes.Length == 0 ? 0 : (int)((max.Y - min.Y) * INV_CELL_SIZE) + 1;

        // First count how many shapes touch every cell, so they can all be packed into one array
        _cellStart = new int[_width * _height + 1];
        for (int i = 0; i < _shapes.Length; i++)
        {
            GetCells(_shapes[i].Min, _shapes[i].Max, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    _cellStart[y * _width + x + 1]++;
        }

        for (int i = 0; i < _width * _height; i++)
        {
            _cellStart[i + 1] += _cellStart[i];
        }

        // Then hand every cell its shapes
        _cellShapes = new int[_cellStart[^1]];
        int[] filled = new int[_width * _height];
        for (int i = 0; i < _shapes.Length; i++)
        {
            GetCells(_shapes[i].Min, _shapes[i].Max, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = y * _width + x;
                    _cellShapes[_cellStart[cell] + filled[cell]++] = i;
                }
            }
        }

        // Mark every cell within 1 cell of any occupied cell so interior fluid queries reject in 1 byte load
        _nearMask = new byte[_width * _height];
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                if (_cellStart[row + x + 1] == _cellStart[row + x]) continue;

                int ny0 = Math.Max(0, y - 1), ny1 = Math.Min(_height - 1, y + 1);
                int nx0 = Math.Max(0, x - 1), nx1 = Math.Min(_width - 1, x + 1);
                for (int ny = ny0; ny <= ny1; ny++)
                {
                    int nRow = ny * _width;
                    for (int nx = nx0; nx <= nx1; nx++)
                    {
                        _nearMask[nRow + nx] = 1;
                    }
                }
            }
        }
    }

    public ref readonly PhysicsShape this[int shape] => ref _shapes[shape];

    public IPhysicsFixture GetFixture(int shape) => _fixtures[shape];

    /// <summary>
    /// Where the body of a shape was when the grid was built.
    /// </summary>
    public Vector2 GetBodyPosition(int shape) => _bodyPositions[shape];

    /// <summary>
    /// Tests if a point is inside of anything in the grid.
    /// </summary>
    public bool Contains(Vector2 point)
    {
        if (!TryGetCells(point, point, out int x, out int y, out _, out _)) return false;

        foreach (int shape in GetShapes(x, y))
        {
            if (_shapes[shape].Contains(point)) return true;
        }

        return false;
    }

    /// <summary>
    /// Tests whether the line segment from <paramref name="from"/> to <paramref name="to"/> enters or crosses
    /// any shape in the grid, returning the earliest hit fraction and outward surface normal.
    /// </summary>
    public bool TryIntersectSegment(Vector2 from, Vector2 to, out float hitT, out Vector2 hitNormal)
    {
        hitT = float.MaxValue;
        hitNormal = default;

        Vector2 min = Vector2.Min(from, to), max = Vector2.Max(from, to);
        if (!TryGetCells(min, max, out int x0, out int y0, out int x1, out int y1)) return false;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int index in GetShapes(x, y))
                {
                    if (_shapes[index].TryIntersectSegment(from, to, out float t, out Vector2 normal) && t < hitT)
                    {
                        hitT = t;
                        hitNormal = normal;
                    }
                }
            }
        }

        return hitT <= 1.0f;
    }

    /// <summary>
    /// Tests whether the line segment from <paramref name="from"/> to <paramref name="to"/> enters or crosses
    /// any shape in the grid.
    /// </summary>
    public bool IntersectsSegment(Vector2 from, Vector2 to)
    {
        Vector2 min = Vector2.Min(from, to), max = Vector2.Max(from, to);
        if (!TryGetCells(min, max, out int x0, out int y0, out int x1, out int y1)) return false;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int index in GetShapes(x, y))
                {
                    if (_shapes[index].TryIntersectSegment(from, to, out _, out _)) return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds every shape that could be touching a box, each of them exactly once.
    /// The result is only good until the next call.
    /// </summary>
    public ReadOnlySpan<int> Query(Vector2 min, Vector2 max)
    {
        if (!TryGetCells(min, max, out int x0, out int y0, out int x1, out int y1)) return default;

        _stamp++;
        int count = 0;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                foreach (int shape in GetShapes(x, y))
                {
                    if (_stamps[shape] == _stamp) continue;

                    _stamps[shape] = _stamp;
                    _found[count++] = shape;
                }
            }
        }

        return _found.AsSpan(0, count);
    }

    /// <summary>
    /// Finds the cells a box reaches into, false if it is entirely off the map.
    /// </summary>
    public bool TryGetCells(Vector2 min, Vector2 max, out int x0, out int y0, out int x1, out int y1)
    {
        if (_width == 0 ||
            max.X < _origin.X || max.Y < _origin.Y ||
            min.X > _origin.X + _width * CELL_SIZE || min.Y > _origin.Y + _height * CELL_SIZE)
        {
            x0 = y0 = x1 = y1 = 0;
            return false;
        }

        GetCells(min, max, out x0, out y0, out x1, out y1);
        return true;
    }

    /// <summary>
    /// The shapes touching a cell, a shape that spans several cells is in each of them.
    /// </summary>
    public ReadOnlySpan<int> GetShapes(int x, int y)
    {
        int cell = y * _width + x;
        return _cellShapes.AsSpan(_cellStart[cell], _cellStart[cell + 1] - _cellStart[cell]);
    }

    /// <summary>
    /// Fast test for whether any static shape AABB is within <paramref name="reach"/> of <paramref name="position"/>.
    /// When <paramref name="reach"/> &lt;= <see cref="CELL_SIZE"/>, interior empty cells reject in a single byte load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNearAnyShape(Vector2 position, float reach)
    {
        if (_width == 0) return false;

        if (reach <= CELL_SIZE)
        {
            int cx = (int)MathF.Floor((position.X - _origin.X) * INV_CELL_SIZE);
            int cy = (int)MathF.Floor((position.Y - _origin.Y) * INV_CELL_SIZE);

            if ((uint)cx < (uint)_width && (uint)cy < (uint)_height)
            {
                if (_nearMask[cy * _width + cx] == 0) return false;
            }
            else if (cx < -1 || cx > _width || cy < -1 || cy > _height)
            {
                return false;
            }
        }

        Vector2 min = position - new Vector2(reach), max = position + new Vector2(reach);
        if (!TryGetCells(min, max, out int x0, out int y0, out int x1, out int y1)) return false;

        for (int y = y0; y <= y1; y++)
        {
            int row = y * _width;
            for (int x = x0; x <= x1; x++)
            {
                int cell = row + x;
                int start = _cellStart[cell], end = _cellStart[cell + 1];
                for (int entry = start; entry < end; entry++)
                {
                    ref readonly PhysicsShape shape = ref _shapes[_cellShapes[entry]];
                    if (max.X >= shape.Min.X && min.X <= shape.Max.X && max.Y >= shape.Min.Y && min.Y <= shape.Max.Y)
                        return true;
                }
            }
        }

        return false;
    }

    private void GetCells(Vector2 min, Vector2 max, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Clamp((int)((min.X - _origin.X) * INV_CELL_SIZE), 0, _width - 1);
        y0 = Math.Clamp((int)((min.Y - _origin.Y) * INV_CELL_SIZE), 0, _height - 1);
        x1 = Math.Clamp((int)((max.X - _origin.X) * INV_CELL_SIZE), 0, _width - 1);
        y1 = Math.Clamp((int)((max.Y - _origin.Y) * INV_CELL_SIZE), 0, _height - 1);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeKneading.Voxel;

public enum TileType : sbyte
{
    None,
    OOB,
    Ground,
    Rock,
}

public readonly struct Tile
{
    public readonly TileType Type { get; init; }

    public static readonly Tile Empty = new()
    {
        Type = TileType.None,
    };
    public static readonly Tile OOB = new()
    {
        Type = TileType.OOB,
    };
}

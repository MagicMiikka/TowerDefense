using System;
using System.Collections.Generic;

namespace TD.Core
{
    public enum TileType { Ground, Rock, Spawn, Exit }

    /// <summary>
    /// Static terrain of one site. Built from ASCII rows so hand-made layouts (and later the
    /// chunk generator's output) are plain text:
    ///   '.' ground (walkable, buildable)   '#' rock   'S' enemy spawn   'E' exit (your base)
    /// </summary>
    public sealed class Layout
    {
        public readonly string Name;
        public readonly int Width;
        public readonly int Height;
        private readonly TileType[] _tiles;
        public readonly List<GridPos> Spawns = new List<GridPos>();
        public readonly List<GridPos> Exits = new List<GridPos>();

        private Layout(string name, int w, int h)
        {
            Name = name; Width = w; Height = h; _tiles = new TileType[w * h];
        }

        public static Layout Parse(string name, params string[] rows)
        {
            if (rows == null || rows.Length == 0) throw new ArgumentException("Layout needs rows");
            int w = rows[0].Length;
            var layout = new Layout(name, w, rows.Length);
            for (int y = 0; y < rows.Length; y++)
            {
                if (rows[y].Length != w) throw new ArgumentException($"Layout '{name}' row {y} has length {rows[y].Length}, expected {w}");
                for (int x = 0; x < w; x++)
                {
                    TileType t;
                    switch (rows[y][x])
                    {
                        case '.': t = TileType.Ground; break;
                        case '#': t = TileType.Rock; break;
                        case 'S': t = TileType.Spawn; layout.Spawns.Add(new GridPos(x, y)); break;
                        case 'E': t = TileType.Exit; layout.Exits.Add(new GridPos(x, y)); break;
                        default: throw new ArgumentException($"Layout '{name}' has unknown char '{rows[y][x]}' at {x},{y}");
                    }
                    layout._tiles[y * w + x] = t;
                }
            }
            if (layout.Spawns.Count == 0 || layout.Exits.Count == 0)
                throw new ArgumentException($"Layout '{name}' needs at least one S and one E");
            return layout;
        }

        public bool InBounds(GridPos p) => p.X >= 0 && p.Y >= 0 && p.X < Width && p.Y < Height;
        public TileType this[GridPos p] => _tiles[p.Y * Width + p.X];
        public bool IsWalkable(GridPos p) => InBounds(p) && this[p] != TileType.Rock;
        public bool IsBuildable(GridPos p) => InBounds(p) && this[p] == TileType.Ground;
        public int Index(GridPos p) => p.Y * Width + p.X;
    }
}

using System;
using System.Collections.Generic;

namespace TD.Core
{
    /// <summary>Integer tile coordinate. Y grows downward (row index in layout text).</summary>
    public readonly struct GridPos : IEquatable<GridPos>
    {
        public readonly int X;
        public readonly int Y;

        public GridPos(int x, int y) { X = x; Y = y; }

        public static readonly GridPos[] Directions =
        {
            new GridPos(1, 0), new GridPos(-1, 0), new GridPos(0, 1), new GridPos(0, -1)
        };

        /// <summary>Orthogonal first, so ties prefer straight moves.</summary>
        public static readonly GridPos[] Directions8 =
        {
            new GridPos(1, 0), new GridPos(-1, 0), new GridPos(0, 1), new GridPos(0, -1),
            new GridPos(1, 1), new GridPos(1, -1), new GridPos(-1, 1), new GridPos(-1, -1)
        };

        public static GridPos operator +(GridPos a, GridPos b) => new GridPos(a.X + b.X, a.Y + b.Y);
        public static bool operator ==(GridPos a, GridPos b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(GridPos a, GridPos b) => !(a == b);
        public bool Equals(GridPos other) => this == other;
        public override bool Equals(object obj) => obj is GridPos p && this == p;
        public override int GetHashCode() => (X * 73856093) ^ (Y * 19349663);
        public override string ToString() => $"({X},{Y})";
        public Vec2 Center => new Vec2(X, Y);
    }

    /// <summary>Minimal float vector so the core has no engine dependency. Tile centers sit on integer coordinates.</summary>
    public readonly struct Vec2
    {
        public readonly float X;
        public readonly float Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public float Length => (float)Math.Sqrt(X * X + Y * Y);
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
        public Vec2 Normalized { get { float l = Length; return l > 1e-6f ? new Vec2(X / l, Y / l) : new Vec2(0, 0); } }
        public Vec2 Perpendicular => new Vec2(-Y, X);
        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

        public static Vec2 MoveTowards(Vec2 from, Vec2 to, float maxStep, out bool arrived)
        {
            Vec2 d = to - from;
            float len = d.Length;
            if (len <= maxStep || len < 1e-6f) { arrived = true; return to; }
            arrived = false;
            return from + d * (maxStep / len);
        }

        public GridPos Round => new GridPos((int)Math.Round(X), (int)Math.Round(Y));
        public override string ToString() => $"({X:0.00},{Y:0.00})";
    }

    /// <summary>Deterministic xorshift RNG. Same seed, same run, on every platform.</summary>
    public sealed class Rng
    {
        private ulong _state;

        public Rng(int seed) { _state = (ulong)(seed * 2654435761L) ^ 0x9E3779B97F4A7C15UL; if (_state == 0) _state = 1; }

        public ulong NextULong()
        {
            _state ^= _state << 13;
            _state ^= _state >> 7;
            _state ^= _state << 17;
            return _state;
        }

        /// <summary>[0, 1)</summary>
        public float NextFloat() => (NextULong() >> 40) / (float)(1UL << 24);

        /// <summary>[min, maxExclusive)</summary>
        public int Range(int min, int maxExclusive) => min + (int)(NextULong() % (ulong)Math.Max(1, maxExclusive - min));

        public bool Chance(float p) => NextFloat() < p;
    }

    /// <summary>Binary min-heap; netstandard2.1 (Unity) has no PriorityQueue.</summary>
    internal sealed class MinHeap<T>
    {
        private readonly List<(float key, T item)> _data = new List<(float, T)>();
        public int Count => _data.Count;

        public void Push(float key, T item)
        {
            _data.Add((key, item));
            int i = _data.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (_data[p].key <= _data[i].key) break;
                (_data[p], _data[i]) = (_data[i], _data[p]);
                i = p;
            }
        }

        public (float key, T item) Pop()
        {
            var top = _data[0];
            int last = _data.Count - 1;
            _data[0] = _data[last];
            _data.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < _data.Count && _data[l].key < _data[m].key) m = l;
                if (r < _data.Count && _data[r].key < _data[m].key) m = r;
                if (m == i) break;
                (_data[m], _data[i]) = (_data[i], _data[m]);
                i = m;
            }
            return top;
        }
    }

    internal static class MathX
    {
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
    }
}

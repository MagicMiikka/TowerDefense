using System;

namespace TD.Core
{
    /// <summary>
    /// Distance-to-exit field (Dijkstra from all exits). Enemies step to the neighbour with the lowest
    /// value. Each movement mode gets its own field via a cost function: +Infinity means impassable.
    /// </summary>
    public sealed class FlowField
    {
        private readonly Layout _layout;
        private readonly float[] _dist;

        public FlowField(Layout layout)
        {
            _layout = layout;
            _dist = new float[layout.Width * layout.Height];
        }

        public float this[GridPos p] => _layout.InBounds(p) ? _dist[_layout.Index(p)] : float.PositiveInfinity;

        /// <param name="enterCost">Cost of stepping INTO a tile; +Infinity = blocked.</param>
        public void Compute(Func<GridPos, float> enterCost)
        {
            for (int i = 0; i < _dist.Length; i++) _dist[i] = float.PositiveInfinity;
            var heap = new MinHeap<GridPos>();
            foreach (var e in _layout.Exits)
            {
                _dist[_layout.Index(e)] = 0;
                heap.Push(0, e);
            }
            while (heap.Count > 0)
            {
                var (d, p) = heap.Pop();
                if (d > _dist[_layout.Index(p)]) continue;
                // Expanding backwards: an enemy on neighbour n pays enterCost(p) to step into p.
                float cost = enterCost(p);
                if (float.IsPositiveInfinity(cost)) continue;
                foreach (var dir in GridPos.Directions8)
                {
                    var n = p + dir;
                    if (!_layout.InBounds(n)) continue;
                    if (float.IsPositiveInfinity(enterCost(n))) continue;
                    bool diagonal = dir.X != 0 && dir.Y != 0;
                    // No corner cutting: a diagonal step needs both orthogonal tiles open.
                    if (diagonal && (float.IsPositiveInfinity(enterCost(new GridPos(p.X, n.Y)))
                                     || float.IsPositiveInfinity(enterCost(new GridPos(n.X, p.Y))))) continue;
                    float nd = d + (diagonal ? cost * 1.41421f : cost);
                    int ni = _layout.Index(n);
                    if (nd < _dist[ni])
                    {
                        _dist[ni] = nd;
                        heap.Push(nd, n);
                    }
                }
            }
        }

        /// <summary>Best neighbour to step into from p, or p itself if stuck. Deterministic tie order.</summary>
        public GridPos NextStep(GridPos p)
        {
            GridPos best = p;
            float bestD = this[p];
            foreach (var dir in GridPos.Directions8)
            {
                var n = p + dir;
                if (dir.X != 0 && dir.Y != 0 && (!Reaches(new GridPos(n.X, p.Y)) || !Reaches(new GridPos(p.X, n.Y)))) continue;
                float d = this[n];
                if (d < bestD - 1e-4f) { bestD = d; best = n; }
            }
            return best;
        }

        public bool Reaches(GridPos p) => !float.IsPositiveInfinity(this[p]);
    }
}

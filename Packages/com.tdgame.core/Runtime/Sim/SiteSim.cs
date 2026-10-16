using System;
using System.Collections.Generic;

namespace TD.Core
{
    public enum SitePhase { Build, Wave, Won, Lost }

    public enum Occupant { None, Wall, Tower }

    /// <summary>
    /// One site (layout) of a run: placement, Essence, waves, combat and the hand-off to the enemy's memory.
    /// Pure logic, fixed-step friendly; the Unity layer calls Tick(dt), reads state, and drains Events.
    /// </summary>
    public sealed class SiteSim
    {
        public readonly Layout Layout;
        public readonly RunState Run;
        private readonly Balance _b;
        private readonly Rng _rng;

        public SitePhase Phase { get; private set; } = SitePhase.Build;
        public int Essence { get; private set; }

        /// <summary>Seconds until the next wave starts on its own. Waves overlap: calling one early never waits for the field to clear.</summary>
        public float NextWaveTimer { get; private set; }
        public int WavesStarted { get; private set; }
        public int WavesCleared { get; private set; }
        public int WaveIndex => WavesStarted;                // kept for older callers
        public int WavesTotal => _b.WavesPerSite;
        public bool MoreWavesToCome => WavesStarted < WavesTotal;
        public float BaselinePathLength { get; private set; }

        /// <summary>Before the first wave of a site: no countdown and no early-call bonus; it starts when the player says so.</summary>
        public bool AwaitingManualStart => Phase == SitePhase.Build;

        /// <summary>Essence the player gets for calling the next wave right now.</summary>
        public int EarlyCallBonus => AwaitingManualStart || !MoreWavesToCome ? 0 : (int)Math.Floor(NextWaveTimer * _b.EarlyCallEssencePerSecond);

        /// <summary>Waves currently on the field (spawning or with enemies alive).</summary>
        public int ActiveWaves => _active.Count;

        public readonly List<Enemy> Enemies = new List<Enemy>();
        public readonly List<Tower> Towers = new List<Tower>();
        public readonly Dictionary<GridPos, Wall> Walls = new Dictionary<GridPos, Wall>();
        public readonly List<SimEvent> Events = new List<SimEvent>();

        /// <summary>Tower ids (or "wall") that sell at full refund right now, because an adaptation just countered them.</summary>
        public readonly HashSet<string> RefundWindow = new HashSet<string>();
        private int _refundExpiresAtWave = -1;

        private readonly Occupant[] _occ;
        private readonly Dictionary<GridPos, Tower> _towerAt = new Dictionary<GridPos, Tower>();
        private readonly Dictionary<string, int> _copies = new Dictionary<string, int>();
        private readonly FlowField _ground, _burrow, _siege, _scratch;
        private bool _fieldsDirty = true;

        private sealed class ActiveWave
        {
            public int Global;
            public readonly Queue<SpawnSpec> Queue = new Queue<SpawnSpec>();
            public float SpawnTimer;
            public int Alive;
            public WaveStats Stats;
        }

        private readonly List<ActiveWave> _active = new List<ActiveWave>();
        private readonly Dictionary<int, ActiveWave> _activeByGlobal = new Dictionary<int, ActiveWave>();
        private int _spawnCursor;
        private int _nextId = 1;

        /// <summary>Stats of the most recently finished wave (for logs and the debug view).</summary>
        public WaveStats LastWaveStats { get; private set; }
        public WaveStats CurrentWaveStats => LastWaveStats;
        public AdaptationEngine Enemy => Run.Enemy;
        public int GlobalWave => Run.GlobalWave;

        public SiteSim(Layout layout, RunState run)
        {
            Layout = layout;
            Run = run;
            _b = run.Balance;
            _rng = new Rng(run.Seed * 31 + run.SitesCleared * 977 + 7);
            _occ = new Occupant[layout.Width * layout.Height];
            _ground = new FlowField(layout);
            _burrow = new FlowField(layout);
            _siege = new FlowField(layout);
            _scratch = new FlowField(layout);
            Essence = _b.StartingEssence + _b.SiteEssencePerGlobalWave * run.GlobalWave;
            NextWaveTimer = _b.WaveInterval;

            _scratch.Compute(p => layout.IsWalkable(p) ? 1f : float.PositiveInfinity);
            BaselinePathLength = ShortestSpawnDistance(_scratch);
        }

        // ------------------------------------------------------------------ queries

        public Occupant OccupantAt(GridPos p) => Layout.InBounds(p) ? _occ[Layout.Index(p)] : Occupant.None;
        public Tower TowerAt(GridPos p) => _towerAt.TryGetValue(p, out var t) ? t : null;
        public int Copies(string towerId) => _copies.TryGetValue(towerId, out var c) ? c : 0;

        public int TowerCost(string towerId)
        {
            var def = Content.Tower(towerId);
            return (int)Math.Round(def.BaseCost * (1f + _b.CopyCostStep * Copies(towerId)));
        }

        public int WallCost => _b.WallBaseCost + Walls.Count / Math.Max(1, _b.WallsPerCostStep);

        public FlowField GroundField { get { EnsureFields(); return _ground; } }

        public float GroundPathLength { get { EnsureFields(); return ShortestSpawnDistance(_ground); } }

        public float MazeRatio => BaselinePathLength > 0 ? GroundPathLength / BaselinePathLength : 1f;

        /// <summary>The route a ground enemy from this spawn would walk right now (for the path preview).</summary>
        public List<GridPos> GroundPath(GridPos spawn)
        {
            EnsureFields();
            var path = new List<GridPos> { spawn };
            var p = spawn;
            for (int guard = 0; guard < Layout.Width * Layout.Height; guard++)
            {
                if (Layout[p] == TileType.Exit) break;
                var n = _ground.NextStep(p);
                if (n == p) break;
                path.Add(n);
                p = n;
            }
            return path;
        }

        /// <summary>
        /// The line a ground enemy from this spawn would actually walk with free movement: the flow-field route
        /// pulled tight with the same line-of-sight rule enemies steer by. For the path preview.
        /// </summary>
        public List<Vec2> PreviewPath(GridPos spawn, float radius = 0.22f)
        {
            EnsureFields();
            var pts = new List<Vec2> { spawn.Center };
            var pos = spawn.Center;
            var tile = spawn;
            int guard = Layout.Width * Layout.Height;
            while (Layout[tile] != TileType.Exit && guard-- > 0)
            {
                var next = _ground.NextStep(tile);
                if (next == tile) break; // no route (shouldn't happen with the path rule)
                GridPos target = next;
                var walk = next;
                for (int k = 0; k < guard; k++)
                {
                    var n = _ground.NextStep(walk);
                    if (n == walk || !ClearPath(pos, n.Center, radius, MoveMode.Ground)) break;
                    target = n;
                    walk = n;
                    if (Layout[n] == TileType.Exit) break;
                }
                pos = target.Center;
                tile = target;
                pts.Add(pos);
            }
            return pts;
        }

        /// <summary>Ground path length if p were blocked (wall/tower). +Infinity if that would block the path. Used by bots and placement hints.</summary>
        public float PathLengthIfBlocked(GridPos p)
        {
            if (!Layout.IsBuildable(p) || OccupantAt(p) != Occupant.None) return float.PositiveInfinity;
            int i = Layout.Index(p);
            _occ[i] = Occupant.Tower;
            _scratch.Compute(GroundCost);
            _occ[i] = Occupant.None;
            float worst = 0;
            foreach (var s in Layout.Spawns) worst = MathX.Max(worst, _scratch[s]);
            return worst;
        }

        // ------------------------------------------------------------------ building

        public bool CanPlace(GridPos p, out string reason)
        {
            if (Phase == SitePhase.Won || Phase == SitePhase.Lost) { reason = "Site is over"; return false; }
            if (!Layout.IsBuildable(p)) { reason = "Can't build there"; return false; }
            if (OccupantAt(p) != Occupant.None) { reason = "Tile is occupied"; return false; }
            foreach (var e in Enemies)
                if (e.IsActive && !e.IsAir && (e.Tile == p || CircleOverlapsTile(e.Pos, e.Radius + 0.02f, p))) { reason = "An enemy is in the way"; return false; }

            // Path rule: every spawn, and every ground enemy, must still reach an exit.
            int i = Layout.Index(p);
            _occ[i] = Occupant.Tower;
            _scratch.Compute(GroundCost);
            _occ[i] = Occupant.None;

            foreach (var s in Layout.Spawns)
                if (!_scratch.Reaches(s)) { reason = "That would block the path"; return false; }
            foreach (var e in Enemies)
                if (e.IsActive && e.Mode == MoveMode.Ground && !_scratch.Reaches(e.Tile)) { reason = "That would trap an enemy"; return false; }

            reason = null;
            return true;
        }

        public bool PlaceTower(string towerId, GridPos p, out string reason)
        {
            if (!Run.OwnedTowers.Contains(towerId)) { reason = "You don't own that tower"; return false; }
            int cost = TowerCost(towerId);
            if (Essence < cost) { reason = "Not enough Essence"; return false; }
            if (!CanPlace(p, out reason)) return false;

            var t = new Tower { Id = _nextId++, Def = Content.Tower(towerId), Pos = p, PaidCost = cost };
            Towers.Add(t);
            _towerAt[p] = t;
            _occ[Layout.Index(p)] = Occupant.Tower;
            _copies[towerId] = Copies(towerId) + 1;
            Essence -= cost;
            _fieldsDirty = true;
            return true;
        }

        public bool PlaceWall(GridPos p, out string reason)
        {
            int cost = WallCost;
            if (Essence < cost) { reason = "Not enough Essence"; return false; }
            if (!CanPlace(p, out reason)) return false;

            Walls[p] = new Wall { Pos = p, PaidCost = cost, Hp = _b.WallHp };
            _occ[Layout.Index(p)] = Occupant.Wall;
            Essence -= cost;
            _fieldsDirty = true;
            return true;
        }

        public int SellValue(GridPos p)
        {
            var t = TowerAt(p);
            if (t != null) return Refund(t.PaidCost, t.Def.Id);
            if (Walls.TryGetValue(p, out var w) && !w.Destroyed) return Refund(w.PaidCost, AdaptationInfo.WallId);
            return 0;
        }

        private int Refund(int paid, string id) =>
            (int)Math.Floor(paid * (RefundWindow.Contains(id) ? _b.AdaptRefund : _b.SellRefund));

        public bool Sell(GridPos p)
        {
            int value = SellValue(p);
            var t = TowerAt(p);
            if (t != null)
            {
                Towers.Remove(t);
                _towerAt.Remove(p);
                _copies[t.Def.Id] = Copies(t.Def.Id) - 1;
            }
            else if (Walls.TryGetValue(p, out var w) && !w.Destroyed)
            {
                Walls.Remove(p);
            }
            else return false;

            _occ[Layout.Index(p)] = Occupant.None;
            Essence += value;
            _fieldsDirty = true;
            return true;
        }

        // ------------------------------------------------------------------ wave flow

        /// <summary>
        /// Send the next wave now. Allowed any time, even while earlier waves are still on the field; calling early
        /// pays Essence for the unused countdown (except the very first wave of a site, which is always manual).
        /// </summary>
        public void StartNextWave()
        {
            if (Phase == SitePhase.Won || Phase == SitePhase.Lost || !MoreWavesToCome) return;

            int bonus = EarlyCallBonus;
            if (bonus > 0)
            {
                Essence += bonus;
                Emit(SimEventType.EarlyCallBonus, bonus);
            }

            int g = Run.GlobalWave;
            if (_refundExpiresAtWave >= 0 && g >= _refundExpiresAtWave) { RefundWindow.Clear(); _refundExpiresAtWave = -1; }

            var landed = Enemy.LandIfDue(g);
            if (landed != null)
            {
                RefundWindow.Clear();
                foreach (var id in AdaptationInfo.Counters(landed.Kind)) RefundWindow.Add(id);
                _refundExpiresAtWave = g + 1; // until the next wave is sent
                Events.Add(new SimEvent { Type = SimEventType.AdaptationLanded, Telegraph = landed, Text = AdaptationInfo.Name(landed.Kind) });
            }

            var w = new ActiveWave
            {
                Global = g,
                Stats = new WaveStats { MazeRatio = MazeRatio, Walls = Walls.Count, Towers = Towers.Count },
            };
            foreach (var spec in WaveGenerator.Build(g, Enemy, _b, _rng)) w.Queue.Enqueue(spec);
            _active.Add(w);
            _activeByGlobal[g] = w;

            Run.GlobalWave++;
            WavesStarted++;
            NextWaveTimer = _b.WaveInterval;
            Phase = SitePhase.Wave;
            Emit(SimEventType.WaveStarted, g);
        }

        public void Tick(float dt)
        {
            if (Phase != SitePhase.Wave) return; // before the first wave nothing moves; after the site, nothing either
            TickWave(dt);
        }

        private void TickWave(float dt)
        {
            EnsureFields();

            if (MoreWavesToCome)
            {
                NextWaveTimer -= dt;
                if (NextWaveTimer <= 0) { NextWaveTimer = 0; StartNextWave(); }
            }

            foreach (var w in _active)
            {
                w.SpawnTimer -= dt;
                while (w.Queue.Count > 0 && w.SpawnTimer <= 0)
                {
                    Spawn(w.Queue.Dequeue(), w);
                    w.SpawnTimer += _b.SpawnInterval;
                }
            }

            for (int i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (!e.IsActive) continue;
                TickPoison(e, dt);
                if (e.IsActive) Move(e, dt);
                if (Phase == SitePhase.Lost) return;
            }

            foreach (var t in Towers) TickTower(t, dt);

            Enemies.RemoveAll(e => !e.IsActive);
            RetryWallRespawns();

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var w = _active[i];
                if (w.Queue.Count == 0 && w.Alive <= 0) EndWave(w);
                if (Phase != SitePhase.Wave) return;
            }
        }

        private void EndWave(ActiveWave w)
        {
            _active.Remove(w);
            _activeByGlobal.Remove(w.Global);
            WavesCleared++;
            LastWaveStats = w.Stats;
            Essence += _b.WaveClearBonus;
            RespawnWallsFrom(w.Global);
            Emit(SimEventType.WaveEnded, w.Global);

            var tele = Enemy.RecordWave(w.Stats, w.Global);
            if (tele != null)
            {
                // Always leave at least one more wave of warning, even if the player is chaining waves.
                if (tele.LandsAtGlobalWave < Run.GlobalWave + 1) tele.LandsAtGlobalWave = Run.GlobalWave + 1;
                Events.Add(new SimEvent { Type = SimEventType.AdaptationTelegraphed, Telegraph = tele, Text = tele.Reason });
            }

            if (WavesCleared >= WavesTotal)
            {
                Phase = SitePhase.Won;
                Run.Gold += (int)Math.Floor(Essence * _b.LeftoverToGold);
                Run.SitesCleared++;
                Emit(SimEventType.SiteWon, Run.Gold);
            }
        }

        private readonly List<Wall> _pendingRespawn = new List<Wall>();

        /// <summary>Walls breached by a wave come back when that wave is over.</summary>
        private void RespawnWallsFrom(int globalWave)
        {
            foreach (var wall in Walls.Values)
                if (wall.Destroyed && wall.DestroyedByWave == globalWave && !_pendingRespawn.Contains(wall)) _pendingRespawn.Add(wall);
            RetryWallRespawns();
        }

        private void RetryWallRespawns()
        {
            for (int i = _pendingRespawn.Count - 1; i >= 0; i--)
            {
                var wall = _pendingRespawn[i];
                if (!Walls.ContainsKey(wall.Pos)) { _pendingRespawn.RemoveAt(i); continue; }
                // Wait while an enemy stands there; give the Essence back if a newer building makes the spot path-blocking.
                if (CanPlace(wall.Pos, out var reason))
                {
                    wall.Destroyed = false;
                    wall.Hp = _b.WallHp;
                    _occ[Layout.Index(wall.Pos)] = Occupant.Wall;
                    _fieldsDirty = true;
                    Events.Add(new SimEvent { Type = SimEventType.WallRespawned, Pos = wall.Pos });
                    _pendingRespawn.RemoveAt(i);
                }
                else if (reason != "An enemy is in the way")
                {
                    Walls.Remove(wall.Pos);
                    Essence += wall.PaidCost;
                    Events.Add(new SimEvent { Type = SimEventType.WallRefunded, Pos = wall.Pos, A = wall.PaidCost });
                    _pendingRespawn.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------------ enemies

        private void Spawn(SpawnSpec s, ActiveWave wave)
        {
            var spawn = Layout.Spawns[_spawnCursor++ % Layout.Spawns.Count];
            var e = new Enemy
            {
                Id = _nextId++,
                Mode = s.Mode,
                Splits = s.Splits,
                MaxHp = s.Hp,
                Hp = s.Hp,
                Speed = s.Speed,
                Bounty = Content.Crawler.Bounty,
                LeakDamage = Content.Crawler.LeakDamage,
                Pos = spawn.Center + new Vec2(0, (_rng.NextFloat() - 0.5f) * 0.3f),
                Lane = (_rng.NextFloat() - 0.5f) * 0.5f,
                Wave = wave.Global,
                Tile = spawn,
                Next = spawn,
            };
            for (int i = 0; i < 3; i++) e.Resist[i] = Enemy.Resist((DamageType)i);
            if (e.Mode == MoveMode.Flying) e.FlyTarget = NearestExit(spawn);
            AddEnemy(e);
        }

        private void AddEnemy(Enemy e)
        {
            Enemies.Add(e);
            if (_activeByGlobal.TryGetValue(e.Wave, out var w)) w.Alive++;
            Events.Add(new SimEvent { Type = SimEventType.EnemySpawned, A = e.Id, At = e.Pos });
        }

        private FlowField FieldFor(MoveMode m) =>
            m == MoveMode.Burrow ? _burrow : m == MoveMode.Siege ? _siege : _ground;

        private void Move(Enemy e, float dt)
        {
            if (e.Mode == MoveMode.Flying)
            {
                e.Pos = Vec2.MoveTowards(e.Pos, e.FlyTarget.Center, e.Speed * dt, out bool landed);
                e.Remaining = Vec2.Distance(e.Pos, e.FlyTarget.Center);
                if (landed) Leak(e);
                return;
            }

            var field = FieldFor(e.Mode);
            var here = e.Pos.Round;
            if (Layout.InBounds(here) && field.Reaches(here)) e.Tile = here;
            if (Layout[e.Tile] == TileType.Exit) { Leak(e); return; }

            e.Next = field.NextStep(e.Tile);

            // Sappers stop against the wall their route goes through and dig.
            if (e.Mode == MoveMode.Siege && OccupantAt(e.Next) == Occupant.Wall
                && Walls.TryGetValue(e.Next, out var wall) && !wall.Destroyed
                && CircleOverlapsTile(e.Pos, e.Radius + 0.12f, e.Next))
            {
                e.Breaching = true;
                e.Velocity = new Vec2(0, 0);
                wall.Hp -= _b.SiegeWallDps * dt;
                if (wall.Hp <= 0) DestroyWall(wall, e.Wave);
                e.Remaining = field[e.Tile];
                return;
            }
            e.Breaching = false;

            // Steering target: the furthest tile along the flow field we can reach in a straight line.
            // This is what makes movement free (any angle, cuts corners smoothly) instead of tile-to-tile.
            GridPos target = e.Next;
            var walk = e.Next;
            for (int k = 0; k < 6; k++)
            {
                var n = field.NextStep(walk);
                if (n == walk) break;
                if (!ClearPath(e.Pos, n.Center, e.Radius, e.Mode)) break;
                target = n;
                walk = n;
            }
            Vec2 toTarget = target.Center - e.Pos;
            Vec2 dir = toTarget.Normalized;
            // Spread across the corridor: aim a little to one side, if that point is open.
            Vec2 aim = target.Center + dir.Perpendicular * e.Lane;
            if (!CircleBlocked(aim, e.Radius, e.Mode)) dir = (aim - e.Pos).Normalized;

            // Separation from nearby ground enemies so groups flow instead of stacking.
            Vec2 push = new Vec2(0, 0);
            foreach (var o in Enemies)
            {
                if (o == e || !o.IsActive || o.IsAir) continue;
                float min = e.Radius + o.Radius;
                Vec2 d = e.Pos - o.Pos;
                float dist = d.Length;
                if (dist >= min) continue;
                push = push + (dist > 1e-4f ? d * ((min - dist) / (min * dist)) : new Vec2((e.Id % 2 == 0 ? 1 : -1) * 0.5f, 0));
            }

            Vec2 desired = dir * e.Speed + push * (e.Speed * 1.5f);
            float len = desired.Length;
            if (len > e.Speed * 1.25f) desired = desired * (e.Speed * 1.25f / len);
            e.Velocity = desired;

            var before = e.Pos;
            e.Pos = ResolveCollisions(e.Pos + desired * dt, e.Radius, e.Mode);

            // Stuck safety: if the crowd or a corner pins us, walk straight to the next tile centre.
            if (Vec2.Distance(before, e.Pos) < e.Speed * dt * 0.1f)
            {
                e.StuckTime += dt;
                if (e.StuckTime > 1.5f) e.Pos = ResolveCollisions(Vec2.MoveTowards(e.Pos, e.Next.Center, e.Speed * dt, out _), e.Radius * 0.5f, e.Mode);
                if (e.StuckTime > 10f) { Leak(e); return; } // last-resort valve; the path rule should prevent this
            }
            else e.StuckTime = 0;

            here = e.Pos.Round;
            if (Layout.InBounds(here) && field.Reaches(here)) e.Tile = here;
            e.Underground = e.Mode == MoveMode.Burrow && OccupantAt(e.Pos.Round) == Occupant.Wall;
            e.Remaining = field[e.Next] + Vec2.Distance(e.Pos, e.Next.Center);
        }

        // ------------------------------------------------------------------ continuous movement helpers

        /// <summary>Whether a mover of this mode may physically stand on the tile (walls are solid except to burrowers).</summary>
        private bool Passable(GridPos p, MoveMode mode)
        {
            if (!Layout.IsWalkable(p)) return false;
            var o = _occ[Layout.Index(p)];
            return mode == MoveMode.Burrow ? o != Occupant.Tower : o == Occupant.None;
        }

        private static bool CircleOverlapsTile(Vec2 c, float r, GridPos t)
        {
            float cx = MathX.Max(t.X - 0.5f, MathX.Min(c.X, t.X + 0.5f));
            float cy = MathX.Max(t.Y - 0.5f, MathX.Min(c.Y, t.Y + 0.5f));
            float dx = c.X - cx, dy = c.Y - cy;
            return dx * dx + dy * dy < r * r;
        }

        private bool CircleBlocked(Vec2 c, float r, MoveMode mode)
        {
            var center = c.Round;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var t = new GridPos(center.X + dx, center.Y + dy);
                    if (!Passable(t, mode) && CircleOverlapsTile(c, r, t)) return true;
                }
            return false;
        }

        private bool ClearPath(Vec2 from, Vec2 to, float r, MoveMode mode)
        {
            float len = Vec2.Distance(from, to);
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.2f));
            for (int i = 1; i <= steps; i++)
            {
                var p = from + (to - from) * (i / (float)steps);
                if (CircleBlocked(p, r, mode)) return false;
            }
            return true;
        }

        /// <summary>Push a circle out of any blocked tile it overlaps (rocks, walls, towers, map edge).</summary>
        private Vec2 ResolveCollisions(Vec2 c, float r, MoveMode mode)
        {
            for (int iter = 0; iter < 3; iter++)
            {
                bool moved = false;
                var center = c.Round;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var t = new GridPos(center.X + dx, center.Y + dy);
                        if (Passable(t, mode)) continue;
                        float px = MathX.Max(t.X - 0.5f, MathX.Min(c.X, t.X + 0.5f));
                        float py = MathX.Max(t.Y - 0.5f, MathX.Min(c.Y, t.Y + 0.5f));
                        float ox = c.X - px, oy = c.Y - py;
                        float d2 = ox * ox + oy * oy;
                        if (d2 >= r * r) continue;
                        float d = (float)Math.Sqrt(d2);
                        if (d > 1e-5f) c = new Vec2(px + ox / d * r, py + oy / d * r);
                        else
                        {
                            // Centre inside the tile: push out along the shallowest axis.
                            float left = c.X - (t.X - 0.5f), right = (t.X + 0.5f) - c.X, up = c.Y - (t.Y - 0.5f), down = (t.Y + 0.5f) - c.Y;
                            float m = MathX.Min(MathX.Min(left, right), MathX.Min(up, down));
                            if (m == left) c = new Vec2(t.X - 0.5f - r, c.Y);
                            else if (m == right) c = new Vec2(t.X + 0.5f + r, c.Y);
                            else if (m == up) c = new Vec2(c.X, t.Y - 0.5f - r);
                            else c = new Vec2(c.X, t.Y + 0.5f + r);
                        }
                        moved = true;
                    }
                if (!moved) break;
            }
            return c;
        }

        private WaveStats StatsFor(Enemy e) => _activeByGlobal.TryGetValue(e.Wave, out var w) ? w.Stats : null;

        private void EnemyGone(Enemy e)
        {
            if (_activeByGlobal.TryGetValue(e.Wave, out var w)) w.Alive--;
        }

        private void DestroyWall(Wall w, int byWave)
        {
            w.Destroyed = true;
            w.DestroyedByWave = byWave;
            _occ[Layout.Index(w.Pos)] = Occupant.None;
            _fieldsDirty = true;
            EnsureFields();
            Events.Add(new SimEvent { Type = SimEventType.WallDestroyed, Pos = w.Pos });
        }

        private void Leak(Enemy e)
        {
            e.Leaked = true;
            var ws = StatsFor(e);
            if (ws != null) ws.Leaks++;
            EnemyGone(e);
            Run.Lives -= e.LeakDamage;
            Events.Add(new SimEvent { Type = SimEventType.EnemyLeaked, A = e.Id, At = e.Pos });
            if (Run.Lives <= 0)
            {
                Run.Lives = 0;
                Phase = SitePhase.Lost;
                Emit(SimEventType.SiteLost, Run.GlobalWave);
            }
        }

        private void TickPoison(Enemy e, float dt)
        {
            if (e.PoisonTime <= 0) return;
            float t = MathX.Min(dt, e.PoisonTime);
            e.PoisonTime -= dt;
            Damage(e, e.PoisonDps * t, DamageType.Poison, false);
        }

        // ------------------------------------------------------------------ towers & damage

        private void TickTower(Tower t, float dt)
        {
            t.Cooldown -= dt;
            if (t.Cooldown > 0) return;

            var def = t.Def;
            var origin = t.Pos.Center;
            Enemy target = SelectTarget(t);
            if (target == null) return;

            t.Cooldown = def.FireInterval;
            Events.Add(new SimEvent { Type = SimEventType.TowerFired, A = t.Id, B = target.Id, At = target.Pos });

            if (def.IsSplash)
            {
                var center = target.Pos;
                // Copy: kills during splash may spawn split children into Enemies.
                var hits = new List<Enemy>();
                foreach (var e in Enemies)
                    if (e.IsTargetable && !e.IsAir && Vec2.Distance(center, e.Pos) <= def.SplashRadius) hits.Add(e);
                foreach (var e in hits) t.DamageDealt += Damage(e, def.HitDamage, def.Damage, true);
            }
            else
            {
                t.DamageDealt += Damage(target, def.HitDamage, def.Damage, false);
                if (def.PoisonDps > 0 && target.IsActive)
                {
                    target.PoisonDps = MathX.Max(target.PoisonDps, def.PoisonDps);
                    target.PoisonTime = MathX.Max(target.PoisonTime, def.PoisonDuration);
                }
            }
        }

        /// <summary>Potential poison damage still to come on this enemy (after resistance).</summary>
        public static float PendingPoison(Enemy e) =>
            e.PoisonTime > 0 ? e.PoisonDps * e.PoisonTime * (1f - e.Resist[(int)DamageType.Poison]) : 0f;

        private bool InReach(Tower t, Enemy e) =>
            e.IsTargetable && (!e.IsAir || t.Def.CanHitAir) && Vec2.Distance(t.Pos.Center, e.Pos) <= t.Def.Range;

        /// <summary>Per-tower targeting. Hits are instant, so later towers in the same tick see updated HP (no overkill).</summary>
        public Enemy SelectTarget(Tower t)
        {
            var def = t.Def;
            var origin = t.Pos.Center;
            Enemy best = null;
            float bestScore = 0, bestTie = 0;

            foreach (var e in Enemies)
            {
                if (!InReach(t, e)) continue;
                float score, tie;
                switch (def.Targeting)
                {
                    case TargetingMode.Cluster:
                        // Most ground enemies inside the splash if centred on e; front-most breaks ties.
                        int count = 0;
                        foreach (var o in Enemies)
                            if (o.IsTargetable && !o.IsAir && Vec2.Distance(e.Pos, o.Pos) <= def.SplashRadius) count++;
                        score = -count;
                        tie = e.Remaining;
                        break;
                    case TargetingMode.LeastPoison:
                        // Least potential poison damage first; closest to the tower breaks ties.
                        score = PendingPoison(e);
                        tie = Vec2.Distance(origin, e.Pos);
                        break;
                    default: // First: closest to the base along its route
                        score = e.Remaining;
                        tie = 0;
                        break;
                }
                if (best == null || score < bestScore - 1e-4f || (Math.Abs(score - bestScore) <= 1e-4f && tie < bestTie))
                {
                    best = e; bestScore = score; bestTie = tie;
                }
            }
            return best;
        }

        private float Damage(Enemy e, float raw, DamageType type, bool splash)
        {
            if (!e.IsActive) return 0;
            float dealt = MathX.Min(e.Hp, raw * (1f - e.Resist[(int)type]));
            e.Hp -= dealt;
            var ws = StatsFor(e);
            if (ws != null)
            {
                ws.DamageByType[(int)type] += dealt;
                if (splash) ws.SplashDamage += dealt; else ws.SingleTargetDamage += dealt;
            }
            if (e.Hp <= 0.0001f) Kill(e);
            return dealt;
        }

        private void Kill(Enemy e)
        {
            e.Dead = true;
            Essence += e.Bounty;
            var ws = StatsFor(e);
            if (ws != null) ws.Kills++;
            EnemyGone(e);
            Events.Add(new SimEvent { Type = SimEventType.EnemyKilled, A = e.Id, At = e.Pos });

            if (e.Splits && !e.IsSplitChild)
            {
                for (int i = 0; i < 2; i++)
                {
                    var c = new Enemy
                    {
                        Id = _nextId++,
                        Mode = e.Mode == MoveMode.Siege ? MoveMode.Ground : e.Mode,
                        IsSplitChild = true,
                        MaxHp = e.MaxHp * _b.SplitChildHpFraction,
                        Speed = e.Speed * 1.15f,
                        Bounty = 0,
                        LeakDamage = 1,
                        Pos = e.Pos + new Vec2(i == 0 ? -0.12f : 0.12f, i == 0 ? 0.08f : -0.08f),
                        Radius = 0.15f,
                        Lane = i == 0 ? -0.2f : 0.2f,
                        Wave = e.Wave,
                        Tile = e.Tile,
                        Next = e.Next,
                        FlyTarget = e.FlyTarget,
                    };
                    c.Hp = c.MaxHp;
                    for (int k = 0; k < 3; k++) c.Resist[k] = e.Resist[k];
                    AddEnemy(c);
                }
            }
        }

        // ------------------------------------------------------------------ fields

        private float GroundCost(GridPos p) =>
            Layout.IsWalkable(p) && _occ[Layout.Index(p)] == Occupant.None ? 1f : float.PositiveInfinity;

        private float BurrowCost(GridPos p) =>
            Layout.IsWalkable(p) && _occ[Layout.Index(p)] != Occupant.Tower ? 1f : float.PositiveInfinity;

        private float SiegeCost(GridPos p)
        {
            if (!Layout.IsWalkable(p)) return float.PositiveInfinity;
            var o = _occ[Layout.Index(p)];
            return o == Occupant.Tower ? float.PositiveInfinity : o == Occupant.Wall ? 1f + _b.SiegeWallPathPenalty : 1f;
        }

        private void EnsureFields()
        {
            if (!_fieldsDirty) return;
            _ground.Compute(GroundCost);
            _burrow.Compute(BurrowCost);
            _siege.Compute(SiegeCost);
            _fieldsDirty = false;
        }

        private float ShortestSpawnDistance(FlowField f)
        {
            float best = float.PositiveInfinity;
            foreach (var s in Layout.Spawns) best = MathX.Min(best, f[s]);
            return best;
        }

        private GridPos NearestExit(GridPos from)
        {
            GridPos best = Layout.Exits[0];
            float bestD = float.MaxValue;
            foreach (var x in Layout.Exits)
            {
                float d = Vec2.Distance(from.Center, x.Center);
                if (d < bestD) { bestD = d; best = x; }
            }
            return best;
        }

        private void Emit(SimEventType type, int a) => Events.Add(new SimEvent { Type = type, A = a });
    }
}

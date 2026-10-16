using System.Collections.Generic;
using System.Linq;

namespace TD.Core
{
    /// <summary>
    /// Scripted players for the headless balance sim. Not smart, just consistent: the point is to compare
    /// a player who ignores the telegraphs (Stubborn) with one who responds to them (Adaptive).
    /// </summary>
    public sealed class SimpleBot
    {
        public readonly string Name;
        public readonly bool Adaptive;
        public readonly string Favorite;
        public float WallShare = 0.3f;

        public SimpleBot(string name, bool adaptive, string favorite)
        {
            Name = name; Adaptive = adaptive; Favorite = favorite;
        }

        /// <summary>Spends Essence. The simulator decides when to send waves (see RunSimulator).</summary>
        public void Build(SiteSim sim)
        {
            if (Adaptive && sim.RefundWindow.Count > 0)
            {
                foreach (var t in sim.Towers.ToList())
                    if (sim.RefundWindow.Contains(t.Def.Id)) sim.Sell(t.Pos);
            }

            int wallBudget = (int)(sim.Essence * WallShare);
            int spentOnWalls = 0;
            while (spentOnWalls + sim.WallCost <= wallBudget && sim.Essence >= sim.WallCost)
            {
                var best = BestWall(sim, out float gain);
                if (best == null || gain <= 0) break;
                int cost = sim.WallCost;
                if (!sim.PlaceWall(best.Value, out _)) break;
                spentOnWalls += cost;
            }

            for (int guard = 0; guard < 50; guard++)
            {
                string type = ChooseTower(sim);
                if (type == null || sim.Essence < sim.TowerCost(type)) break;
                var spot = BestTowerSpot(sim, Content.Tower(type));
                if (spot == null || !sim.PlaceTower(type, spot.Value, out _)) break;
            }
        }

        /// <summary>Bots send the next wave as soon as the field is clear (classic "call early when safe" play).</summary>
        public bool WantsNextWave(SiteSim sim) => sim.MoreWavesToCome && sim.Enemies.Count == 0;

        private string ChooseTower(SiteSim sim)
        {
            if (!Adaptive) return Favorite;

            var avoid = new HashSet<string>();
            foreach (var kv in sim.Enemy.Levels)
                if (kv.Value > 0) foreach (var id in AdaptationInfo.Counters(kv.Key)) avoid.Add(id);
            if (sim.Enemy.Pending != null)
                foreach (var id in AdaptationInfo.Counters(sim.Enemy.Pending.Kind)) avoid.Add(id);

            var options = sim.Run.OwnedTowers.Where(id => !avoid.Contains(id)).ToList();
            if (options.Count == 0) options = sim.Run.OwnedTowers.ToList();
            // Diversity: cheapest next copy wins (escalating copy cost does the rest).
            return options.OrderBy(id => sim.TowerCost(id)).ThenBy(id => id).FirstOrDefault();
        }

        private static GridPos? BestWall(SiteSim sim, out float gain)
        {
            float current = sim.GroundPathLength;
            GridPos? best = null;
            gain = 0;
            foreach (var p in NearPath(sim))
            {
                if (!sim.CanPlace(p, out _)) continue;
                float len = sim.PathLengthIfBlocked(p);
                if (float.IsPositiveInfinity(len)) continue;
                if (len - current > gain) { gain = len - current; best = p; }
            }
            return best;
        }

        private static GridPos? BestTowerSpot(SiteSim sim, TowerDef def)
        {
            var pathTiles = new HashSet<GridPos>();
            foreach (var s in sim.Layout.Spawns) foreach (var p in sim.GroundPath(s)) pathTiles.Add(p);

            GridPos? best = null;
            int bestScore = 0;
            foreach (var p in NearPath(sim))
            {
                if (!sim.CanPlace(p, out _)) continue;
                int score = 0;
                foreach (var q in pathTiles) if (Vec2.Distance(p.Center, q.Center) <= def.Range) score++;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private static IEnumerable<GridPos> NearPath(SiteSim sim)
        {
            var seen = new HashSet<GridPos>();
            foreach (var s in sim.Layout.Spawns)
                foreach (var p in sim.GroundPath(s))
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var n = new GridPos(p.X + dx, p.Y + dy);
                            if (sim.Layout.IsBuildable(n) && seen.Add(n)) yield return n;
                        }
        }
    }

    public sealed class RunResult
    {
        public string Bot;
        public int Seed;
        public int SitesCleared;
        public int WavesSurvived;
        public int LivesLeft;
        public int Gold;
        public readonly List<string> Log = new List<string>();
    }

    /// <summary>Plays a whole mini run headless with a bot. Same seed + same bot = same result.</summary>
    public static class RunSimulator
    {
        public static RunResult Play(int seed, SimpleBot bot, Layout[] layouts, Balance balance = null, float dt = 0.05f)
        {
            var run = new RunState(seed, balance);
            var result = new RunResult { Bot = bot.Name, Seed = seed };

            foreach (var layout in layouts)
            {
                var sim = new SiteSim(layout, run);
                result.Log.Add($"-- Site: {layout.Name}");
                int guard = 0;
                bot.Build(sim);
                sim.StartNextWave();
                while (sim.Phase != SitePhase.Won && sim.Phase != SitePhase.Lost && guard++ < 200000)
                {
                    sim.Tick(dt);
                    bool rebuild = false;
                    foreach (var ev in sim.Events)
                    {
                        Describe(ev, sim, result);
                        if (ev.Type == SimEventType.WaveEnded || ev.Type == SimEventType.AdaptationLanded) rebuild = true;
                    }
                    sim.Events.Clear();
                    if (rebuild && sim.Phase == SitePhase.Wave)
                    {
                        bot.Build(sim);
                        if (bot.WantsNextWave(sim)) sim.StartNextWave();
                    }
                }
                if (sim.Phase == SitePhase.Lost) break;
            }

            result.SitesCleared = run.SitesCleared;
            result.WavesSurvived = run.GlobalWave;
            result.LivesLeft = run.Lives;
            result.Gold = run.Gold;
            return result;
        }

        private static void Describe(SimEvent ev, SiteSim sim, RunResult r)
        {
            switch (ev.Type)
            {
                case SimEventType.WaveEnded:
                    var s = sim.LastWaveStats;
                    r.Log.Add($"   wave {ev.A + 1,2}: kills {s.Kills,3}  leaks {s.Leaks,2}  lives {sim.Run.Lives,2}  maze {s.MazeRatio:0.00}x  dmg P/F/V {Pct(s, 0)}/{Pct(s, 1)}/{Pct(s, 2)}  meter {sim.Enemy.MeterFraction:0.00}");
                    break;
                case SimEventType.AdaptationTelegraphed:
                    r.Log.Add($"   ! TELEGRAPH {AdaptationInfo.Name(ev.Telegraph.Kind)} lands wave {ev.Telegraph.LandsAtGlobalWave + 1}: {ev.Telegraph.Reason}");
                    break;
                case SimEventType.AdaptationLanded:
                    r.Log.Add($"   ! LANDED {ev.Text} (level {sim.Enemy.Level(ev.Telegraph.Kind)})");
                    break;
                case SimEventType.SiteLost:
                    r.Log.Add($"   X base destroyed on wave {ev.A + 1}");
                    break;
            }
        }

        private static string Pct(WaveStats s, int i) =>
            s.TotalDamage > 0 ? $"{System.Math.Round(100 * s.DamageByType[i] / s.TotalDamage),3}%" : "  0%";
    }
}

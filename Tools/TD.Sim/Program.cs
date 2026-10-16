using System;
using System.Collections.Generic;
using System.Linq;
using TD.Core;

namespace TD.Sim
{
    /// <summary>
    /// Headless balance sim. Plays the 3-layout prototype run with scripted bots across many seeds and
    /// compares a player who ignores the telegraphs with one who responds to them.
    /// Usage: dotnet run -c Release -- [runs=50] [--log]
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            int runs = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 50;
            bool log = args.Contains("--log");
            var layouts = Content.PrototypeLayouts();

            if (args.Contains("--bench"))
            {
                // How expensive is one simulation tick with N enemies on the field? (Open field, 6 towers, no walls.)
                foreach (int count in new[] { 100, 300, 1000, 3000 })
                {
                    var b = new Balance { StartingEssence = 99999, BaseEnemiesPerWave = count, EnemiesPerWaveStep = 0, SpawnInterval = 0.001f, HpGrowthPerWave = 50f, StartingLives = 1000000 };
                    var sim = new SiteSim(layouts[0], new RunState(1, b));
                    for (int x = 3; x <= 8; x++) sim.PlaceTower(x % 2 == 0 ? Content.Bolt : Content.Venom, new GridPos(x, 3), out _);
                    sim.StartNextWave();
                    for (int i = 0; i < 10; i++) { sim.Tick(0.05f); sim.Events.Clear(); }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int ticks = 0;
                    while (sw.ElapsedMilliseconds < 2000 && sim.Enemies.Count > 0) { sim.Tick(0.05f); sim.Events.Clear(); ticks++; }
                    Console.WriteLine($"{count,5} enemies spawned, {sim.Enemies.Count,5} alive at end, {ticks} ticks: {sw.Elapsed.TotalMilliseconds / ticks,8:0.000} ms per tick");
                }
                return;
            }

            if (args.Contains("--maps"))
            {
                // Per-map maze bias: play each layout three times in a row with different wall budgets.
                var shares = new[] { 0f, 0.15f, 0.3f, 0.45f, 0.6f };
                Console.WriteLine($"{"Map",-16}" + string.Concat(shares.Select(x => $"  walls {x * 100,3:0}%")));
                foreach (var l in layouts)
                {
                    var line = $"{l.Name,-16}";
                    foreach (var share in shares)
                    {
                        double waves = 0; int wins = 0;
                        for (int seed = 1; seed <= runs; seed++)
                        {
                            var r = RunSimulator.Play(seed, new SimpleBot("x", true, Content.Flame) { WallShare = share }, new[] { l, l, l });
                            waves += r.WavesSurvived; if (r.SitesCleared == 3) wins++;
                        }
                        line += $"  {waves / runs,4:0.0}w {100.0 * wins / runs,3:0}%";
                    }
                    Console.WriteLine(line);
                }
                return;
            }

            var bots = new List<Func<SimpleBot>>
            {
                () => new SimpleBot("Stubborn Flame", false, Content.Flame),
                () => new SimpleBot("Stubborn Bolt", false, Content.Bolt),
                () => new SimpleBot("Adaptive", true, Content.Flame),
                () => new SimpleBot("Adaptive no-maze", true, Content.Flame) { WallShare = 0f },
                () => new SimpleBot("Adaptive mazer", true, Content.Flame) { WallShare = 0.6f },
                () => new SimpleBot("Stubborn mazer", false, Content.Bolt) { WallShare = 0.6f },
            };

            if (log)
            {
                foreach (var make in bots)
                {
                    var r = RunSimulator.Play(1, make(), layouts);
                    Console.WriteLine($"=== {r.Bot} (seed 1)");
                    foreach (var line in r.Log) Console.WriteLine(line);
                    Console.WriteLine();
                }
            }

            int totalWaves = layouts.Length * new Balance().WavesPerSite;
            Console.WriteLine($"{runs} runs per bot, {layouts.Length} sites x {new Balance().WavesPerSite} waves = {totalWaves} waves per run");
            Console.WriteLine($"{"Bot",-18} {"Win%",6} {"AvgWaves",9} {"AvgLives",9} {"Top adaptations"}");
            foreach (var make in bots)
            {
                int wins = 0; double waves = 0, lives = 0;
                var counts = new Dictionary<string, int>();
                for (int seed = 1; seed <= runs; seed++)
                {
                    var bot = make();
                    var r = RunSimulator.Play(seed, bot, layouts);
                    if (r.SitesCleared == layouts.Length) wins++;
                    waves += r.WavesSurvived; lives += r.LivesLeft;
                    foreach (var line in r.Log.Where(l => l.Contains("LANDED")))
                    {
                        var name = line.Split(new[] { "LANDED " }, StringSplitOptions.None)[1].Split(' ')[0];
                        counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                    }
                }
                var top = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value}"));
                Console.WriteLine($"{make().Name,-18} {100.0 * wins / runs,5:0}% {waves / runs,9:0.0} {lives / runs,9:0.0}  {top}");
            }
        }
    }
}

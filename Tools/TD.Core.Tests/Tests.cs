using System;
using System.Linq;
using System.Reflection;
using TD.Core;

namespace TD.Core.Tests
{
    // Tiny self-contained runner (no NuGet needed). Every public static void method starting with "Test" runs.
    public static class Program
    {
        public static int Main()
        {
            var tests = typeof(CoreTests).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Test")).OrderBy(m => m.MetadataToken).ToList();
            int failed = 0;
            foreach (var t in tests)
            {
                try { t.Invoke(null, null); Console.WriteLine($"  PASS {t.Name}"); }
                catch (TargetInvocationException e) { failed++; Console.WriteLine($"  FAIL {t.Name}: {e.InnerException?.Message}"); }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
            return failed == 0 ? 0 : 1;
        }
    }

    internal static class Assert
    {
        public static void True(bool c, string msg) { if (!c) throw new Exception(msg); }
        public static void Equal<T>(T expected, T actual, string what) { if (!Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}"); }
    }

    public static class CoreTests
    {
        static Layout Corridor() => Layout.Parse("corridor",
            "#######",
            "S.....E",
            "#######");

        static Layout Room() => Layout.Parse("room",
            "#########",
            "S.......#",
            "#.......#",
            "#.......E",
            "#.......#",
            "#########");

        static void RunUntil(SiteSim sim, Func<bool> done, float maxSeconds = 300f)
        {
            for (float t = 0; t < maxSeconds && !done(); t += 0.05f) sim.Tick(0.05f);
        }

        static void PlayWave(SiteSim sim)
        {
            int cleared = sim.WavesCleared;
            sim.StartNextWave();
            RunUntil(sim, () => sim.WavesCleared > cleared || sim.Phase == SitePhase.Lost);
        }

        public static void TestLayoutParses()
        {
            var l = Room();
            Assert.Equal(9, l.Width, "width");
            Assert.Equal(1, l.Spawns.Count, "spawns");
            Assert.True(l.IsBuildable(new GridPos(3, 2)), "ground buildable");
            Assert.True(!l.IsBuildable(new GridPos(0, 1)), "spawn not buildable");
        }

        public static void TestBlockingThePathIsRejected()
        {
            var sim = new SiteSim(Corridor(), new RunState(1));
            Assert.True(!sim.PlaceWall(new GridPos(3, 1), out var reason), "corridor wall must be rejected");
            Assert.Equal("That would block the path", reason, "reason");
        }

        public static void TestWallLengthensPath()
        {
            var sim = new SiteSim(Room(), new RunState(1));
            float before = sim.GroundPathLength;
            for (int y = 1; y <= 3; y++) Assert.True(sim.PlaceWall(new GridPos(4, y), out _), "wall placed");
            Assert.True(sim.GroundPathLength > before, $"path should grow ({before} -> {sim.GroundPathLength})");
            Assert.True(sim.MazeRatio > 1f, "maze ratio > 1");
        }

        public static void TestWallCostEscalates()
        {
            var b = new Balance { StartingEssence = 999 };
            var sim = new SiteSim(Room(), new RunState(1, b));
            int first = sim.WallCost;
            int placed = 0;
            for (int x = 2; x <= 6 && placed < 5; x++) if (sim.PlaceWall(new GridPos(x, 4), out _)) placed++;
            Assert.Equal(5, placed, "walls placed");
            Assert.Equal(first + 1, sim.WallCost, "cost after 5 walls");
        }

        public static void TestCopyCostEscalates()
        {
            var b = new Balance { StartingEssence = 999 };
            var sim = new SiteSim(Room(), new RunState(1, b));
            int c0 = sim.TowerCost(Content.Bolt);
            sim.PlaceTower(Content.Bolt, new GridPos(2, 2), out _);
            Assert.True(sim.TowerCost(Content.Bolt) > c0, "second copy costs more");
            Assert.Equal(Content.Tower(Content.Flame).BaseCost, sim.TowerCost(Content.Flame), "other type unaffected");
        }

        public static void TestTowersKillAndPayEssence()
        {
            var b = new Balance { StartingEssence = 999, BaseEnemiesPerWave = 3, EnemiesPerWaveStep = 0 };
            var run = new RunState(1, b);
            var sim = new SiteSim(Room(), run);
            sim.PlaceTower(Content.Bolt, new GridPos(4, 2), out _);
            sim.PlaceTower(Content.Bolt, new GridPos(5, 2), out _);
            int before = sim.Essence;
            PlayWave(sim);
            Assert.Equal(b.StartingLives, run.Lives, "no leaks");
            Assert.True(sim.Essence >= before + 3 + b.WaveClearBonus, "bounty + clear bonus paid");
        }

        public static void TestUndefendedEnemiesLeak()
        {
            var b = new Balance { BaseEnemiesPerWave = 4, EnemiesPerWaveStep = 0 };
            var run = new RunState(1, b);
            var sim = new SiteSim(Corridor(), run);
            PlayWave(sim);
            Assert.Equal(b.StartingLives - 4, run.Lives, "lives after 4 leaks");
        }

        public static void TestResistanceReducesDamage()
        {
            var b = new Balance();
            var e = new AdaptationEngine(b, new Rng(1));
            e.Levels[AdaptationKind.ResistFire] = 1;
            Assert.Equal(b.ResistPerLevel, e.Resist(DamageType.Fire), "fire resist");
            Assert.Equal(0f, e.Resist(DamageType.Physical), "physical untouched");
            e.Levels[AdaptationKind.ResistFire] = 5;
            Assert.Equal(b.ResistCap, e.Resist(DamageType.Fire), "capped, never immune");
        }

        static WaveStats FireHeavy() { var s = new WaveStats(); s.DamageByType[1] = 90; s.DamageByType[0] = 10; s.SplashDamage = 90; s.SingleTargetDamage = 10; return s; }
        static WaveStats Balanced() { var s = new WaveStats(); s.DamageByType[0] = 34; s.DamageByType[1] = 33; s.DamageByType[2] = 33; s.SplashDamage = 33; s.SingleTargetDamage = 67; return s; }

        public static void TestFireBuildGetsScalesTelegraphedThenLanded()
        {
            var b = new Balance();
            var e = new AdaptationEngine(b, new Rng(3));
            Telegraph t = null;
            int wave = 0;
            for (; wave < 10 && t == null; wave++) t = e.RecordWave(FireHeavy(), wave);
            Assert.True(t != null, "a telegraph is issued");
            Assert.Equal(AdaptationKind.ResistFire, t.Kind, "counter targets fire");
            Assert.True(t.Reason.Contains("Flame"), "reason names the player's build");
            Assert.True(e.LandIfDue(t.LandsAtGlobalWave - 1) == null, "not before its wave");
            Assert.True(e.LandIfDue(t.LandsAtGlobalWave) != null, "lands on time");
            Assert.Equal(1, e.Level(AdaptationKind.ResistFire), "level 1");
        }

        public static void TestSpecializedBuildIsCounteredSooner()
        {
            int WavesUntilTelegraph(WaveStats s)
            {
                var e = new AdaptationEngine(new Balance(), new Rng(5));
                for (int w = 0; w < 20; w++) if (e.RecordWave(s, w) != null) return w;
                return 99;
            }
            int specialized = WavesUntilTelegraph(FireHeavy());
            int balanced = WavesUntilTelegraph(Balanced());
            Assert.True(specialized < balanced, $"specialized {specialized} should be < balanced {balanced}");
        }

        public static void TestLongMazeDrawsMazeCounter()
        {
            var e = new AdaptationEngine(new Balance(), new Rng(9));
            Telegraph t = null;
            for (int w = 0; w < 10 && t == null; w++)
            {
                var s = Balanced(); s.MazeRatio = 2.6f; s.Walls = 18;
                t = e.RecordWave(s, w);
            }
            Assert.True(t != null && (t.Kind == AdaptationKind.Siege || t.Kind == AdaptationKind.Flying || t.Kind == AdaptationKind.Burrow),
                $"maze build should draw a maze counter, got {t?.Kind}");
        }

        public static void TestSappersBreakWallsThatRespawnAfterWave()
        {
            var b = new Balance { BaseEnemiesPerWave = 6, EnemiesPerWaveStep = 0, SiegeFractionPerLevel = 1f, StartingEssence = 999, SiegeWallPathPenalty = 0.5f };
            var run = new RunState(1, b);
            run.Enemy.Levels[AdaptationKind.Siege] = 1;
            var sim = new SiteSim(Room(), run);
            // A wall line that forces a long detour: sappers prefer to dig straight through.
            for (int y = 1; y <= 3; y++) sim.PlaceWall(new GridPos(4, y), out _);
            int destroyed = 0, respawned = 0;
            sim.StartNextWave();
            for (float t = 0; t < 300 && sim.WavesCleared == 0; t += 0.05f)
            {
                sim.Tick(0.05f);
                destroyed += sim.Events.Count(ev => ev.Type == SimEventType.WallDestroyed);
                respawned += sim.Events.Count(ev => ev.Type == SimEventType.WallRespawned);
                sim.Events.Clear();
            }
            Assert.True(destroyed > 0, "sappers destroyed a wall");
            Assert.Equal(destroyed, respawned, "every destroyed wall respawned");
            Assert.Equal(3, sim.Walls.Values.Count(w => !w.Destroyed), "all walls standing after the wave");
        }

        public static void TestBurrowersAreUntargetableUnderWalls()
        {
            var b = new Balance { BaseEnemiesPerWave = 1, EnemiesPerWaveStep = 0, BurrowFractionPerLevel = 1f, StartingEssence = 999 };
            var run = new RunState(1, b);
            run.Enemy.Levels[AdaptationKind.Burrow] = 1;
            var sim = new SiteSim(Room(), run);
            for (int y = 1; y <= 3; y++) sim.PlaceWall(new GridPos(4, y), out _);
            sim.StartNextWave();
            bool sawUnderground = false;
            for (float t = 0; t < 60 && sim.WavesCleared == 0; t += 0.05f)
            {
                sim.Tick(0.05f);
                foreach (var e in sim.Enemies) if (e.Underground) { sawUnderground = true; Assert.True(!e.IsTargetable, "underground = untargetable"); }
            }
            Assert.True(sawUnderground, "burrower went under a wall");
        }

        public static void TestSplittersSpawnChildren()
        {
            var b = new Balance { BaseEnemiesPerWave = 1, EnemiesPerWaveStep = 0, SplitFractionPerLevel = 1f, StartingEssence = 999 };
            var run = new RunState(1, b);
            run.Enemy.Levels[AdaptationKind.Splitting] = 2;
            var sim = new SiteSim(Room(), run);
            sim.PlaceTower(Content.Bolt, new GridPos(2, 2), out _);
            sim.PlaceTower(Content.Bolt, new GridPos(3, 2), out _);
            sim.StartNextWave();
            int spawned = 0;
            for (float t = 0; t < 120 && sim.WavesCleared == 0; t += 0.05f)
            {
                sim.Tick(0.05f);
                spawned += sim.Events.Count(ev => ev.Type == SimEventType.EnemySpawned);
                sim.Events.Clear();
            }
            Assert.Equal(3, spawned, "1 parent + 2 children");
        }

        public static void TestRefundWindowGivesFullRefundOnCounteredTower()
        {
            var b = new Balance { StartingEssence = 999, BaseEnemiesPerWave = 1, EnemiesPerWaveStep = 0 };
            var run = new RunState(1, b);
            var sim = new SiteSim(Room(), run);
            sim.PlaceTower(Content.Flame, new GridPos(3, 2), out _);
            sim.PlaceTower(Content.Bolt, new GridPos(5, 2), out _);
            int flamePaid = sim.TowerAt(new GridPos(3, 2)).PaidCost;
            int boltPaid = sim.TowerAt(new GridPos(5, 2)).PaidCost;

            // Force a pending Scales that lands on the first wave.
            for (int w = 0; w < 10 && run.Enemy.Pending == null; w++) run.Enemy.RecordWave(FireHeavy(), -10 + w);
            Assert.True(run.Enemy.Pending != null && run.Enemy.Pending.Kind == AdaptationKind.ResistFire, "scales pending");
            PlayWave(sim);

            Assert.True(sim.RefundWindow.Contains(Content.Flame), "flame in refund window");
            Assert.Equal(flamePaid, sim.SellValue(new GridPos(3, 2)), "flame full refund");
            Assert.Equal((int)(boltPaid * b.SellRefund), sim.SellValue(new GridPos(5, 2)), "bolt normal refund");

            PlayWave(sim);
            Assert.True(!sim.RefundWindow.Contains(Content.Flame), "window closes after the next wave starts");
        }

        public static void TestFirstWaveWaitsForPlayerWithoutBonus()
        {
            var sim = new SiteSim(Room(), new RunState(1));
            int before = sim.Essence;
            for (int i = 0; i < 2000; i++) sim.Tick(0.05f); // 100 s
            Assert.Equal(SitePhase.Build, sim.Phase, "first wave never auto-starts");
            sim.StartNextWave();
            Assert.Equal(before, sim.Essence, "no early-call bonus on the first wave");
        }

        public static void TestEarlyCallPaysBonusFromSecondWave()
        {
            var b = new Balance { BaseEnemiesPerWave = 1, EnemiesPerWaveStep = 0 };
            var sim = new SiteSim(Room(), new RunState(1, b));
            sim.PlaceTower(Content.Bolt, new GridPos(4, 2), out _);
            PlayWave(sim);
            int before = sim.Essence;
            Assert.True(sim.EarlyCallBonus > 0, "bonus offered on wave 2");
            sim.StartNextWave();
            Assert.True(sim.Essence > before, "calling wave 2 early pays Essence");
        }

        static TD.Core.Enemy PutEnemy(SiteSim sim, float x, float y, float remaining)
        {
            var e = new TD.Core.Enemy { Id = 1000 + sim.Enemies.Count, Hp = 100, MaxHp = 100, Speed = 1, Pos = new Vec2(x, y), Remaining = remaining };
            var p = new GridPos((int)Math.Round(x), (int)Math.Round(y)); e.Tile = p; e.Next = p;
            sim.Enemies.Add(e);
            return e;
        }

        public static void TestVenomTargetsLeastPoisonThenClosest()
        {
            var sim = new SiteSim(Room(), new RunState(1, new Balance { StartingEssence = 999 }));
            sim.PlaceTower(Content.Venom, new GridPos(4, 2), out _);
            var tower = sim.Towers[0];
            var poisonedFront = PutEnemy(sim, 5, 2, 1f); poisonedFront.PoisonDps = 8; poisonedFront.PoisonTime = 2;
            var cleanFar = PutEnemy(sim, 6, 3, 9f);
            var cleanNear = PutEnemy(sim, 3, 2, 12f);
            Assert.True(sim.SelectTarget(tower) == cleanNear, "unpoisoned beats poisoned; nearest to tower breaks the tie");
            cleanNear.PoisonDps = 8; cleanNear.PoisonTime = 3; cleanFar.PoisonDps = 8; cleanFar.PoisonTime = 0.5f;
            Assert.True(sim.SelectTarget(tower) == cleanFar, "least potential poison damage wins");
        }

        public static void TestFlameTargetsMostEnemies()
        {
            var sim = new SiteSim(Room(), new RunState(1, new Balance { StartingEssence = 999 }));
            sim.PlaceTower(Content.Flame, new GridPos(4, 2), out _);
            var tower = sim.Towers[0];
            var loneFront = PutEnemy(sim, 5, 1, 1f);
            PutEnemy(sim, 3, 3, 10f); PutEnemy(sim, 3.5f, 3, 9f); var groupFront = PutEnemy(sim, 4, 3, 8f);
            var t = sim.SelectTarget(tower);
            Assert.True(t != loneFront, "flame ignores the lone front enemy");
            Assert.True(t == groupFront, "flame aims at the group (all three in splash); front-most of the group breaks the tie");
        }

        public static void TestBoltTargetsFirst()
        {
            var sim = new SiteSim(Room(), new RunState(1, new Balance { StartingEssence = 999 }));
            sim.PlaceTower(Content.Bolt, new GridPos(4, 2), out _);
            PutEnemy(sim, 3, 3, 10f); var front = PutEnemy(sim, 5, 3, 2f);
            Assert.True(sim.SelectTarget(sim.Towers[0]) == front, "bolt shoots the enemy closest to the base");
        }


        public static void TestFreeMovementCrossesAtExpectedSpeedAndNeverEntersWalls()
        {
            var b = new Balance { BaseEnemiesPerWave = 6, EnemiesPerWaveStep = 0, StartingEssence = 999, SpawnInterval = 0.3f };
            var run = new RunState(1, b);
            var sim = new SiteSim(Room(), run);
            for (int y = 1; y <= 3; y++) sim.PlaceWall(new GridPos(4, y), out _);
            float path = sim.GroundPathLength;
            sim.StartNextWave();
            float t = 0; bool offCentre = false;
            while (sim.WavesCleared == 0 && sim.Phase != SitePhase.Lost && t < 120)
            {
                sim.Tick(0.05f); t += 0.05f;
                foreach (var e in sim.Enemies)
                {
                    if (!e.IsActive) continue;
                    var tile = e.Pos.Round;
                    Assert.True(sim.OccupantAt(tile) == Occupant.None && sim.Layout.IsWalkable(tile), $"enemy inside a blocked tile at {e.Pos}");
                    if (Math.Abs(e.Pos.X - Math.Round(e.Pos.X)) > 0.15f && Math.Abs(e.Pos.Y - Math.Round(e.Pos.Y)) > 0.15f) offCentre = true;
                }
            }
            Assert.Equal(b.StartingLives - 6, run.Lives, "all 6 leak (no towers)");
            float ideal = path / Content.Crawler.Speed + 5 * 0.3f;
            Assert.True(t < ideal * 1.4f, $"took {t:0.0}s, ideal about {ideal:0.0}s");
            Assert.True(offCentre, "enemies move off the tile grid lines");
        }


        public static void TestPreviewPathIsTautAndEndsAtExit()
        {
            var sim = new SiteSim(Room(), new RunState(1));
            var open = sim.PreviewPath(sim.Layout.Spawns[0]);
            Assert.True(open.Count <= 4, $"open room should be a few straight segments, got {open.Count} points");
            Assert.True(sim.Layout[open[open.Count - 1].Round] == TileType.Exit, "ends at the exit");
            for (int y = 1; y <= 3; y++) sim.PlaceWall(new GridPos(4, y), out _);
            var walled = sim.PreviewPath(sim.Layout.Spawns[0]);
            Assert.True(walled.Count > open.Count, "a wall bends the line");
        }


        public static void TestWavesOverlapAndTimerSendsTheNext()
        {
            var b = new Balance { BaseEnemiesPerWave = 4, EnemiesPerWaveStep = 0, WaveInterval = 10f };
            var sim = new SiteSim(Room(), new RunState(1, b));
            sim.StartNextWave();
            for (int i = 0; i < 20; i++) sim.Tick(0.05f); // 1 s in: wave 1 still on the field
            Assert.True(sim.Enemies.Count > 0, "wave 1 is on the field");
            int before = sim.Essence;
            sim.StartNextWave();
            Assert.Equal(2, sim.WavesStarted, "wave 2 sent while wave 1 is running");
            Assert.Equal(2, sim.ActiveWaves, "both waves active");
            Assert.True(sim.Essence > before, "early call paid a bonus");
            for (int i = 0; i < 220; i++) sim.Tick(0.05f); // 11 s: the countdown sends wave 3 on its own
            Assert.Equal(3, sim.WavesStarted, "timer sent wave 3");
        }

        public static void TestRunIsDeterministic()
        {
            var a = RunSimulator.Play(42, new SimpleBot("a", true, Content.Flame), Content.PrototypeLayouts());
            var b = RunSimulator.Play(42, new SimpleBot("a", true, Content.Flame), Content.PrototypeLayouts());
            Assert.Equal(a.WavesSurvived, b.WavesSurvived, "waves");
            Assert.Equal(a.LivesLeft, b.LivesLeft, "lives");
            Assert.Equal(string.Join("\n", a.Log), string.Join("\n", b.Log), "log");
        }
    }
}

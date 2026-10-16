using System.Collections.Generic;

namespace TD.Core
{
    public enum DamageType { Physical = 0, Fire = 1, Poison = 2 }

    public enum MoveMode { Ground, Flying, Burrow, Siege }

    public enum TargetingMode
    {
        First,        // closest to the base along its route
        Cluster,      // where the splash catches the most enemies
        LeastPoison,  // least potential poison damage, then closest to the tower
    }

    public sealed class TowerDef
    {
        public string Id;
        public string DisplayName;
        public DamageType Damage;
        public int BaseCost;
        public float Range;          // tiles
        public float FireInterval;   // seconds
        public float HitDamage;
        public float SplashRadius;   // 0 = single target
        public float PoisonDps;      // damage over time applied on hit
        public float PoisonDuration;
        public bool CanHitAir;
        public TargetingMode Targeting = TargetingMode.First;
        public bool IsSplash => SplashRadius > 0;
    }

    public sealed class EnemyDef
    {
        public string Id;
        public float BaseHp;
        public float Speed;       // tiles per second
        public int Bounty;        // Essence per kill
        public int LeakDamage;    // base HP lost per leak
    }

    /// <summary>
    /// Every tuning number for the prototype in one place. Plain fields so the Unity side can mirror them
    /// into a ScriptableObject later, and the headless balance sim can sweep them.
    /// </summary>
    public sealed class Balance
    {
        // Economy
        public int StartingEssence = 40;
        // Prototype stand-in for run-level power (shops/relics come later): later sites start richer.
        public int SiteEssencePerGlobalWave = 15;
        public int WaveClearBonus = 12;
        public float CopyCostStep = 0.4f;          // each extra copy of a type costs +40% of base
        public int WallBaseCost = 2;
        public int WallsPerCostStep = 5;           // +1 Essence per 5 walls placed
        public float SellRefund = 0.6f;
        public float AdaptRefund = 1.0f;           // refund window when an adaptation lands
        public float WaveInterval = 25f;           // seconds between waves once the site has started
        public float EarlyCallEssencePerSecond = 0.25f;
        public float LeftoverToGold = 0.5f;

        // Base
        public int StartingLives = 20;

        // Walls
        public float WallHp = 40f;
        public float SiegeWallPathPenalty = 4f;    // how many tiles of detour a wall is "worth" to a sapper

        // Waves
        public int WavesPerSite = 5;
        public int BaseEnemiesPerWave = 8;
        public int EnemiesPerWaveStep = 1;
        public float HpGrowthPerWave = 0.16f;      // per global wave index across the run
        public float SpawnInterval = 0.8f;

        // Adaptation engine
        public float AdaptMeterThreshold = 3.0f;
        public float AdaptMeterBase = 0.5f;        // per wave, plus the dominant damage share (0..1)
        public int TelegraphLeadWaves = 2;
        public float ProfileSmoothing = 0.5f;      // EMA weight of the newest wave
        public int MaxAdaptationLevel = 2;

        // Adaptation strengths (per level)
        public float ResistPerLevel = 0.35f;
        public float ResistCap = 0.7f;
        public float ResistSpeedPenaltyPerLevel = 0.1f;
        public float FlyingFractionPerLevel = 0.3f;
        public float FlyingHpMult = 0.6f;
        public float FlyingSpeedMult = 1.1f;
        public float BurrowFractionPerLevel = 0.3f;
        public float BurrowSpeedMult = 0.8f;
        public float SiegeFractionPerLevel = 0.25f;
        public float SiegeWallDps = 20f;
        public float SiegeHpMult = 1.2f;
        public float SiegeSpeedMult = 0.9f;
        public float SplitFractionPerLevel = 0.4f;
        public float SplitParentHpMult = 0.7f;
        public float SplitChildHpFraction = 0.4f;
    }

    public static class Content
    {
        public const string Bolt = "bolt";
        public const string Flame = "flame";
        public const string Venom = "venom";

        public static readonly TowerDef[] Towers =
        {
            new TowerDef { Id = Bolt,  DisplayName = "Bolt",  Damage = DamageType.Physical, BaseCost = 10, Range = 3.0f, FireInterval = 0.5f, HitDamage = 10f, CanHitAir = true },
            new TowerDef { Id = Flame, DisplayName = "Flame", Damage = DamageType.Fire,     BaseCost = 12, Range = 2.0f, FireInterval = 1.0f, HitDamage = 12f, SplashRadius = 1.2f, CanHitAir = false, Targeting = TargetingMode.Cluster },
            new TowerDef { Id = Venom, DisplayName = "Venom", Damage = DamageType.Poison,   BaseCost = 12, Range = 2.5f, FireInterval = 1.2f, HitDamage = 2f,  PoisonDps = 8f, PoisonDuration = 3f, CanHitAir = true, Targeting = TargetingMode.LeastPoison },
        };

        public static readonly EnemyDef Crawler = new EnemyDef { Id = "crawler", BaseHp = 40f, Speed = 1.2f, Bounty = 1, LeakDamage = 1 };

        public static TowerDef Tower(string id)
        {
            foreach (var t in Towers) if (t.Id == id) return t;
            throw new KeyNotFoundException(id);
        }

        /// <summary>Three hand-made layouts for the prototype mini run (generator comes later).</summary>
        public static Layout[] PrototypeLayouts() => new[]
        {
            Layout.Parse("Open Field",
                "##############",
                "S............#",
                "#............#",
                "#............#",
                "#............E",
                "#............#",
                "#............#",
                "#............#",
                "##############"),
            Layout.Parse("Twin Gates",
                "##############",
                "S............#",
                "#............#",
                "#.....##.....#",
                "#.....##.....E",
                "#.....##.....#",
                "#............#",
                "S............#",
                "##############"),
            Layout.Parse("Broken Ground",
                "##############",
                "S...#........#",
                "#...#...##...#",
                "#.......##...#",
                "##..#........E",
                "#...#...#....#",
                "#.......#..#.#",
                "#...##.....#.#",
                "##############"),
        };
    }
}

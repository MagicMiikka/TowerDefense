using System.Collections.Generic;

namespace TD.Core
{
    public struct SpawnSpec
    {
        public MoveMode Mode;
        public bool Splits;
        public float Hp;
        public float Speed;
    }

    /// <summary>Turns (global wave index, enemy memory) into a spawn list. Adaptations change the mix, not the count.</summary>
    public static class WaveGenerator
    {
        public static List<SpawnSpec> Build(int globalWave, AdaptationEngine enemy, Balance b, Rng rng)
        {
            int count = b.BaseEnemiesPerWave + b.EnemiesPerWaveStep * globalWave;
            float hp = Content.Crawler.BaseHp * (1f + b.HpGrowthPerWave * globalWave);

            int resistLevels = enemy.Level(AdaptationKind.ResistPhysical) + enemy.Level(AdaptationKind.ResistFire) + enemy.Level(AdaptationKind.ResistPoison);
            float speed = Content.Crawler.Speed * MathX.Max(0.6f, 1f - b.ResistSpeedPenaltyPerLevel * resistLevels);

            float fly = b.FlyingFractionPerLevel * enemy.Level(AdaptationKind.Flying);
            float burrow = b.BurrowFractionPerLevel * enemy.Level(AdaptationKind.Burrow);
            float siege = b.SiegeFractionPerLevel * enemy.Level(AdaptationKind.Siege);
            float split = MathX.Min(0.8f, b.SplitFractionPerLevel * enemy.Level(AdaptationKind.Splitting));
            float modeTotal = fly + burrow + siege;
            if (modeTotal > 0.8f) { float k = 0.8f / modeTotal; fly *= k; burrow *= k; siege *= k; }

            var list = new List<SpawnSpec>(count);
            for (int i = 0; i < count; i++)
            {
                var s = new SpawnSpec { Mode = MoveMode.Ground, Hp = hp, Speed = speed };
                float r = rng.NextFloat();
                if (r < fly) { s.Mode = MoveMode.Flying; s.Hp *= b.FlyingHpMult; s.Speed *= b.FlyingSpeedMult; }
                else if (r < fly + burrow) { s.Mode = MoveMode.Burrow; s.Speed *= b.BurrowSpeedMult; }
                else if (r < fly + burrow + siege) { s.Mode = MoveMode.Siege; s.Hp *= b.SiegeHpMult; s.Speed *= b.SiegeSpeedMult; }

                if (rng.NextFloat() < split) { s.Splits = true; s.Hp *= b.SplitParentHpMult; }
                list.Add(s);
            }
            return list;
        }
    }
}

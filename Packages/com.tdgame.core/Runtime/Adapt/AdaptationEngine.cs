using System;
using System.Collections.Generic;

namespace TD.Core
{
    /// <summary>What the player's defence looked like during one wave. Collected by SiteSim.</summary>
    public sealed class WaveStats
    {
        public readonly float[] DamageByType = new float[3];
        public float SingleTargetDamage;
        public float SplashDamage;
        public float MazeRatio = 1f;   // ground path length / shortest possible path on this layout
        public int Walls;
        public int Towers;
        public int Kills;
        public int Leaks;

        public float TotalDamage => DamageByType[0] + DamageByType[1] + DamageByType[2];
    }

    /// <summary>
    /// The enemy's memory. Reads the player's build every wave, fills a meter (faster when the build is
    /// one-sided), then telegraphs a counter that lands a fixed number of waves later. Persists across
    /// every site of a run.
    /// </summary>
    public sealed class AdaptationEngine
    {
        private readonly Balance _b;
        private readonly Rng _rng;

        // Smoothed build profile (exponential moving average over waves)
        public readonly float[] DamageShare = new float[3];
        public float SingleTargetShare;
        public float MazeRatio = 1f;
        public float Walls;
        public float Towers;
        private bool _hasProfile;

        public float Meter { get; private set; }
        public Telegraph Pending { get; private set; }
        public readonly Dictionary<AdaptationKind, int> Levels = new Dictionary<AdaptationKind, int>();
        public readonly List<Telegraph> History = new List<Telegraph>();

        /// <summary>Temperament hook: multiplies candidate scores (Bulwark, Swarm, Cunning...). Default neutral.</summary>
        public readonly Dictionary<AdaptationKind, float> TemperamentWeights = new Dictionary<AdaptationKind, float>();

        public AdaptationEngine(Balance b, Rng rng) { _b = b; _rng = rng; }

        public int Level(AdaptationKind k) => Levels.TryGetValue(k, out var l) ? l : 0;

        public float Resist(DamageType t) =>
            MathX.Min(_b.ResistCap, Level(AdaptationInfo.ResistFor(t)) * _b.ResistPerLevel);

        public float MeterFraction => MathX.Clamp01(Meter / _b.AdaptMeterThreshold);

        /// <summary>Call at the start of a wave. Returns the adaptation that lands now, if any.</summary>
        public Telegraph LandIfDue(int globalWave)
        {
            if (Pending == null || globalWave < Pending.LandsAtGlobalWave) return null;
            var landed = Pending;
            Levels[landed.Kind] = Level(landed.Kind) + 1;
            History.Add(landed);
            Pending = null;
            return landed;
        }

        /// <summary>Call when a wave ends. Returns a new telegraph if one was issued.</summary>
        public Telegraph RecordWave(WaveStats s, int globalWave)
        {
            UpdateProfile(s);

            if (Pending != null) return null; // the meter rests while a counter is on its way

            float dominant = Math.Max(DamageShare[0], Math.Max(DamageShare[1], DamageShare[2]));
            Meter = MathX.Min(_b.AdaptMeterThreshold, Meter + _b.AdaptMeterBase + dominant);
            if (Meter < _b.AdaptMeterThreshold) return null;

            var pick = PickCounter(out float score);
            if (pick == null || score < 0.15f) return null;

            Meter = 0;
            Pending = new Telegraph
            {
                Kind = pick.Value,
                LandsAtGlobalWave = globalWave + _b.TelegraphLeadWaves,
                Reason = Explain(pick.Value),
            };
            return Pending;
        }

        private void UpdateProfile(WaveStats s)
        {
            float a = _hasProfile ? _b.ProfileSmoothing : 1f;
            float total = s.TotalDamage;
            for (int i = 0; i < 3; i++)
            {
                float share = total > 0 ? s.DamageByType[i] / total : 0;
                DamageShare[i] = Lerp(DamageShare[i], share, a);
            }
            float st = s.SingleTargetDamage + s.SplashDamage;
            SingleTargetShare = Lerp(SingleTargetShare, st > 0 ? s.SingleTargetDamage / st : 0, a);
            MazeRatio = Lerp(MazeRatio, s.MazeRatio, a);
            Walls = Lerp(Walls, s.Walls, a);
            Towers = Lerp(Towers, s.Towers, a);
            _hasProfile = true;
        }

        private static float Lerp(float from, float to, float t) => from + (to - from) * t;

        private float Maze01 => MathX.Clamp01((MazeRatio - 1f) / 1.5f);

        /// <summary>How strongly each counter answers the current build. Exposed for the debug overlay.</summary>
        public Dictionary<AdaptationKind, float> ScoreCandidates()
        {
            var scores = new Dictionary<AdaptationKind, float>();
            for (int i = 0; i < 3; i++)
                scores[AdaptationInfo.ResistFor((DamageType)i)] = DamageShare[i] >= 0.4f ? DamageShare[i] : 0f;
            scores[AdaptationKind.Flying] = 0.6f * Maze01 + 0.4f * DamageShare[(int)DamageType.Fire];
            scores[AdaptationKind.Siege] = 0.8f * Maze01 + 0.2f * MathX.Clamp01(Walls / 20f);
            scores[AdaptationKind.Burrow] = 0.7f * MathX.Clamp01(Walls / 15f) + 0.3f * Maze01;
            scores[AdaptationKind.Splitting] = 0.8f * SingleTargetShare;

            var keys = new List<AdaptationKind>(scores.Keys);
            foreach (var k in keys)
            {
                int lvl = Level(k);
                if (lvl >= _b.MaxAdaptationLevel) { scores[k] = 0; continue; }
                float w = TemperamentWeights.TryGetValue(k, out var tw) ? tw : 1f;
                scores[k] = scores[k] * (float)Math.Pow(0.6, lvl) * w; // diminishing: favour variety
            }
            return scores;
        }

        private AdaptationKind? PickCounter(out float bestScore)
        {
            var scores = ScoreCandidates();
            AdaptationKind? best = null;
            bestScore = 0;
            // Iterate in enum order for determinism; small jitter breaks near-ties.
            foreach (AdaptationKind k in Enum.GetValues(typeof(AdaptationKind)))
            {
                float s = scores[k];
                if (s <= 0) continue;
                s += _rng.NextFloat() * 0.05f;
                if (s > bestScore) { bestScore = s; best = k; }
            }
            return best;
        }

        private string Explain(AdaptationKind k)
        {
            string Pct(float v) => $"{Math.Round(v * 100)}%";
            switch (k)
            {
                case AdaptationKind.ResistPhysical: return $"Your Bolt towers dealt {Pct(DamageShare[0])} of the damage. The swarm grows plating.";
                case AdaptationKind.ResistFire: return $"Your Flame towers dealt {Pct(DamageShare[1])} of the damage. The swarm grows scales.";
                case AdaptationKind.ResistPoison: return $"Your Venom towers dealt {Pct(DamageShare[2])} of the damage. The swarm builds antitoxin.";
                case AdaptationKind.Flying: return $"Your maze stretches the path {MazeRatio:0.0}x. Some of the swarm grows wings.";
                case AdaptationKind.Siege: return $"Your maze stretches the path {MazeRatio:0.0}x. Sappers are coming for your walls.";
                case AdaptationKind.Burrow: return $"You rely on {Math.Round(Walls)} walls. The swarm learns to dig under them.";
                case AdaptationKind.Splitting: return $"{Pct(SingleTargetShare)} of your damage hits one target at a time. The swarm starts to split.";
                default: return "";
            }
        }
    }
}

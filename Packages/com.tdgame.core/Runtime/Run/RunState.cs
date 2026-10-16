using System.Collections.Generic;

namespace TD.Core
{
    /// <summary>Everything that survives from one site (layout) to the next within a run.</summary>
    public sealed class RunState
    {
        public readonly Balance Balance;
        public readonly Rng Rng;
        public readonly int Seed;
        public readonly AdaptationEngine Enemy;

        /// <summary>Tower types unlocked for this run (bought with Gold, drafted, or from a blessing).</summary>
        public readonly List<string> OwnedTowers = new List<string>();

        public int Gold;
        public int Lives;
        public int GlobalWave;     // wave counter across the whole run; drives HP scaling and telegraph timing
        public int SitesCleared;

        public RunState(int seed, Balance balance = null)
        {
            Seed = seed;
            Balance = balance ?? new Balance();
            Rng = new Rng(seed);
            Enemy = new AdaptationEngine(Balance, new Rng(seed ^ 0x5EED));
            Lives = Balance.StartingLives;
            OwnedTowers.AddRange(new[] { Content.Bolt, Content.Flame, Content.Venom });
        }

        public bool IsOver => Lives <= 0;
    }
}

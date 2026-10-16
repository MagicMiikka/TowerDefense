using System.Collections.Generic;

namespace TD.Core
{
    public enum AdaptationKind
    {
        ResistPhysical, ResistFire, ResistPoison, // counter-pressure on your top damage type
        Flying,     // ignores the maze; fragile; Flame can't hit it
        Burrow,     // passes under walls (untargetable there); slower
        Siege,      // sappers break walls (walls respawn after the wave)
        Splitting,  // splits on death; punishes single-target burst
    }

    public sealed class Telegraph
    {
        public AdaptationKind Kind;
        public int LandsAtGlobalWave;   // applied from this wave onward
        public string Reason;           // shown to the player: why the enemy is doing this
    }

    public static class AdaptationInfo
    {
        public static string Name(AdaptationKind k)
        {
            switch (k)
            {
                case AdaptationKind.ResistPhysical: return "Plating";
                case AdaptationKind.ResistFire: return "Scales";
                case AdaptationKind.ResistPoison: return "Antitoxin";
                case AdaptationKind.Flying: return "Wings";
                case AdaptationKind.Burrow: return "Burrowers";
                case AdaptationKind.Siege: return "Sappers";
                case AdaptationKind.Splitting: return "Brood";
                default: return k.ToString();
            }
        }

        public static AdaptationKind ResistFor(DamageType t) =>
            t == DamageType.Physical ? AdaptationKind.ResistPhysical :
            t == DamageType.Fire ? AdaptationKind.ResistFire : AdaptationKind.ResistPoison;

        /// <summary>What the adaptation counters: tower ids, or "wall". Drives the full-refund window.</summary>
        public static IReadOnlyList<string> Counters(AdaptationKind k)
        {
            switch (k)
            {
                case AdaptationKind.ResistPhysical: return new[] { Content.Bolt };
                case AdaptationKind.ResistFire: return new[] { Content.Flame };
                case AdaptationKind.ResistPoison: return new[] { Content.Venom };
                case AdaptationKind.Flying: return new[] { Content.Flame };
                case AdaptationKind.Burrow: return new[] { WallId };
                case AdaptationKind.Siege: return new[] { WallId };
                case AdaptationKind.Splitting: return new[] { Content.Bolt };
                default: return new string[0];
            }
        }

        public const string WallId = "wall";

        /// <summary>The trade-off: every adaptation opens a weakness. Shown in the UI next to the threat.</summary>
        public static string Weakness(AdaptationKind k)
        {
            switch (k)
            {
                case AdaptationKind.ResistPhysical:
                case AdaptationKind.ResistFire:
                case AdaptationKind.ResistPoison: return "Heavier: slower, so your maze is worth more.";
                case AdaptationKind.Flying: return "Fragile: low HP, but ignores your maze.";
                case AdaptationKind.Burrow: return "Slow, and exposed whenever it surfaces.";
                case AdaptationKind.Siege: return "Stops to dig: easy to hit while breaching.";
                case AdaptationKind.Splitting: return "Smaller pieces: splash damage shreds them.";
                default: return "";
            }
        }
    }
}

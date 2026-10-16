using TD.Core;
using UnityEngine;

namespace TD.Game
{
    /// <summary>
    /// One colour language for the whole prototype. Resources always use their colour, and everything the enemy
    /// does uses the enemy's colours: the same colour is used on the map and in the panel.
    /// </summary>
    public static class Palette
    {
        // Resources
        public static readonly Color Essence = new Color(0.72f, 0.58f, 1.00f);   // violet
        public static readonly Color Gold = new Color(1.00f, 0.82f, 0.30f);      // gold
        public static readonly Color Lives = new Color(1.00f, 0.45f, 0.45f);     // base health
        public static readonly Color Maze = new Color(0.85f, 0.75f, 0.55f);      // wall tan

        // The swarm
        public static readonly Color Swarm = new Color(1.00f, 0.50f, 0.35f);     // base enemy: coral
        public static readonly Color Wings = new Color(0.45f, 0.90f, 1.00f);
        public static readonly Color Burrowers = new Color(0.80f, 0.58f, 0.32f);
        public static readonly Color Sappers = new Color(0.70f, 0.50f, 1.00f);
        public static readonly Color Brood = new Color(1.00f, 0.45f, 0.85f);
        public static readonly Color Plating = new Color(0.62f, 0.74f, 0.92f);
        public static readonly Color Scales = new Color(1.00f, 0.72f, 0.28f);
        public static readonly Color Antitoxin = new Color(0.78f, 0.98f, 0.35f);

        public static Color Of(AdaptationKind k)
        {
            switch (k)
            {
                case AdaptationKind.ResistPhysical: return Plating;
                case AdaptationKind.ResistFire: return Scales;
                case AdaptationKind.ResistPoison: return Antitoxin;
                case AdaptationKind.Flying: return Wings;
                case AdaptationKind.Burrow: return Burrowers;
                case AdaptationKind.Siege: return Sappers;
                case AdaptationKind.Splitting: return Brood;
                default: return Swarm;
            }
        }

        /// <summary>IMGUI rich-text colour tag.</summary>
        public static string C(string text, Color c) => $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text}</color>";

        public static string Name(AdaptationKind k) => C(AdaptationInfo.Name(k), Of(k));
        public static string Ess(int amount) => C($"{amount} Essence", Essence);
    }
}

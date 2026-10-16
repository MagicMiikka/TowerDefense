namespace TD.Core
{
    public sealed class Enemy
    {
        public int Id;
        public int Wave;          // global index of the wave it belongs to
        public MoveMode Mode;
        public bool Splits;
        public bool IsSplitChild;
        public float MaxHp;
        public float Hp;
        public float Speed;
        public int Bounty;
        public int LeakDamage;
        public readonly float[] Resist = new float[3];

        public Vec2 Pos;          // free continuous position (tile centres sit on integer coordinates)
        public Vec2 Velocity;
        public float Radius = 0.22f;
        public float Lane;        // personal sideways offset so a group spreads across a corridor
        public GridPos Tile;      // tile the enemy currently stands on
        public GridPos Next;      // next tile its flow field points to (sappers dig into it when it's a wall)
        public GridPos FlyTarget; // exit a flyer is heading to
        public bool Breaching;    // sapper stopped, digging through the wall on Next
        public bool Underground;  // burrower currently under a wall: untargetable
        public float Remaining;   // distance to exit along its route; lowest = targeted first
        public float StuckTime;

        public float PoisonDps;
        public float PoisonTime;

        public bool Dead;
        public bool Leaked;

        public bool IsActive => !Dead && !Leaked;
        public bool IsTargetable => IsActive && !Underground;
        public bool IsAir => Mode == MoveMode.Flying;
    }

    public sealed class Tower
    {
        public int Id;
        public TowerDef Def;
        public GridPos Pos;
        public int PaidCost;
        public float Cooldown;
        public float DamageDealt;
    }

    public sealed class Wall
    {
        public GridPos Pos;
        public int PaidCost;
        public float Hp;
        public bool Destroyed;     // breached; respawns when the wave that breached it is over
        public int DestroyedByWave;
    }

    public enum SimEventType
    {
        WaveStarted, WaveEnded, EarlyCallBonus,
        EnemySpawned, EnemyKilled, EnemyLeaked,
        TowerFired, WallDestroyed, WallRespawned, WallRefunded,
        AdaptationTelegraphed, AdaptationLanded,
        SiteWon, SiteLost,
    }

    /// <summary>One thing that happened this tick, for the view layer (VFX, sounds, UI) and logs.</summary>
    public sealed class SimEvent
    {
        public SimEventType Type;
        public int A;          // enemy/tower id, or an amount
        public int B;          // secondary id
        public GridPos Pos;
        public Vec2 At;
        public Telegraph Telegraph;
        public string Text;

        public override string ToString() => $"{Type} {A} {B} {Text}";
    }
}

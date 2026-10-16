# Tower Defense prototype

A compact-grid mazing roguelite where the enemy evolves to counter your build.

## Layout

| Path | What it is |
|---|---|
| `Packages/com.tdgame.core/` | **Game logic, no Unity dependencies** (asmdef `TD.Core`, `noEngineReferences`). Grid, pathing, towers, enemies, waves, Essence, the adaptation engine, bots. |
| `Assets/_Project/Scripts/` | **Greybox view** (asmdef `TD.Game`). Draws the sim with coloured shapes, handles mouse and touch, IMGUI HUD. Throwaway. |
| `Tools/TD.Core.Tests/` | Tests for the core, runnable outside Unity. |
| `Tools/TD.Sim/` | Headless balance sim: bots play the run across many seeds. |

## Play the greybox

Open the project and press Play in any scene. A `Prototype Game` object creates itself.

- **1–5**: Wall, Bolt, Flame, Venom, Sell. **Click or tap** a tile to use the tool.
- **Space**: send the next wave (the first is manual; after that waves also come on a timer and can overlap). Sending early pays bonus Essence.
- **F**: speed x1/x2/x4. **P**: pause.

The left panel shows the swarm's learning meter, the incoming adaptation with its reason and weakness,
and (Debug) the counter scores it is weighing.

## Run tests and the balance sim (needs the .NET 8 SDK)

```
cd Tools/TD.Core.Tests && dotnet run -c Release
cd Tools/TD.Sim && dotnet run -c Release -- 50 --log
```

## Rules of the codebase

- Game rules live in `TD.Core` only. The view reads state and drains `SiteSim.Events`; it never decides anything.
- All tuning numbers are in `Balance` (`Runtime/Model/Defs.cs`).
- Layouts are ASCII (`.` ground, `#` rock, `S` spawn, `E` exit). Y grows downward; the view draws tile (x, y) at world (x, -y).
- Determinism: all randomness goes through the `Rng` class, seeded from the run.

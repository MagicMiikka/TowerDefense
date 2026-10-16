using System.Collections.Generic;
using TD.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TD.Game
{
    public enum BuildTool { Wall, Bolt, Flame, Venom, Sell }

    /// <summary>
    /// Greybox prototype: owns the run, ticks the core sim at a fixed step, handles pointer input
    /// (mouse and touch), and hands drawing to PrototypeGame.View.cs / PrototypeGame.Hud.cs.
    /// Creates itself in any scene that doesn't already have one, so pressing Play is enough.
    /// </summary>
    public partial class PrototypeGame : MonoBehaviour
    {
        private const float Step = 0.05f; // 20 sim ticks per second, independent of frame rate

        public int Seed = 1;

        private RunState _run;
        private Layout[] _layouts;
        private int _siteIndex;
        private SiteSim _sim;
        private float _accumulator;
        private int _speed = 1;
        private bool _paused;
        private BuildTool _tool = BuildTool.Wall;
        private GridPos? _hover;
        private readonly List<string> _log = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<PrototypeGame>() != null) return;
            new GameObject("Prototype Game").AddComponent<PrototypeGame>();
        }

        private void Start()
        {
            NewRun(Seed);
        }

        private void NewRun(int seed)
        {
            Seed = seed;
            _run = new RunState(seed);
            _layouts = Content.PrototypeLayouts();
            _siteIndex = 0;
            _log.Clear();
            StartSite();
        }

        private void StartSite()
        {
            _sim = new SiteSim(_layouts[_siteIndex], _run);
            _accumulator = 0;
            AddLog($"Site {_siteIndex + 1}/{_layouts.Length}: {_sim.Layout.Name}");
            RebuildView();
        }

        private void Update()
        {
            HandlePointer();
            HandleKeys();

            if (!_paused && _sim.Phase != SitePhase.Won && _sim.Phase != SitePhase.Lost)
            {
                _accumulator += Time.deltaTime * _speed;
                int guard = 0;
                while (_accumulator >= Step && guard++ < 40)
                {
                    _sim.Tick(Step);
                    _accumulator -= Step;
                    DrainEvents();
                }
            }

            SyncView();
        }

        private void DrainEvents()
        {
            foreach (var ev in _sim.Events)
            {
                switch (ev.Type)
                {
                    case SimEventType.WaveStarted:
                    case SimEventType.WaveEnded:
                    case SimEventType.WallDestroyed:
                    case SimEventType.WallRespawned:
                    case SimEventType.WallRefunded:
                    case SimEventType.AdaptationLanded:
                        MarkStaticDirty();
                        break;
                }
                switch (ev.Type)
                {
                    case SimEventType.WaveStarted: AddLog($"Wave {_sim.WavesStarted}/{_sim.WavesTotal} sent"); break;
                    case SimEventType.EarlyCallBonus: AddLog($"Early call: +{Palette.Ess(ev.A)}"); break;
                    case SimEventType.AdaptationTelegraphed: AddLog(Palette.C("WARNING: ", Palette.Swarm) + Palette.Name(ev.Telegraph.Kind) + Palette.C($" incoming. {ev.Text}", Palette.Swarm)); break;
                    case SimEventType.AdaptationLanded: AddLog(Palette.Name(ev.Telegraph.Kind) + Palette.C(" has landed.", Palette.Swarm) + " Countered pieces sell at full refund until you send the next wave."); break;
                    case SimEventType.WallDestroyed: AddLog(Palette.C("A sapper breached a wall", Palette.Sappers) + " (it respawns after that wave)"); break;
                    case SimEventType.WallRefunded: AddLog($"A breached wall couldn't respawn there: refunded {Palette.Ess(ev.A)}"); break;
                    case SimEventType.SiteWon: AddLog("Site cleared. " + Palette.C($"Gold is now {_run.Gold}", Palette.Gold)); break;
                    case SimEventType.SiteLost: AddLog(Palette.C("Your base fell.", Palette.Lives)); break;
                    case SimEventType.TowerFired: SpawnShot(ev); break;
                }
            }
            _sim.Events.Clear();
        }

        private void AddLog(string line)
        {
            _log.Add(line);
            if (_log.Count > 8) _log.RemoveAt(0);
        }

        // ------------------------------------------------------------------ input

        private void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.digit1Key.wasPressedThisFrame) _tool = BuildTool.Wall;
            if (kb.digit2Key.wasPressedThisFrame) _tool = BuildTool.Bolt;
            if (kb.digit3Key.wasPressedThisFrame) _tool = BuildTool.Flame;
            if (kb.digit4Key.wasPressedThisFrame) _tool = BuildTool.Venom;
            if (kb.digit5Key.wasPressedThisFrame) _tool = BuildTool.Sell;
            if (kb.spaceKey.wasPressedThisFrame) _sim.StartNextWave();
            if (kb.pKey.wasPressedThisFrame) _paused = !_paused;
            if (kb.fKey.wasPressedThisFrame) _speed = _speed >= 4 ? 1 : _speed * 2;
        }

        private void HandlePointer()
        {
            _hover = null;
            var pointer = Pointer.current;
            var cam = Camera.main;
            if (pointer == null || cam == null) return;

            Vector2 screen = pointer.position.ReadValue();
            if (IsOverHud(screen)) return;

            Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -cam.transform.position.z));
            var p = new GridPos(Mathf.RoundToInt(world.x), Mathf.RoundToInt(-world.y));
            if (!_sim.Layout.InBounds(p)) return;
            _hover = p;

            if (pointer.press.wasPressedThisFrame) Apply(_tool, p);
        }

        private void Apply(BuildTool tool, GridPos p)
        {
            string reason = null;
            bool ok;
            switch (tool)
            {
                case BuildTool.Wall: ok = _sim.PlaceWall(p, out reason); break;
                case BuildTool.Bolt: ok = _sim.PlaceTower(Content.Bolt, p, out reason); break;
                case BuildTool.Flame: ok = _sim.PlaceTower(Content.Flame, p, out reason); break;
                case BuildTool.Venom: ok = _sim.PlaceTower(Content.Venom, p, out reason); break;
                default: ok = _sim.Sell(p); if (!ok) reason = "Nothing to sell there"; break;
            }
            if (!ok && reason != null) Toast(reason);
            if (ok) MarkStaticDirty();
        }

        private static string TowerIdFor(BuildTool t) =>
            t == BuildTool.Bolt ? Content.Bolt : t == BuildTool.Flame ? Content.Flame : t == BuildTool.Venom ? Content.Venom : null;

        private void NextSite()
        {
            _siteIndex++;
            if (_siteIndex >= _layouts.Length) { AddLog("Run complete! Starting a new seed."); NewRun(Seed + 1); return; }
            StartSite();
        }
    }
}

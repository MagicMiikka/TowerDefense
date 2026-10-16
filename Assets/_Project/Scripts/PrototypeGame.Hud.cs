using System.Linq;
using TD.Core;
using UnityEngine;

namespace TD.Game
{
    // Throwaway IMGUI HUD for the greybox. Real UI (UI Toolkit, touch-sized) replaces it after the prototype.
    public partial class PrototypeGame
    {
        private const float HudWidthUnscaled = 320f;
        private bool _showDebug = true;
        private string _toast;
        private float _toastUntil;
        private GUIStyle _wrap, _title, _small, _baseHp, _button;

        private float GuiScale => Mathf.Max(0.75f, Screen.height / 720f);
        private float HudWidth => HudWidthUnscaled * GuiScale;

        private bool IsOverHud(Vector2 screen) => screen.x <= HudWidth;

        private void Toast(string msg) { _toast = msg; _toastUntil = Time.time + 1.6f; }

        private void OnGUI()
        {
            if (_sim == null) return;
            EnsureStyles();
            float s = GuiScale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float h = Screen.height / s;

            GUI.Box(new Rect(0, 0, HudWidthUnscaled, h), GUIContent.none);
            GUILayout.BeginArea(new Rect(8, 8, HudWidthUnscaled - 16, h - 16));

            GUILayout.Label($"{_sim.Layout.Name}   (site {_siteIndex + 1}/{_layouts.Length}, seed {Seed})", _title);
            GUILayout.Label($"{Palette.C($"Lives {_run.Lives}", Palette.Lives)}    {Palette.C($"Essence {_sim.Essence}", Palette.Essence)}    {Palette.C($"Gold {_run.Gold}", Palette.Gold)}", _title);
            GUILayout.Label($"Waves sent {_sim.WavesStarted}/{_sim.WavesTotal}, cleared {_sim.WavesCleared}    {Palette.C($"Maze {_sim.MazeRatio:0.00}x", Palette.Maze)}", _wrap);

            DrawPhase();
            GUILayout.Space(6);
            DrawTools();
            GUILayout.Space(6);
            DrawEnemyMind();
            GUILayout.Space(6);
            foreach (var line in _log) GUILayout.Label(line, _small);

            GUILayout.FlexibleSpace();
            GUILayout.Label("Keys: 1-5 tools, Space send next wave, F speed, P pause", _small);
            GUILayout.EndArea();

            DrawBaseHealth(s);

            if (_toast != null && Time.time < _toastUntil)
            {
                float w = Screen.width / s;
                GUI.Label(new Rect(HudWidthUnscaled + (w - HudWidthUnscaled) * 0.5f - 150, 10, 300, 30), _toast, _title);
            }
        }

        /// <summary>Base health drawn right on the exit tile(s).</summary>
        private void DrawBaseHealth(float scale)
        {
            var cam = Camera.main;
            if (cam == null) return;
            string text = _run.Lives.ToString(); // colour comes from the style (lives colour)
            foreach (var exit in _sim.Layout.Exits)
            {
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(exit.X, -exit.Y, 0));
                float x = sp.x / scale, y = (Screen.height - sp.y) / scale;
                var r = new Rect(x - 40, y - 16, 80, 32);
                var shadow = new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height);
                var prev = GUI.color;
                GUI.color = Color.black;
                GUI.Label(shadow, text, _baseHp);
                GUI.color = prev;
                GUI.Label(r, text, _baseHp);
            }
        }

        private void DrawPhase()
        {
            switch (_sim.Phase)
            {
                case SitePhase.Build:
                    if (GUILayout.Button("Start first wave (when you're ready)", _button, GUILayout.Height(30))) _sim.StartNextWave();
                    break;
                case SitePhase.Wave:
                    if (_sim.MoreWavesToCome)
                    {
                        if (GUILayout.Button($"Send next wave now (+{Palette.Ess(_sim.EarlyCallBonus)})   auto in {_sim.NextWaveTimer:0}s", _button, GUILayout.Height(30)))
                            _sim.StartNextWave();
                    }
                    else GUILayout.Label("Last wave sent. Hold the line!");
                    GUILayout.Label(Palette.C($"{_sim.Enemies.Count(e => e.IsActive)} enemies on the field, {_sim.ActiveWaves} wave(s) active", Palette.Swarm), _wrap);
                    break;
                case SitePhase.Won:
                    if (GUILayout.Button(_siteIndex + 1 < _layouts.Length ? "Site cleared! Next site" : "Run won! New run", _button, GUILayout.Height(30))) NextSite();
                    break;
                case SitePhase.Lost:
                    if (GUILayout.Button(Palette.C("Base destroyed.", Palette.Lives) + " Restart run (new seed)", _button, GUILayout.Height(30))) NewRun(Seed + 1);
                    break;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_paused ? "Resume" : "Pause")) _paused = !_paused;
            if (GUILayout.Button($"Speed x{_speed}")) _speed = _speed >= 4 ? 1 : _speed * 2;
            _showDebug = GUILayout.Toggle(_showDebug, "Debug");
            GUILayout.EndHorizontal();
        }

        private void DrawTools()
        {
            GUILayout.Label("Build", _title);
            string wallRefund = _sim.RefundWindow.Contains(AdaptationInfo.WallId) ? Palette.C("  FULL REFUND", Palette.Gold) : "";
            ToolButton(BuildTool.Wall, $"1  {Palette.C("Wall", Palette.Maze)}  {Palette.C(_sim.WallCost.ToString(), Palette.Essence)}{wallRefund}", WallCol);
            ToolButton(BuildTool.Bolt, ToolLabel(BuildTool.Bolt, "2", "physical, single target"), BoltCol);
            ToolButton(BuildTool.Flame, ToolLabel(BuildTool.Flame, "3", "fire splash, can't hit air"), FlameCol);
            ToolButton(BuildTool.Venom, ToolLabel(BuildTool.Venom, "4", "poison over time"), VenomCol);
            ToolButton(BuildTool.Sell, _hover.HasValue && _sim.SellValue(_hover.Value) > 0 ? $"5  Sell  +{Palette.C(_sim.SellValue(_hover.Value).ToString(), Palette.Essence)}" : "5  Sell", Color.yellow);
        }

        private string ToolLabel(BuildTool tool, string key, string blurb)
        {
            string id = TowerIdFor(tool);
            string refund = _sim.RefundWindow.Contains(id) ? Palette.C("  FULL REFUND", Palette.Gold) : "";
            string name = Palette.C(Content.Tower(id).DisplayName, TowerColor(id));
            return $"{key}  {name}  {Palette.C(_sim.TowerCost(id).ToString(), Palette.Essence)}  x{_sim.Copies(id)}  {blurb}{refund}";
        }

        private void ToolButton(BuildTool tool, string label, Color color)
        {
            bool selected = _tool == tool;
            var prevBg = GUI.backgroundColor;
            var prevContent = GUI.contentColor;
            // The dark editor skin barely shows backgroundColor, so the selected tool also gets coloured text and a marker.
            GUI.backgroundColor = selected ? color * 2f : Color.Lerp(color, Color.gray, 0.7f);
            GUI.contentColor = Color.white; // label colours come from rich text
            if (GUILayout.Button(selected ? $"<b>>  {label}  <</b>" : label, _button, GUILayout.Height(26))) _tool = tool;
            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
        }

        private void DrawEnemyMind()
        {
            var mind = _sim.Enemy;
            string Sw(string t) => Palette.C(t, Palette.Swarm);
            GUILayout.Label(Sw("The swarm"), _title);

            var bar = GUILayoutUtility.GetRect(HudWidthUnscaled - 16, 12);
            GUI.Box(bar, GUIContent.none);
            var prev = GUI.color;
            GUI.color = mind.Pending != null ? Palette.Of(mind.Pending.Kind) : Palette.Swarm;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * (mind.Pending != null ? 1f : mind.MeterFraction), bar.height), Texture2D.whiteTexture);
            GUI.color = prev;
            GUILayout.Label(Sw(mind.Pending != null ? "Adapting..." : "Learning your build (fills faster when one damage type dominates)"), _small);

            if (mind.Pending != null)
            {
                int inWaves = mind.Pending.LandsAtGlobalWave - _run.GlobalWave;
                string when = inWaves <= 0 ? "arrives with the next wave you send" : $"arrives in {inWaves + 1} waves";
                GUILayout.Label(Sw("INCOMING: ") + Palette.Name(mind.Pending.Kind) + Sw($"  ({when})"), _title);
                GUILayout.Label(Sw(mind.Pending.Reason), _wrap);
                GUILayout.Label(Palette.C("Weakness: " + AdaptationInfo.Weakness(mind.Pending.Kind), Palette.Of(mind.Pending.Kind)), _wrap);
            }

            var landed = mind.Levels.Where(kv => kv.Value > 0).Select(kv => Palette.C($"{AdaptationInfo.Name(kv.Key)} {kv.Value}", Palette.Of(kv.Key))).ToList();
            GUILayout.Label(Sw("Evolved: ") + (landed.Count > 0 ? string.Join(Sw(", "), landed) : Sw("nothing yet")), _wrap);

            if (_sim.RefundWindow.Count > 0)
                GUILayout.Label(Palette.C("Full refund on: " + string.Join(", ", _sim.RefundWindow), Palette.Gold), _wrap);

            if (_showDebug)
            {
                var scores = mind.ScoreCandidates().OrderByDescending(kv => kv.Value).Take(4)
                    .Select(kv => $"{Palette.Name(kv.Key)} {kv.Value:0.00}");
                GUILayout.Label("Debug, counter scores: " + string.Join("  ", scores), _small);
                GUILayout.Label($"Debug, profile P/F/V {mind.DamageShare[0]:0.00}/{mind.DamageShare[1]:0.00}/{mind.DamageShare[2]:0.00}  single {mind.SingleTargetShare:0.00}  maze {mind.MazeRatio:0.00}", _small);
            }
        }

        private void EnsureStyles()
        {
            if (_wrap != null) return;
            _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
            _small = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11, richText = true };
            _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = true, richText = true };
            _button = new GUIStyle(GUI.skin.button) { richText = true };
            _baseHp = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 22, alignment = TextAnchor.MiddleCenter };
            _baseHp.normal.textColor = Palette.Lives;
        }
    }
}

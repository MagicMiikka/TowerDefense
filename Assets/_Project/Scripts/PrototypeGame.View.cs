using System.Collections.Generic;
using TD.Core;
using UnityEngine;

namespace TD.Game
{
    // Greybox rendering: coloured squares and circles generated at runtime, no assets needed.
    // World space: tile (x, y) is drawn at (x, -y), one unit per tile.
    public partial class PrototypeGame
    {
        private static readonly Color Ground = new Color(0.20f, 0.22f, 0.25f);
        private static readonly Color Rock = new Color(0.07f, 0.07f, 0.08f);
        private static readonly Color SpawnCol = new Color(0.75f, 0.20f, 0.20f);
        private static readonly Color ExitCol = new Color(0.20f, 0.70f, 0.35f);
        private static readonly Color WallCol = new Color(0.66f, 0.58f, 0.44f);
        private static readonly Color BoltCol = new Color(0.45f, 0.75f, 1.00f);
        private static readonly Color FlameCol = new Color(1.00f, 0.55f, 0.15f);
        private static readonly Color VenomCol = new Color(0.55f, 0.90f, 0.30f);
        private static readonly Color PathCol = new Color(1f, 1f, 1f, 0.35f);

        private Sprite _square, _circle;
        private Material _unlit;
        private Material _lineMat;
        private Transform _root, _staticRoot, _dynamicRoot;
        private bool _staticDirty = true;
        private readonly Dictionary<int, EnemyView> _enemyViews = new Dictionary<int, EnemyView>();

        private sealed class EnemyView
        {
            public SpriteRenderer Body, Ring, BarBack, Hp, Poison;
        }

        private const float BarWidth = 1.3f;   // in body-local units (bars are children of the body)
        private const float BarHeight = 0.16f;
        private const float BarY = 0.75f;
        private static readonly Color HpCol = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color PoisonCol = new Color(0.45f, 0.95f, 0.30f);
        private static readonly Color BarBackCol = new Color(0f, 0f, 0f, 0.6f);
        private readonly List<(SpriteRenderer sr, float until)> _shots = new List<(SpriteRenderer, float)>();
        private SpriteRenderer _hoverView;
        private readonly List<SpriteRenderer> _exitViews = new List<SpriteRenderer>();
        private int _lastScreenW, _lastScreenH;

        public static Color TowerColor(string id) => id == Content.Bolt ? BoltCol : id == Content.Flame ? FlameCol : VenomCol;

        private void RebuildView()
        {
            EnsureAssets();
            if (_root != null) Destroy(_root.gameObject);
            _root = new GameObject("View").transform;
            _staticRoot = new GameObject("Static").transform; _staticRoot.SetParent(_root);
            _dynamicRoot = new GameObject("Dynamic").transform; _dynamicRoot.SetParent(_root);
            _enemyViews.Clear();
            _shots.Clear();
            _exitViews.Clear();

            var l = _sim.Layout;
            for (int y = 0; y < l.Height; y++)
                for (int x = 0; x < l.Width; x++)
                {
                    var p = new GridPos(x, y);
                    var c = l[p] == TileType.Rock ? Rock : l[p] == TileType.Spawn ? SpawnCol : l[p] == TileType.Exit ? ExitCol : Ground;
                    var tile = MakeSprite(_root, _square, World(p, 0), 0.96f, c, 0);
                    if (l[p] == TileType.Exit) _exitViews.Add(tile);
                }

            _hoverView = MakeSprite(_root, _square, Vector3.zero, 1f, Color.clear, 5);
            FitCamera();
            _staticDirty = true;
        }

        private void MarkStaticDirty() => _staticDirty = true;

        private void SyncView()
        {
            if (_sim == null || _root == null) return;
            if (_staticDirty) RebuildStatic();
            if (Screen.width != _lastScreenW || Screen.height != _lastScreenH) FitCamera();
            SyncEnemies();
            // The base (exit) shifts from green to red as it loses health.
            float lifeF = Mathf.Clamp01(_run.Lives / (float)Mathf.Max(1, _run.Balance.StartingLives));
            foreach (var x in _exitViews) x.color = Color.Lerp(new Color(0.75f, 0.12f, 0.12f), ExitCol, lifeF);
            SyncShots();
            SyncHover();
        }

        private void RebuildStatic()
        {
            _staticDirty = false;
            for (int i = _staticRoot.childCount - 1; i >= 0; i--) Destroy(_staticRoot.GetChild(i).gameObject);

            foreach (var s in _sim.Layout.Spawns)
                MakePathLine(_sim.PreviewPath(s));

            foreach (var w in _sim.Walls.Values)
            {
                var c = w.Destroyed ? new Color(WallCol.r, WallCol.g, WallCol.b, 0.2f) : WallCol;
                if (_sim.RefundWindow.Contains(AdaptationInfo.WallId) && !w.Destroyed) c = Color.Lerp(c, Color.yellow, 0.35f);
                MakeSprite(_staticRoot, _square, World(w.Pos, 0), 0.86f, c, 2);
            }

            foreach (var t in _sim.Towers)
            {
                var c = TowerColor(t.Def.Id);
                if (_sim.RefundWindow.Contains(t.Def.Id)) c = Color.Lerp(c, Color.yellow, 0.45f);
                MakeSprite(_staticRoot, _square, World(t.Pos, 0), 0.8f, c * 0.6f + Color.black * 0.4f, 2);
                MakeSprite(_staticRoot, _circle, World(t.Pos, 0), 0.55f, c, 3);
            }
        }

        private void SyncEnemies()
        {
            var alive = new HashSet<int>();
            foreach (var e in _sim.Enemies)
            {
                if (!e.IsActive) continue;
                alive.Add(e.Id);
                if (!_enemyViews.TryGetValue(e.Id, out var v))
                {
                    float size = e.IsSplitChild ? 0.32f : e.Mode == MoveMode.Flying ? 0.38f : 0.5f;
                    v = new EnemyView { Body = MakeSprite(_dynamicRoot, _circle, Vector3.zero, size, EnemyColor(e), 6) };
                    v.Ring = MakeSprite(v.Body.transform, _circle, Vector3.zero, 1.45f, Color.clear, 5);
                    v.Ring.transform.localPosition = Vector3.zero;
                    v.BarBack = MakeBar(v.Body.transform, BarBackCol, 7);
                    v.Hp = MakeBar(v.Body.transform, HpCol, 8);
                    v.Poison = MakeBar(v.Body.transform, PoisonCol, 9);
                    SetBar(v.BarBack, 0f, 1f);
                    _enemyViews[e.Id] = v;
                }
                v.Body.transform.position = new Vector3(e.Pos.X, -e.Pos.Y, 0);
                var col = EnemyColor(e);
                if (e.Underground) col.a = 0.3f;
                v.Body.color = col;
                var ring = ResistRing(e);
                v.Ring.enabled = ring.HasValue;
                if (ring.HasValue) { var rc = ring.Value; rc.a = e.Underground ? 0.25f : 0.95f; v.Ring.color = rc; }

                // Red = health that will remain, green = health the active poison is still going to take.
                float max = Mathf.Max(1f, e.MaxHp);
                float poison = Mathf.Min(SiteSim.PendingPoison(e), e.Hp);
                float redF = Mathf.Clamp01((e.Hp - poison) / max);
                float greenF = Mathf.Clamp01(poison / max);
                SetBar(v.Hp, 0f, redF);
                SetBar(v.Poison, redF, greenF);
            }

            var dead = new List<int>();
            foreach (var kv in _enemyViews) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var id in dead) { Destroy(_enemyViews[id].Body.gameObject); _enemyViews.Remove(id); }
        }

        /// <summary>Body colour = how the enemy moves (or splits), in the same colours the panel uses for those adaptations.</summary>
        private static Color EnemyColor(TD.Core.Enemy e)
        {
            Color c;
            switch (e.Mode)
            {
                case MoveMode.Flying: c = Palette.Wings; break;
                case MoveMode.Burrow: c = Palette.Burrowers; break;
                case MoveMode.Siege: c = Palette.Sappers; break;
                default: c = Palette.Swarm; break;
            }
            if (e.Splits || e.IsSplitChild) c = Color.Lerp(c, Palette.Brood, 0.6f);
            return c;
        }

        /// <summary>Resistances show as a coloured ring around the body (strongest one wins).</summary>
        private static Color? ResistRing(TD.Core.Enemy e)
        {
            int best = -1; float bestR = 0f;
            for (int i = 0; i < 3; i++) if (e.Resist[i] > bestR) { bestR = e.Resist[i]; best = i; }
            if (best < 0) return null;
            return Palette.Of(AdaptationInfo.ResistFor((DamageType)best));
        }

        private void SpawnShot(SimEvent ev)
        {
            if (_dynamicRoot == null) return;
            Tower tower = null;
            foreach (var t in _sim.Towers) if (t.Id == ev.A) { tower = t; break; }
            var col = tower != null ? TowerColor(tower.Def.Id) : Color.white;
            float size = tower != null && tower.Def.IsSplash ? tower.Def.SplashRadius * 2f : 0.25f;
            col.a = tower != null && tower.Def.IsSplash ? 0.35f : 0.9f;
            var sr = MakeSprite(_dynamicRoot, _circle, new Vector3(ev.At.X, -ev.At.Y, 0), size, col, 8);
            _shots.Add((sr, Time.time + 0.12f));
        }

        private void SyncShots()
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                if (Time.time < _shots[i].until && _shots[i].sr != null) continue;
                if (_shots[i].sr != null) Destroy(_shots[i].sr.gameObject);
                _shots.RemoveAt(i);
            }
        }

        private void SyncHover()
        {
            if (_hoverView == null) return;
            if (_hover == null) { _hoverView.color = Color.clear; return; }
            var p = _hover.Value;
            _hoverView.transform.position = World(p, 0);
            Color c;
            if (_tool == BuildTool.Sell) c = _sim.SellValue(p) > 0 ? new Color(1f, 0.9f, 0.2f, 0.45f) : new Color(1f, 1f, 1f, 0.1f);
            else
            {
                bool ok = _sim.Layout.IsBuildable(p) && _sim.OccupantAt(p) == Occupant.None;
                var baseCol = _tool == BuildTool.Wall ? WallCol : TowerColor(TowerIdFor(_tool));
                c = ok ? new Color(baseCol.r, baseCol.g, baseCol.b, 0.5f) : new Color(1f, 0.2f, 0.2f, 0.35f);
            }
            _hoverView.color = c;
        }

        // ------------------------------------------------------------------ helpers

        private SpriteRenderer MakeBar(Transform parent, Color c, int order)
        {
            var sr = MakeSprite(parent, _square, Vector3.zero, 1f, c, order);
            sr.transform.localPosition = new Vector3(0, BarY, 0);
            return sr;
        }

        /// <summary>Place a bar segment: starts at fraction `from` of the bar, `width` fraction long.</summary>
        private static void SetBar(SpriteRenderer bar, float from, float width)
        {
            bar.enabled = width > 0.001f;
            bar.transform.localScale = new Vector3(BarWidth * width, BarHeight, 1f);
            bar.transform.localPosition = new Vector3(BarWidth * (from + width * 0.5f - 0.5f), BarY, 0);
        }

        /// <summary>The route a ground enemy would walk, drawn as one line from spawn to exit.</summary>
        private void MakePathLine(List<Vec2> points)
        {
            if (points.Count < 2) return;
            var go = new GameObject("Path");
            go.transform.SetParent(_staticRoot, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) lr.SetPosition(i, new Vector3(points[i].X, -points[i].Y, 0));
            lr.widthMultiplier = 0.08f;
            lr.numCornerVertices = 4;
            lr.numCapVertices = 4;
            lr.sharedMaterial = _lineMat;
            lr.startColor = PathCol;
            lr.endColor = new Color(ExitCol.r, ExitCol.g, ExitCol.b, 0.6f);
            lr.sortingOrder = 1;
        }

        private static Vector3 World(GridPos p, float z) => new Vector3(p.X, -p.Y, z);

        private SpriteRenderer MakeSprite(Transform parent, Sprite sprite, Vector3 pos, float size, Color color, int order)
        {
            var go = new GameObject("s");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = new Vector3(size, size, 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (_unlit != null) sr.sharedMaterial = _unlit;
            return sr;
        }

        private void EnsureAssets()
        {
            if (_square != null) return;
            _square = MakeTextureSprite(4, (x, y) => true);
            _circle = MakeTextureSprite(32, (x, y) =>
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                return dx * dx + dy * dy <= 15.5f * 15.5f;
            });
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader != null) _unlit = new Material(shader);
            _lineMat = _unlit != null ? _unlit : new Material(Shader.Find("Sprites/Default"));
        }

        private static Sprite MakeTextureSprite(int size, System.Func<int, int, bool> inside)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    px[y * size + x] = inside(x, y) ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        private void FitCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.orthographic = true;
            cam.backgroundColor = new Color(0.04f, 0.04f, 0.05f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            _lastScreenW = Screen.width; _lastScreenH = Screen.height;
            var l = _sim.Layout;
            // Leave the left part of the screen for the HUD panel.
            float hudFraction = HudWidth / Mathf.Max(1f, Screen.width);
            float aspect = cam.aspect * (1f - hudFraction);
            float sizeForHeight = l.Height * 0.5f + 0.6f;
            float sizeForWidth = (l.Width * 0.5f + 0.6f) / Mathf.Max(0.1f, aspect);
            cam.orthographicSize = Mathf.Max(sizeForHeight, sizeForWidth);
            float worldWidth = cam.orthographicSize * 2f * cam.aspect;
            float shift = worldWidth * hudFraction * 0.5f;
            cam.transform.position = new Vector3((l.Width - 1) * 0.5f - shift, -(l.Height - 1) * 0.5f, -10f);
        }
    }
}

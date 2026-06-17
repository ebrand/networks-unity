// A full-screen, top-down MAP TRIMMER for the DEM chunk world. Visualises every chunk in the mosaic
// as a grid coloured by terrain relief (how much height variation it has), so empty/ocean/no-data
// chunks (flat) stand out. You can auto-flag flat chunks via a relief threshold and/or paint chunks
// by hand, then Apply — which writes ChunkWorld's exclusion set (those chunks stop streaming and are
// saved per-game). Reversible: clear and re-apply any time.
//
// IMGUI (its own OnGUI) so the grid is trivially clickable; gated open via the System palette. While
// open, TerrainDesigner suppresses sculpt/camera input (see ChunkMapEditor.IsOpen).

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class ChunkMapEditor : MonoBehaviour
    {
        static ChunkMapEditor _inst;
        public static bool IsOpen => _inst != null && _inst._open;

        bool _open;
        int _cx0, _cz0, _nx, _nz;          // mosaic chunk origin + grid dims
        float[] _relief;                   // per-cell max-min height; NaN until scanned
        readonly HashSet<Vector2Int> _excl = new HashSet<Vector2Int>();   // working selection
        float _threshold = 8f;             // relief (m) at/below which a chunk reads as "flat/empty"
        bool _scanning;
        int _scanIndex;
        bool _painting, _paintErase;

        public static void Toggle()
        {
            if (_inst == null)
            {
                var go = new GameObject("ChunkMapEditor");
                _inst = go.AddComponent<ChunkMapEditor>();
            }
            _inst.SetOpen(!_inst._open);
        }

        void SetOpen(bool on)
        {
            _open = on && DemChunkSource.Active;
            if (_open) Init();
        }

        void Init()
        {
            float cs = ChunkWorld.ChunkSize;
            _cx0 = Mathf.FloorToInt(DemChunkSource.OriginX / cs);
            _cz0 = Mathf.FloorToInt(DemChunkSource.OriginZ / cs);
            int cx1 = Mathf.FloorToInt((DemChunkSource.OriginX + DemChunkSource.WorldWidthX - 1f) / cs);
            int cz1 = Mathf.FloorToInt((DemChunkSource.OriginZ + DemChunkSource.WorldLengthZ - 1f) / cs);
            _nx = Mathf.Max(1, cx1 - _cx0 + 1);
            _nz = Mathf.Max(1, cz1 - _cz0 + 1);
            _relief = new float[_nx * _nz];
            for (int i = 0; i < _relief.Length; i++) _relief[i] = float.NaN;
            _excl.Clear();
            for (int z = 0; z < _nz; z++)
                for (int x = 0; x < _nx; x++)
                {
                    var c = new Vector2Int(_cx0 + x, _cz0 + z);
                    if (ChunkWorld.IsExcluded(c)) _excl.Add(c);
                }
            _scanIndex = 0; _scanning = true;
        }

        Vector2Int CellCoord(int idx) => new Vector2Int(_cx0 + idx % _nx, _cz0 + idx / _nx);

        void Update()
        {
            if (_open && !DemChunkSource.Active) { _open = false; return; }
            if (!_open || !_scanning || _relief == null) return;
            // Scan a batch of chunks per frame (decode-paced via DemChunkSource's tile cache): sample a
            // 3×3 grid per chunk and record relief = max-min. Sequential order keeps tile locality.
            float cs = ChunkWorld.ChunkSize;
            int budget = 24;
            while (budget-- > 0 && _scanIndex < _relief.Length)
            {
                var c = CellCoord(_scanIndex);
                float ox = c.x * cs, oz = c.y * cs;
                float mn = float.MaxValue, mx = float.MinValue;
                for (int sz = 0; sz <= 2; sz++)
                    for (int sx = 0; sx <= 2; sx++)
                    {
                        float y = DemChunkSource.SampleWorldYAt(ox + sx * 0.5f * cs, oz + sz * 0.5f * cs);
                        if (y < mn) mn = y; if (y > mx) mx = y;
                    }
                _relief[_scanIndex] = mx - mn;
                _scanIndex++;
            }
            if (_scanIndex >= _relief.Length) _scanning = false;
        }

        Color CellColor(int idx, Vector2Int c)
        {
            if (_excl.Contains(c)) return new Color(0.85f, 0.22f, 0.22f, 1f);     // trimmed = red
            float r = _relief[idx];
            if (float.IsNaN(r)) return new Color(0.28f, 0.28f, 0.30f, 1f);        // not scanned yet = grey
            if (r <= _threshold) return new Color(0.88f, 0.62f, 0.15f, 1f);       // flat candidate = orange
            float t = Mathf.Clamp01(r / 300f);
            return Color.Lerp(new Color(0.22f, 0.42f, 0.20f, 1f), new Color(0.5f, 0.9f, 0.45f, 1f), t);
        }

        void OnGUI()
        {
            if (!_open) return;
            float sw = Screen.width, sh = Screen.height;
            Color prev = GUI.color;

            // Backdrop (also eats stray clicks so they don't fall through to the scene).
            GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.92f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(20, 14, 900, 24), "Map Trimmer — orange = flat; red = trimmed.  \"Flag border flats\" trims the flat apron from the edges inward (interior flats stay).  Click/drag to fix by hand.");

            // Layout: grid area on top, controls strip at the bottom.
            float pad = 20f, ctrlH = 64f;
            float availW = sw - pad * 2f, availH = sh - 44f - ctrlH - pad;
            float cell = Mathf.Max(2f, Mathf.Floor(Mathf.Min(availW / _nx, availH / _nz)));
            float gridW = cell * _nx, gridH = cell * _nz;
            float gx = (sw - gridW) * 0.5f, gy = 44f + (availH - gridH) * 0.5f;
            var grid = new Rect(gx, gy, gridW, gridH);

            HandlePaint(grid, cell);

            // Cells.
            for (int z = 0; z < _nz; z++)
                for (int x = 0; x < _nx; x++)
                {
                    int idx = z * _nx + x;
                    var c = new Vector2Int(_cx0 + x, _cz0 + z);
                    // +Z (north) up: row 0 at the bottom.
                    float cxp = gx + x * cell, cyp = gy + (_nz - 1 - z) * cell;
                    GUI.color = CellColor(idx, c);
                    GUI.DrawTexture(new Rect(cxp, cyp, cell - (cell > 5f ? 1f : 0f), cell - (cell > 5f ? 1f : 0f)), Texture2D.whiteTexture);
                    if (cell > 6f && ChunkWorld.IsLoaded(c))   // resident now → thin bright inset
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.5f);
                        GUI.DrawTexture(new Rect(cxp + 1f, cyp + 1f, cell - 3f, 1f), Texture2D.whiteTexture);
                    }
                }

            // Stats + controls.
            int flat = 0, scanned = 0;
            for (int i = 0; i < _relief.Length; i++) { if (!float.IsNaN(_relief[i])) { scanned++; if (_relief[i] <= _threshold) flat++; } }
            float cy = sh - ctrlH - 6f;
            GUI.color = Color.white;
            string scanMsg = _scanning ? $"scanning {scanned}/{_relief.Length}…" : "scan complete";
            GUI.Label(new Rect(pad, cy, sw, 20f),
                $"{_nx}×{_nz} = {_relief.Length} chunks   |   trimmed {_excl.Count}   |   flat ≤ {_threshold:0} m: {flat}   |   {scanMsg}");

            float rowY = cy + 24f;
            GUI.Label(new Rect(pad, rowY + 2f, 150f, 20f), $"Flat threshold: {_threshold:0} m");
            _threshold = Mathf.Round(GUI.HorizontalSlider(new Rect(pad + 150f, rowY + 8f, 220f, 16f), _threshold, 0f, 100f));

            float bx = pad + 390f, bw = 160f, bh = 28f;
            if (GUI.Button(new Rect(bx, rowY, bw, bh), "Flag border flats"))
                FlagBorderFlats();
            if (GUI.Button(new Rect(bx + bw + 8f, rowY, 110f, bh), "Clear"))
                _excl.Clear();
            if (GUI.Button(new Rect(bx + bw + 126f, rowY, 160f, bh), "Apply & Save"))
                ChunkWorld.ApplyExclusions(_excl);
            if (GUI.Button(new Rect(sw - pad - 110f, rowY, 110f, bh), "Close"))
                SetOpen(false);

            GUI.color = prev;
        }

        // Click/drag paints exclusions. The first cell decides add-vs-erase for the whole drag.
        void HandlePaint(Rect grid, float cell)
        {
            Event e = Event.current;
            if (e == null) return;
            if (e.type == EventType.MouseDown && e.button == 0 && grid.Contains(e.mousePosition))
            {
                Vector2Int c = CellAt(grid, cell, e.mousePosition);
                _painting = true; _paintErase = _excl.Contains(c);
                Paint(c); e.Use();
            }
            else if (e.type == EventType.MouseDrag && _painting)
            {
                if (grid.Contains(e.mousePosition)) Paint(CellAt(grid, cell, e.mousePosition));
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _painting)
            {
                _painting = false; e.Use();
            }
        }

        Vector2Int CellAt(Rect grid, float cell, Vector2 m)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((m.x - grid.x) / cell), 0, _nx - 1);
            int zrow = Mathf.Clamp(Mathf.FloorToInt((m.y - grid.y) / cell), 0, _nz - 1);
            int z = _nz - 1 - zrow;   // screen row → chunk Z (north up)
            return new Vector2Int(_cx0 + x, _cz0 + z);
        }

        void Paint(Vector2Int c)
        {
            if (_paintErase) _excl.Remove(c); else _excl.Add(c);
        }

        // Flood from the map borders inward through FLAT chunks (relief ≤ threshold), stopping at real
        // terrain — so only the no-data/ocean apron attached to the edge gets trimmed. Interior flats
        // (valleys, plateaus surrounded by terrain) are never reached, so no holes punched mid-map.
        void FlagBorderFlats()
        {
            if (_relief == null) return;
            var seen = new bool[_relief.Length];
            var q = new Queue<int>();
            for (int x = 0; x < _nx; x++) { Seed(x); Seed(x + (_nz - 1) * _nx); }   // top + bottom edges
            for (int z = 0; z < _nz; z++) { Seed(z * _nx); Seed(_nx - 1 + z * _nx); } // left + right edges
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                _excl.Add(CellCoord(i));
                int x = i % _nx, z = i / _nx;
                if (x > 0) Seed(i - 1);
                if (x < _nx - 1) Seed(i + 1);
                if (z > 0) Seed(i - _nx);
                if (z < _nz - 1) Seed(i + _nx);
            }

            void Seed(int i)
            {
                if (i < 0 || i >= _relief.Length || seen[i]) return;
                if (float.IsNaN(_relief[i]) || _relief[i] > _threshold) return;   // unscanned or real terrain → wall
                seen[i] = true; q.Enqueue(i);
            }
        }
    }
}

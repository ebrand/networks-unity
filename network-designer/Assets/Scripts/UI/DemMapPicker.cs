// Slippy tile-map area picker for the DEM downloader (Leaflet/Google-style).
//
// The map is a layer of INDIVIDUAL tile elements, each positioned at its world-pixel location for the
// current zoom. As you drag/zoom, the visible tile set is recomputed every frame: tiles already loaded
// just reposition, newly-revealed tiles are created and their image streams in (disk cache → network,
// in parallel) and pops in when ready. There is no fixed buffer and no atomic re-stitch — so panning is
// unbounded and never "walls", and zoom is incremental (cached overview zooms appear instantly).
//
// A centre pin marks the download centre; a square AREA box with corner handles sets the size (1 km
// steps). Basemap is USGS The National Map topo (public domain) via the shared TileCache. North-up.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace NetworkDesigner.UI
{
    public class DemMapPicker
    {
        public VisualElement Root { get; }
        public double CenterLat { get; private set; }
        public double CenterLon { get; private set; }
        public double AreaKm { get; private set; }
        public Action OnChanged;

        const int TileSize = 256;
        const int Concurrency = 8;         // tiles fetched in parallel
        const int TileMargin = 2;          // extra tiles loaded beyond the viewport (lead for panning)
        const int TexCacheCap = 600;       // decoded tile textures kept (~150 MB) — instant revisits
        const float HandleSize = 16f;
        const float MaxAreaKm = 16f;
        const double EarthKm = 40075.017;

        readonly MonoBehaviour _host;
        readonly VisualElement _viewport, _layer, _box, _pin, _label;
        readonly VisualElement[] _handles = new VisualElement[4];
        int MapW, MapH;
        int _ovZoom = 13;

        readonly Dictionary<long, VisualElement> _tiles = new Dictionary<long, VisualElement>();  // live tile elements
        readonly Dictionary<long, Texture2D> _texCache = new Dictionary<long, Texture2D>();
        readonly LinkedList<long> _texLru = new LinkedList<long>();
        readonly Queue<long> _pending = new Queue<long>();
        readonly HashSet<long> _queued = new HashSet<long>();
        readonly HashSet<long> _want = new HashSet<long>();
        readonly List<long> _toRemove = new List<long>();
        int _loading;
        bool _dragging, _resizing; Vector2 _dragLast; float _dragTotal;

        public DemMapPicker(MonoBehaviour host, double lat, double lon, int width, int height, double areaKm = 3.0)
        {
            _host = host; CenterLat = lat; CenterLon = lon; AreaKm = Mathf.Clamp((float)areaKm, 1f, MaxAreaKm);
            MapW = Mathf.Max(64, width); MapH = Mathf.Max(64, height);
            Root = new VisualElement();

            _viewport = new VisualElement();
            _viewport.style.width = MapW; _viewport.style.height = MapH;
            _viewport.style.overflow = Overflow.Hidden;
            _viewport.style.backgroundColor = new Color(0.10f, 0.12f, 0.15f);
            _viewport.RegisterCallback<PointerDownEvent>(OnDown);
            _viewport.RegisterCallback<PointerMoveEvent>(OnMove);
            _viewport.RegisterCallback<PointerUpEvent>(OnUp);
            _viewport.RegisterCallback<WheelEvent>(OnWheel);
            Root.Add(_viewport);

            _layer = new VisualElement { pickingMode = PickingMode.Ignore };
            _layer.style.position = Position.Absolute;
            _layer.style.left = 0; _layer.style.top = 0; _layer.style.right = 0; _layer.style.bottom = 0;
            _viewport.Add(_layer);

            var blue = new Color(0.16f, 0.32f, 0.95f);
            _box = new VisualElement { pickingMode = PickingMode.Ignore };
            _box.style.position = Position.Absolute;
            _box.style.borderTopWidth = _box.style.borderBottomWidth = _box.style.borderLeftWidth = _box.style.borderRightWidth = 3;
            _box.style.borderTopColor = _box.style.borderBottomColor = _box.style.borderLeftColor = _box.style.borderRightColor = blue;
            _viewport.Add(_box);

            _pin = new VisualElement { pickingMode = PickingMode.Ignore };
            _pin.style.position = Position.Absolute;
            _pin.style.width = 12; _pin.style.height = 12;
            _pin.style.backgroundColor = new Color(0.20f, 0.28f, 0.45f);
            _pin.style.borderTopWidth = _pin.style.borderBottomWidth = _pin.style.borderLeftWidth = _pin.style.borderRightWidth = 2;
            _pin.style.borderTopColor = _pin.style.borderBottomColor = _pin.style.borderLeftColor = _pin.style.borderRightColor = Color.white;
            Radius(_pin, 6);
            _pin.style.left = MapW * 0.5f - 6f; _pin.style.top = MapH * 0.5f - 6f;
            _viewport.Add(_pin);

            for (int i = 0; i < 4; i++)
            {
                var h = new VisualElement();
                h.style.position = Position.Absolute;
                h.style.width = HandleSize; h.style.height = HandleSize;
                h.style.backgroundColor = new Color(0.30f, 0.42f, 0.95f, 0.65f);
                h.style.borderTopWidth = h.style.borderBottomWidth = h.style.borderLeftWidth = h.style.borderRightWidth = 2;
                h.style.borderTopColor = h.style.borderBottomColor = h.style.borderLeftColor = h.style.borderRightColor = blue;
                h.RegisterCallback<PointerDownEvent>(OnHandleDown);
                _handles[i] = h; _viewport.Add(h);
            }

            _label = new Label() { pickingMode = PickingMode.Ignore };
            ((Label)_label).style.color = blue;
            _label.style.position = Position.Absolute;
            _label.style.unityFontStyleAndWeight = FontStyle.Bold;
            _viewport.Add(_label);

            var attr = new Label("USGS The National Map") { pickingMode = PickingMode.Ignore };
            attr.style.position = Position.Absolute; attr.style.bottom = 2; attr.style.left = 4;
            attr.style.fontSize = 9; attr.style.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            _viewport.Add(attr);

            var zr = new VisualElement(); zr.style.flexDirection = FlexDirection.Row; zr.style.marginTop = 4; zr.style.marginBottom = 4;
            zr.Add(ZoomBtn("–", () => ZoomAt(MapW * 0.5f, MapH * 0.5f, -1)));
            zr.Add(ZoomBtn("+", () => ZoomAt(MapW * 0.5f, MapH * 0.5f, +1)));
            var hint = new Label("drag to pan · drag a corner to resize (1 km) · wheel/± to zoom");
            hint.style.color = new Color(0.6f, 0.64f, 0.7f); hint.style.fontSize = 10; hint.style.marginLeft = 8; hint.style.alignSelf = Align.Center;
            zr.Add(hint);
            Root.Add(zr);

            UpdateTiles();
            UpdateBox();
        }

        public void SetCenter(double lat, double lon) { CenterLat = lat; CenterLon = lon; UpdateTiles(); }

        public void SetAreaKm(double km)
        { AreaKm = Mathf.Clamp(Mathf.Round((float)km), 1f, MaxAreaKm); UpdateBox(); OnChanged?.Invoke(); }

        // ── view math (world pixels at the current zoom) ──
        double CenterWX() => Lon2TileX(CenterLon, _ovZoom) * TileSize;
        double CenterWY() => Lat2TileY(CenterLat, _ovZoom) * TileSize;
        float KmPerTile() => (float)(EarthKm * Math.Cos(CenterLat * Math.PI / 180.0) / (1 << _ovZoom));

        void OnDown(PointerDownEvent ev)
        { _dragging = true; _resizing = false; _dragLast = (Vector2)ev.localPosition; _dragTotal = 0f; _viewport.CapturePointer(ev.pointerId); }

        void OnHandleDown(PointerDownEvent ev)
        { _resizing = true; _dragging = false; _viewport.CapturePointer(ev.pointerId); ev.StopPropagation(); }

        void OnMove(PointerMoveEvent ev)
        {
            if (_resizing) { ResizeTo((Vector2)ev.localPosition); return; }
            if (!_dragging) return;
            Vector2 p = (Vector2)ev.localPosition;
            float dx = p.x - _dragLast.x, dy = p.y - _dragLast.y; _dragLast = p; _dragTotal += Mathf.Abs(dx) + Mathf.Abs(dy);
            PanByPixels(-dx, -dy);   // drag right reveals content to the west
            UpdateTiles();
        }

        void OnUp(PointerUpEvent ev)
        {
            if (_resizing) { _resizing = false; _viewport.ReleasePointer(ev.pointerId); OnChanged?.Invoke(); return; }
            if (!_dragging) return;
            _dragging = false; _viewport.ReleasePointer(ev.pointerId);
            if (_dragTotal < 4f)   // click → centre on the clicked point
            {
                double cwx = CenterWX() - MapW * 0.5 + ev.localPosition.x, cwy = CenterWY() - MapH * 0.5 + ev.localPosition.y;
                CenterLon = Tile2Lon(cwx / TileSize, _ovZoom); CenterLat = Tile2Lat(cwy / TileSize, _ovZoom);
                UpdateTiles();
            }
            OnChanged?.Invoke();
        }

        void OnWheel(WheelEvent ev) { ZoomAt(ev.localMousePosition.x, ev.localMousePosition.y, ev.delta.y < 0f ? +1 : -1); ev.StopPropagation(); }

        // Zoom keeping the geo point under (px,py) fixed (zoom toward cursor).
        void ZoomAt(float px, float py, int dir)
        {
            int nz = Mathf.Clamp(_ovZoom + dir, 3, 16);
            if (nz == _ovZoom) return;
            double cwx = CenterWX() - MapW * 0.5 + px, cwy = CenterWY() - MapH * 0.5 + py;
            double lon = Tile2Lon(cwx / TileSize, _ovZoom), lat = Tile2Lat(cwy / TileSize, _ovZoom);
            _ovZoom = nz;
            double anchorWX = Lon2TileX(lon, nz) * TileSize, anchorWY = Lat2TileY(lat, nz) * TileSize;
            double newCWX = anchorWX - px + MapW * 0.5, newCWY = anchorWY - py + MapH * 0.5;
            CenterLon = Tile2Lon(newCWX / TileSize, nz); CenterLat = Tile2Lat(newCWY / TileSize, nz);
            ClearTiles();   // tile coords are zoom-specific; textures stay cached for zoom-back
            UpdateTiles(); UpdateBox();
            OnChanged?.Invoke();
        }

        void ResizeTo(Vector2 p)
        {
            float half = Mathf.Max(Mathf.Abs(p.x - MapW * 0.5f), Mathf.Abs(p.y - MapH * 0.5f));
            float pxPerKm = TileSize / Mathf.Max(0.0001f, KmPerTile());
            AreaKm = Mathf.Clamp(Mathf.Round(2f * half / pxPerKm), 1f, MaxAreaKm);
            UpdateBox();
        }

        void PanByPixels(double dpx, double dpy)
        {
            double cwx = CenterWX() + dpx, cwy = CenterWY() + dpy;
            CenterLon = Tile2Lon(cwx / TileSize, _ovZoom); CenterLat = Tile2Lat(cwy / TileSize, _ovZoom);
        }

        // Recompute the visible tile set: position/keep loaded tiles, create newly-visible ones (stream
        // their image), drop tiles that scrolled out. Cheap enough to run every drag frame.
        void UpdateTiles()
        {
            if (MapW <= 0) return;
            double tlx = CenterWX() - MapW * 0.5, tly = CenterWY() - MapH * 0.5;
            int worldTiles = 1 << _ovZoom;
            int tx0 = (int)Math.Floor(tlx / TileSize) - TileMargin, tx1 = (int)Math.Floor((tlx + MapW) / TileSize) + TileMargin;
            int ty0 = (int)Math.Floor(tly / TileSize) - TileMargin, ty1 = (int)Math.Floor((tly + MapH) / TileSize) + TileMargin;
            _want.Clear();
            for (int ty = ty0; ty <= ty1; ty++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (tx < 0 || ty < 0 || tx >= worldTiles || ty >= worldTiles) continue;
                    long k = Key(_ovZoom, tx, ty);
                    _want.Add(k);
                    if (!_tiles.TryGetValue(k, out var el))
                    {
                        el = new VisualElement { pickingMode = PickingMode.Ignore };
                        el.style.position = Position.Absolute;
                        el.style.width = TileSize; el.style.height = TileSize;
                        el.style.unityBackgroundScaleMode = ScaleMode.StretchToFill;
                        _layer.Add(el);
                        _tiles[k] = el;
                        if (_texCache.TryGetValue(k, out var tex) && tex != null) { el.style.backgroundImage = new StyleBackground(tex); TexTouch(k); }
                        else Enqueue(k);
                    }
                    el.style.left = (float)(tx * TileSize - tlx);
                    el.style.top = (float)(ty * TileSize - tly);
                }
            _toRemove.Clear();
            foreach (var kv in _tiles) if (!_want.Contains(kv.Key)) _toRemove.Add(kv.Key);
            for (int i = 0; i < _toRemove.Count; i++) { _tiles[_toRemove[i]].RemoveFromHierarchy(); _tiles.Remove(_toRemove[i]); }
            PumpLoads();
        }

        void ClearTiles()
        {
            foreach (var kv in _tiles) kv.Value.RemoveFromHierarchy();
            _tiles.Clear(); _pending.Clear(); _queued.Clear();
        }

        void Enqueue(long k) { if (_queued.Add(k)) _pending.Enqueue(k); }

        void PumpLoads()
        {
            while (_loading < Concurrency && _pending.Count > 0)
            {
                long k = _pending.Dequeue();
                if (!_tiles.ContainsKey(k)) { _queued.Remove(k); continue; }            // scrolled out before loading
                if (_texCache.ContainsKey(k)) { _queued.Remove(k); ApplyTex(k); continue; }
                _loading++;
                _host.StartCoroutine(LoadTile(k));
            }
        }

        IEnumerator LoadTile(long k)
        {
            yield return null;   // spread work across frames (don't decode a whole screen in one frame)
            DecodeKey(k, out int z, out int tx, out int ty);
            Texture2D tex = null;
            byte[] disk = TileCache.TryLoad(TileCache.Layer, z, tx, ty);
            if (disk != null) tex = Decode(disk);
            if (tex == null)
            {
                using var req = UnityWebRequest.Get(string.Format(TileCache.UsgsTopoUrl, z, ty, tx));
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    byte[] raw = req.downloadHandler.data;
                    TileCache.Save(TileCache.Layer, z, tx, ty, raw);
                    tex = Decode(raw);
                }
            }
            _loading--;
            _queued.Remove(k);
            if (tex != null) { TexPut(k, tex); ApplyTex(k); }
            PumpLoads();
        }

        static Texture2D Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (t.LoadImage(bytes)) return t;
            UnityEngine.Object.Destroy(t);
            return null;
        }

        void ApplyTex(long k)
        {
            if (_tiles.TryGetValue(k, out var el) && _texCache.TryGetValue(k, out var tex) && tex != null)
            { el.style.backgroundImage = new StyleBackground(tex); TexTouch(k); }
        }

        // FIFO-ish texture cache: evict the oldest tile NOT currently on screen.
        void TexPut(long k, Texture2D tex)
        {
            if (!_texCache.ContainsKey(k)) _texLru.AddLast(k);
            _texCache[k] = tex;
            int guard = _texLru.Count;
            while (_texCache.Count > TexCacheCap && _texLru.Count > 0 && guard-- > 0)
            {
                long old = _texLru.First.Value; _texLru.RemoveFirst();
                if (_tiles.ContainsKey(old) || old == k) { _texLru.AddLast(old); continue; }   // visible → keep
                if (_texCache.TryGetValue(old, out var t)) { if (t != null) UnityEngine.Object.Destroy(t); _texCache.Remove(old); }
            }
        }

        void TexTouch(long k) { if (_texLru.Remove(k)) _texLru.AddLast(k); }

        void UpdateBox()
        {
            float pxPerKm = TileSize / Mathf.Max(0.0001f, KmPerTile());
            float boxPx = Mathf.Max(8f, (float)AreaKm * pxPerKm);
            float half = boxPx * 0.5f, cx = MapW * 0.5f, cy = MapH * 0.5f;
            float l = cx - half, t = cy - half;
            _box.style.left = l; _box.style.top = t; _box.style.width = boxPx; _box.style.height = boxPx;
            PlaceHandle(0, l, t); PlaceHandle(1, l + boxPx, t); PlaceHandle(2, l, t + boxPx); PlaceHandle(3, l + boxPx, t + boxPx);
            ((Label)_label).text = $"{AreaKm:0} km × {AreaKm:0} km";
            _label.style.left = cx - 44f; _label.style.top = Mathf.Min(MapH - 18f, t + boxPx + 2f);
        }

        void PlaceHandle(int i, float x, float y)
        { _handles[i].style.left = x - HandleSize * 0.5f; _handles[i].style.top = y - HandleSize * 0.5f; }

        static long Key(int z, int tx, int ty) => ((long)(z & 0x3F) << 48) | ((long)(tx & 0xFFFFFF) << 24) | (uint)(ty & 0xFFFFFF);
        static void DecodeKey(long k, out int z, out int tx, out int ty)
        { z = (int)((k >> 48) & 0x3F); tx = (int)((k >> 24) & 0xFFFFFF); ty = (int)(k & 0xFFFFFF); }

        static Button ZoomBtn(string text, Action a)
        { var b = new Button(a) { text = text }; b.style.width = 30; b.style.height = 22; return b; }

        static void Radius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        static double Lon2TileX(double lon, int z) => (lon + 180.0) / 360.0 * (1 << z);
        static double Lat2TileY(double lat, int z)
        { double r = lat * Math.PI / 180.0; return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * (1 << z); }
        static double Tile2Lon(double x, int z) => x / (1 << z) * 360.0 - 180.0;
        static double Tile2Lat(double y, int z)
        { double a = Math.PI - 2.0 * Math.PI * y / (1 << z); return 180.0 / Math.PI * Math.Atan(Math.Sinh(a)); }
    }
}

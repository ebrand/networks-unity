// Off-screen 3D preview rig for the Road Designer: sweeps a short straight road from a RoadProfile,
// parks it far from the world, and renders it with an orbit camera into a RenderTexture the modal shows.
// Placing the stub 100 km away means the preview camera sees only it (no dedicated render layer needed)
// and the main camera never frames it; the scene's directional sun lights it.

using UnityEngine;
using NetworkDesigner.Model;
using NetworkDesigner.Roads;

namespace NetworkDesigner.UI
{
    public class RoadPreview3D : MonoBehaviour
    {
        static readonly Vector3 Far = new Vector3(100000f, 0f, 100000f);

        Camera _cam;
        RenderTexture _rt;
        GameObject _road, _lines, _guard, _feat;
        float _yaw = 35f, _pitch = 28f, _dist = 45f;

        public RenderTexture Texture { get { EnsureRig(); return _rt; } }

        void EnsureRig()
        {
            if (_cam != null) return;
            _rt = new RenderTexture(1024, 720, 24) { name = "RoadPreviewRT" };
            var camGo = new GameObject("RoadPreviewCam") { hideFlags = HideFlags.DontSave };
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.08f, 0.09f, 0.10f);
            _cam.fieldOfView = 35f;
            _cam.nearClipPlane = 0.3f;
            _cam.farClipPlane = 500f;
            _cam.enabled = false;
        }

        public void SetProfile(RoadProfile p)
        {
            EnsureRig();
            if (_road != null) DestroySafe(_road);
            if (p == null) return;
            RoadCrossSection xs = BuildXS(p);
            Vector2 a = new Vector2(Far.x - 20f, Far.z), b = new Vector2(Far.x + 20f, Far.z);
            _road = RoadSweep.Build(xs, a, b, false, default, default, transform, "RoadPreviewMesh", Far.y, Far.y);
            _road.hideFlags = HideFlags.DontSave;
            BuildLaneLines(p);
            BuildGuardrails(p);
            _dist = Mathf.Clamp(p.TotalWidth * 1.8f + 18f, 22f, 140f);
        }

        // Render a pre-built cross-section (the corridor-stack path). No painted lane lines/guardrails in Phase 1 —
        // the swept bands (incl. fence/parapet/guardrail walls) carry the look.
        public void SetCrossSection(RoadCrossSection xs, float totalWidth,
                                    System.Collections.Generic.List<(float u, bool yellow, bool dashed)> marks,
                                    System.Collections.Generic.List<RoadCrossSectionBuilder.StackBand> bands)
        {
            EnsureRig();
            if (_road != null) DestroySafe(_road);
            if (_lines != null) { DestroySafe(_lines); _lines = null; }
            if (_guard != null) { DestroySafe(_guard); _guard = null; }
            if (_feat != null) { DestroySafe(_feat); _feat = null; }
            if (xs == null) return;
            Vector2 a = new Vector2(Far.x - 20f, Far.z), b = new Vector2(Far.x + 20f, Far.z);
            _road = RoadSweep.Build(xs, a, b, false, default, default, transform, "RoadPreviewMesh", Far.y, Far.y);
            _road.hideFlags = HideFlags.DontSave;
            BuildPreviewLines(xs, marks);

            // Free-standing features (parapets) extruded along the straight stub at each parapet band's centre.
            if (bands != null)
            {
                float half = totalWidth * 0.5f;
                _feat = new GameObject("RoadPreviewFeatures") { hideFlags = HideFlags.DontSave };
                _feat.transform.SetParent(transform, false);
                for (int i = 0; i < bands.Count; i++)
                {
                    var bd = bands[i];
                    float latOff = (bd.U0 + bd.U1) * 0.5f - half;
                    if (bd.Parapet)
                        RoadFeatureSweep.BuildParapet(a, default, default, b, false, latOff,
                            Mathf.Max(0.1f, bd.ParapetH), Far.y, Far.y, null, 0f, _feat.transform, "parapet" + i);
                    if (bd.Fence)
                        RoadFeatureSweep.BuildFence(a, default, default, b, false, latOff,
                            Far.y, Far.y, null, 0f, _feat.transform, "fence" + i);
                    if (bd.Type == CorridorType.Rail)
                        RoadFeatureSweep.BuildRail(a, default, default, b, false, latOff,
                            Far.y, Far.y, null, 0f, _feat.transform, "rail" + i);
                }
            }
            _dist = Mathf.Clamp(totalWidth * 1.8f + 18f, 22f, 140f);
        }

        static readonly Color32 Cyan = new Color32(80, 220, 255, 255);

        // Lane markings (white/yellow, solid/dashed) from the stack + a bright DASHED cyan line down the A→B / B→A
        // boundary (the directional midline). The sweep centres the section geometrically, so the split is offset.
        void BuildPreviewLines(RoadCrossSection xs, System.Collections.Generic.List<(float u, bool yellow, bool dashed)> marks)
        {
            if (_lines != null) { DestroySafe(_lines); _lines = null; }
            if (xs == null) return;
            _lv.Clear(); _lc.Clear(); _lt.Clear();
            if (marks != null) foreach (var m in marks) Stripe(m.u, m.yellow ? Yellow : White, m.dashed);
            // Cyan A↔B midline aid — but ONLY where no real lane marking already sits at the split (e.g. a median or
            // centre rail). For opposing lanes the double-yellow centreline IS the split, so the cyan would duplicate it.
            if (xs.SplitU >= 0f && xs.Width >= 0.5f)
            {
                float uOff = xs.SplitU - xs.Width * 0.5f;
                bool covered = false;
                if (marks != null) foreach (var m in marks) if (Mathf.Abs(m.u - uOff) < 0.35f) { covered = true; break; }
                if (!covered)
                {
                    float z = Far.z - uOff, hw = 0.18f, x0 = Far.x - 20f, x1 = Far.x + 20f;
                    const float dash = 2.5f, gap = 2f;
                    for (float xx = x0; xx < x1; xx += dash + gap) Quad(xx, Mathf.Min(xx + dash, x1), z - hw, z + hw, Cyan);
                }
            }
            if (_lv.Count == 0) return;

            var mesh = new Mesh { name = "RoadPreviewLines" };
            mesh.SetVertices(_lv); mesh.SetColors(_lc); mesh.SetTriangles(_lt, 0); mesh.RecalculateBounds();
            _lines = new GameObject("RoadPreviewLines") { hideFlags = HideFlags.DontSave };
            _lines.transform.SetParent(transform, false);
            _lines.AddComponent<MeshFilter>().sharedMesh = mesh;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            Material mat = sh != null ? new Material(sh) { name = "RoadPreviewLines" }
                                      : NetworkDesigner.PipelineMaterials.CreateUnlitColor(Color.white, "RoadPreviewLines");
            _lines.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static readonly Color32 White = new Color32(235, 235, 235, 255);
        static readonly Color32 Yellow = new Color32(245, 205, 45, 255);

        readonly System.Collections.Generic.List<Vector3> _lv = new System.Collections.Generic.List<Vector3>();
        readonly System.Collections.Generic.List<Color32> _lc = new System.Collections.Generic.List<Color32>();
        readonly System.Collections.Generic.List<int> _lt = new System.Collections.Generic.List<int>();

        // Lane markings on the preview road: white solid pavement/edge lines, white DASHED same-direction lane
        // dividers, double-YELLOW opposing centreline, and a YELLOW solid+dashed pair on each turn-lane edge.
        // Double-sided + always-on-top (vertex-colour overlay shader) so they can't be hidden by winding/depth.
        void BuildLaneLines(RoadProfile p)
        {
            if (_lines != null) DestroySafe(_lines);
            var lay = RoadLayout.Of(p);
            int Trn = RoadLayout.TurnLane;
            float totalU = 0f; foreach (var (w, _) in lay) totalU += w;
            if (totalU < 0.5f) return;

            _lv.Clear(); _lc.Clear(); _lt.Clear();
            float cum = 0f;
            for (int i = 0; i <= lay.Count; i++)
            {
                int left = i > 0 ? lay[i - 1].k : -1;
                int right = i < lay.Count ? lay[i].k : -1;
                float uOff = cum - totalU * 0.5f;
                if (RoadLayout.IsLane(left) || RoadLayout.IsLane(right))   // only paint where a lane meets something
                {
                    if (RoadLayout.IsLane(left) && RoadLayout.IsLane(right) && left != right)
                    { Stripe(uOff - 0.16f, Yellow, false); Stripe(uOff + 0.16f, Yellow, false); }   // double-yellow centreline
                    else if (left == Trn || right == Trn)
                    { float toTurn = right == Trn ? +1f : -1f; Stripe(uOff - 0.18f * toTurn, Yellow, false); Stripe(uOff + 0.18f * toTurn, Yellow, true); }
                    else if (RoadLayout.IsLane(left) && RoadLayout.IsLane(right))
                        Stripe(uOff, White, true);    // same-direction lane divider (dashed)
                    else
                        Stripe(uOff, White, false);   // pavement edge / median edge (solid)
                }
                if (i < lay.Count) cum += lay[i].w;
            }

            var m = new Mesh { name = "RoadPreviewLines" };
            m.SetVertices(_lv); m.SetColors(_lc); m.SetTriangles(_lt, 0); m.RecalculateBounds();
            _lines = new GameObject("RoadPreviewLines") { hideFlags = HideFlags.DontSave };
            _lines.transform.SetParent(transform, false);
            _lines.AddComponent<MeshFilter>().sharedMesh = m;
            Shader sh = Shader.Find("NetworkDesigner/VertexColorOverlay");
            Material mat = sh != null ? new Material(sh) { name = "RoadPreviewLines" }
                                      : NetworkDesigner.PipelineMaterials.CreateUnlitColor(Color.white, "RoadPreviewLines");
            _lines.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // A marking stripe at lateral offset uOff (from the road centre), solid or dashed, along the stub.
        void Stripe(float uOff, Color32 col, bool dashed)
        {
            float z = Far.z - uOff, hw = 0.10f, x0 = Far.x - 20f, x1 = Far.x + 20f, y = Far.y + 0.03f;
            if (!dashed) { Quad(x0, x1, z - hw, z + hw, col); return; }
            const float dash = 2.5f, gap = 2.5f;
            for (float xx = x0; xx < x1; xx += dash + gap)
                Quad(xx, Mathf.Min(xx + dash, x1), z - hw, z + hw, col);
        }

        void Quad(float xa, float xb, float za, float zb, Color32 col)
        {
            float y = Far.y + 0.03f; int s = _lv.Count;
            _lv.Add(new Vector3(xa, y, za)); _lv.Add(new Vector3(xb, y, za));
            _lv.Add(new Vector3(xb, y, zb)); _lv.Add(new Vector3(xa, y, zb));
            for (int i = 0; i < 4; i++) _lc.Add(col);
            _lt.Add(s); _lt.Add(s + 1); _lt.Add(s + 2); _lt.Add(s); _lt.Add(s + 2); _lt.Add(s + 3);   // Cull Off → winding irrelevant
        }

        // RoadProfile → RoadCrossSection — shared with the Build-Plan sweep via RoadCrossSectionBuilder so the
        // authored preview and the laid road match exactly.
        static RoadCrossSection BuildXS(RoadProfile p) => NetworkDesigner.Roads.RoadCrossSectionBuilder.FromProfile(p);

        // Guardrails: a wood post (1 m tall × 0.4 m) every 5 m + a light-gray rail (0.5 m tall × 0.10 m) along
        // each outer edge. Only with shoulders (not sidewalks); on elevated roads they replace the parapet.
        static readonly Color Wood = new Color(0.47f, 0.34f, 0.20f);
        static readonly Color RailGray = new Color(0.75f, 0.75f, 0.77f);
        readonly System.Collections.Generic.List<Vector3> _gv = new System.Collections.Generic.List<Vector3>();
        readonly System.Collections.Generic.List<int> _gtPost = new System.Collections.Generic.List<int>();   // submesh 0: wood posts
        readonly System.Collections.Generic.List<int> _gtRail = new System.Collections.Generic.List<int>();   // submesh 1: gray rail

        void BuildGuardrails(RoadProfile p)
        {
            if (_guard != null) DestroySafe(_guard);
            bool shoulders = !(p.Sidewalks && !p.Elevated);
            if (!p.Guardrails || !shoulders) return;
            float totalU = RoadLayout.Width(p);
            if (totalU < 0.5f) return;
            float baseY = Far.y + (p.Curbs ? 0.25f : 0f);
            float half = totalU * 0.5f - RoadLayout.GuardWidth * 0.5f;   // centre of the outer guard strip
            _gv.Clear(); _gtPost.Clear(); _gtRail.Clear();
            GuardrailSide(Far.z - half, baseY);
            GuardrailSide(Far.z + half, baseY);

            var m = new Mesh { name = "RoadPreviewGuardrail" };
            m.SetVertices(_gv); m.subMeshCount = 2;
            m.SetTriangles(_gtPost, 0); m.SetTriangles(_gtRail, 1);
            m.RecalculateNormals(); m.RecalculateBounds();
            _guard = new GameObject("RoadPreviewGuardrail") { hideFlags = HideFlags.DontSave };
            _guard.transform.SetParent(transform, false);
            _guard.AddComponent<MeshFilter>().sharedMesh = m;
            _guard.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                NetworkDesigner.PipelineMaterials.CreateLitMatte(Wood, "GuardrailPost"),
                NetworkDesigner.PipelineMaterials.CreateLitMatte(RailGray, "GuardrailRail"),
            };
        }

        void GuardrailSide(float z, float baseY)
        {
            float x0 = Far.x - 20f, x1 = Far.x + 20f;
            for (float x = x0; x <= x1 + 0.01f; x += 5f)
                GBox(x - 0.2f, x + 0.2f, baseY, baseY + 1f, z - 0.2f, z + 0.2f, _gtPost);   // post 1 m × 0.4 m
            GBox(x0, x1, baseY + 0.45f, baseY + 0.95f, z - 0.05f, z + 0.05f, _gtRail);        // rail 0.5 m × 0.10 m
        }

        void GBox(float xa, float xb, float ya, float yb, float za, float zb, System.Collections.Generic.List<int> tris)
        {
            int s = _gv.Count;
            _gv.Add(new Vector3(xa, ya, za)); _gv.Add(new Vector3(xb, ya, za)); _gv.Add(new Vector3(xb, ya, zb)); _gv.Add(new Vector3(xa, ya, zb));
            _gv.Add(new Vector3(xa, yb, za)); _gv.Add(new Vector3(xb, yb, za)); _gv.Add(new Vector3(xb, yb, zb)); _gv.Add(new Vector3(xa, yb, zb));
            int[] f = { 0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,  2,3,7, 2,7,6,  3,0,4, 3,4,7,  1,2,6, 1,6,5 };
            for (int i = 0; i < f.Length; i++) tris.Add(s + f[i]);
        }

        public void Orbit(float dx, float dy) { _yaw += dx * 0.3f; _pitch = Mathf.Clamp(_pitch + dy * 0.3f, 5f, 85f); }
        public void Zoom(float d) { _dist = Mathf.Clamp(_dist * (1f + d * 0.08f), 8f, 220f); }
        public void SetActive(bool on) { EnsureRig(); _cam.enabled = on; if (_road != null) _road.SetActive(on); if (_lines != null) _lines.SetActive(on); if (_guard != null) _guard.SetActive(on); if (_feat != null) _feat.SetActive(on); }

        void Update()
        {
            if (_cam == null || !_cam.enabled) return;
            Vector3 center = Far + Vector3.up * 1f;
            Vector3 pos = center + Quaternion.Euler(_pitch, _yaw, 0f) * (Vector3.back * _dist);
            _cam.transform.position = pos;
            _cam.transform.rotation = Quaternion.LookRotation(center - pos, Vector3.up);
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        void OnDestroy() { if (_rt != null) _rt.Release(); }
    }
}

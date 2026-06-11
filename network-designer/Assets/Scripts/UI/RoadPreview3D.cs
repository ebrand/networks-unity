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
        GameObject _road, _lines, _guard;
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
            var lay = Layout(p);
            float totalU = 0f; foreach (var (w, _) in lay) totalU += w;
            if (totalU < 0.5f) return;

            _lv.Clear(); _lc.Clear(); _lt.Clear();
            bool IsLn(int kk) => kk == KLnBA || kk == KLnAB;
            float cum = 0f;
            for (int i = 0; i <= lay.Count; i++)
            {
                int left = i > 0 ? lay[i - 1].k : -1;
                int right = i < lay.Count ? lay[i].k : -1;
                float uOff = cum - totalU * 0.5f;
                if (IsLn(left) || IsLn(right))   // only paint where a lane meets something
                {
                    if (IsLn(left) && IsLn(right) && left != right)
                    { Stripe(uOff - 0.16f, Yellow, false); Stripe(uOff + 0.16f, Yellow, false); }   // double-yellow centreline
                    else if (left == KTrn || right == KTrn)
                    { float toTurn = right == KTrn ? +1f : -1f; Stripe(uOff - 0.18f * toTurn, Yellow, false); Stripe(uOff + 0.18f * toTurn, Yellow, true); }
                    else if (IsLn(left) && IsLn(right))
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

        // Cross-section kinds, left→right. Shared by the 3D mesh (BuildXS) and the lane markings
        // (BuildLaneLines) so they always line up: parapet (elevated) · shoulder/sidewalk · curb · lanes ·
        // median / turn lane. Curb, median and parapet are raised boxes; shoulders are consistent in all modes.
        public const int KPar = 0, KEdge = 1, KCurb = 2, KLnBA = 3, KMed = 4, KTrn = 5, KLnAB = 6;

        // Colour for a layout kind (for the 2D cross-section strip in the designer / palette).
        public static Color LayoutColor(int k, bool sidewalk)
        {
            switch (k)
            {
                case KPar:  return new Color(0.50f, 0.51f, 0.54f);                         // parapet (steel/concrete)
                case KEdge: return sidewalk ? new Color(0.85f, 0.85f, 0.86f) : new Color(0.20f, 0.21f, 0.22f);  // sidewalk / shoulder
                case KCurb: return new Color(0.72f, 0.72f, 0.72f);                         // curb (light gray)
                case KMed:  return new Color(0.32f, 0.42f, 0.26f);                         // median (planted)
                case KTrn:  return new Color(0.42f, 0.36f, 0.14f);                         // turn lane
                default:    return new Color(0.30f, 0.31f, 0.33f);                         // lanes
            }
        }

        public static System.Collections.Generic.List<(float w, int k)> Layout(RoadProfile p)
        {
            var s = new System.Collections.Generic.List<(float, int)>();
            void A(float w, int k) { if (w > 0.01f) s.Add((w, k)); }
            float shBA = p.ShoulderBA != null ? p.ShoulderBA.Width : 0f;
            float shAB = p.ShoulderAB != null ? p.ShoulderAB.Width : 0f;
            if (p.Elevated && !p.Guardrails) A(0.3f, KPar);   // guardrail replaces the concrete parapet
            A(shBA, KEdge);
            if (p.Curbs) A(0.5f, KCurb);
            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) A(p.BA.Lanes[i].Width, KLnBA);
            if (p.Median != null) A(p.Median.Width, KMed);
            else if (p.TurnLane != null) A(p.TurnLane.Width, KTrn);
            for (int i = 0; i < p.AB.Lanes.Count; i++) A(p.AB.Lanes[i].Width, KLnAB);
            if (p.Curbs) A(0.5f, KCurb);
            A(shAB, KEdge);
            if (p.Elevated && !p.Guardrails) A(0.3f, KPar);   // guardrail replaces the concrete parapet
            return s;
        }

        // RoadProfile → RoadCrossSection. With curbs the shoulder/sidewalk is RAISED even with the curb top
        // (the curb is the step down to the road). Elevated adds a deck + 1 m concrete parapet (unless the
        // road has guardrails, which are built as separate post/rail geometry). Elevated forces shoulders.
        static RoadCrossSection BuildXS(RoadProfile p)
        {
            var xs = new RoadCrossSection();
            if (p.Elevated) xs.Thickness = 0.6f;   // deck slab
            RoadSurface edge = (p.Sidewalks && !p.Elevated) ? RoadSurface.Sidewalk : RoadSurface.Shoulder;
            float shBA = p.ShoulderBA != null ? p.ShoulderBA.Width : 0f;
            float shAB = p.ShoulderAB != null ? p.ShoulderAB.Width : 0f;
            bool curbs = p.Curbs;
            bool par = p.Elevated && !p.Guardrails;
            const float cH = 0.25f, cW = 0.5f;

            if (par) { xs.Step(1f, RoadSurface.Concrete); xs.Flat(0.3f, RoadSurface.Concrete); xs.Step(-1f, RoadSurface.Concrete); }
            if (curbs)
            {
                xs.Step(cH, edge);                        // outer face up to the raised verge
                if (shBA > 0.01f) xs.Flat(shBA, edge);    // shoulder/sidewalk, raised even with the curb
                xs.Flat(cW, RoadSurface.Curb);            // curb top (light gray)
                xs.Step(-cH, RoadSurface.Curb);           // curb face down to the lane
            }
            else if (shBA > 0.01f) xs.Flat(shBA, edge);

            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) xs.Lane(p.BA.Lanes[i].Width);
            if (p.Median != null) xs.Median(p.Median.Width, 0.25f);
            else if (p.TurnLane != null) xs.Lane(p.TurnLane.Width);
            for (int i = 0; i < p.AB.Lanes.Count; i++) xs.Lane(p.AB.Lanes[i].Width);

            if (curbs)
            {
                xs.Step(cH, RoadSurface.Curb);            // curb face up
                xs.Flat(cW, RoadSurface.Curb);            // curb top
                if (shAB > 0.01f) xs.Flat(shAB, edge);    // raised shoulder/sidewalk
                xs.Step(-cH, edge);                       // outer face down
            }
            else if (shAB > 0.01f) xs.Flat(shAB, edge);
            if (par) { xs.Step(1f, RoadSurface.Concrete); xs.Flat(0.3f, RoadSurface.Concrete); xs.Step(-1f, RoadSurface.Concrete); }
            return xs;
        }

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
            float totalU = 0f; foreach (var (w, _) in Layout(p)) totalU += w;
            if (totalU < 0.5f) return;
            float baseY = Far.y + (p.Curbs ? 0.25f : 0f);
            _gv.Clear(); _gtPost.Clear(); _gtRail.Clear();
            GuardrailSide(Far.z - totalU * 0.5f, baseY);
            GuardrailSide(Far.z + totalU * 0.5f, baseY);

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
        public void SetActive(bool on) { EnsureRig(); _cam.enabled = on; if (_road != null) _road.SetActive(on); if (_lines != null) _lines.SetActive(on); if (_guard != null) _guard.SetActive(on); }

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

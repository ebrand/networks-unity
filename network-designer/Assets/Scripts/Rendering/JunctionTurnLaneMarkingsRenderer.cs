// Per-vertex overlay that carries a TURN LANE's yellow markings across a
// promoted collinear junction. A turn lane (TWLTL) is drivable asphalt, so
// the surface is already continuous via the intersection fill — only its
// flanking yellow lines (solid outer + dashed inner, per side) break at the
// setback. This draws those lines across the gap, tapering between the two
// roads' turn-lane half-widths.
//
// Spawned only when BOTH approaches at a promoted collinear joint have a
// turn lane (no median); the turn-lane↔median case is handled by the median
// bridge instead. Centered-on-axis like the median bridge (correct in
// AxisSplit / for symmetric roads).

using System.Collections.Generic;
using UnityEngine;

namespace NetworkDesigner.Rendering
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class JunctionTurnLaneMarkingsRenderer : MonoBehaviour
    {
        [Header("Source (world XZ)")]
        public Vector2 Center0;
        public Vector2 Center1;
        public float Half0; // turn-lane half-width at Center0
        public float Half1; // turn-lane half-width at Center1
        // When a small median runs through this joint it sits on one side and
        // covers that side's yellow lines — suppress them. -1 = the -PerpRight
        // side, +1 = the +PerpRight side, 0 = draw both.
        public int SuppressSide = 0;

        [Header("Style")]
        public float LineWidth = 0.15f;
        public float StripeInset = 0.3f;
        public float DashLength = 3f;
        public float DashGap = 9f;
        public float MarkingHeight = 0.01f;
        public Material CenterlineMaterial;
        public Color CenterlineColor = new Color(0.95f, 0.78f, 0.15f);

        Mesh _mesh;

        void Start() { Rebuild(); }

        public void Rebuild()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();

            Vector2 axis = Center1 - Center0;
            float len = axis.magnitude;
            if (len < 1e-4f || (Half0 <= 0f && Half1 <= 0f))
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            Vector2 fwd = axis / len;
            Vector2 rt = new Vector2(fwd.y, -fwd.x); // PerpRight
            Vector3 origin = new Vector3(Center0.x, MarkingHeight, Center0.y);
            Vector3 fwd3 = new Vector3(fwd.x, 0f, fwd.y);
            Vector3 rt3 = new Vector3(rt.x, 0f, rt.y);
            float half = LineWidth * 0.5f;

            var verts = new List<Vector3>();
            var tris = new List<int>();

            // Two sides: solid line at ±turnHalf, dashed line just inside it.
            for (int s = -1; s <= 1; s += 2)
            {
                if (s == SuppressSide) continue; // small median covers this side
                float solid0 = s * Half0;
                float solid1 = s * Half1;
                AddRibbon(verts, tris, origin, fwd3, rt3, 0f, len, len,
                    solid0, solid1, half);

                // Dashed inner stripe (toward centerline). Skip if the inset
                // would cross the centerline (turn lane narrower than 2×inset).
                float dash0 = s * (Half0 - StripeInset);
                float dash1 = s * (Half1 - StripeInset);
                if (Half0 - StripeInset > 0f && Half1 - StripeInset > 0f)
                    AddDashed(verts, tris, origin, fwd3, rt3, len,
                        dash0, dash1, half);
            }

            if (verts.Count == 0)
            {
                if (_mesh != null) _mesh.Clear();
                mf.sharedMesh = _mesh;
                return;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "JunctionTurnLaneMarkings" };
                mf.sharedMesh = _mesh;
            }
            else
            {
                _mesh.Clear();
            }
            _mesh.SetVertices(verts);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            Material mat = CenterlineMaterial != null
                ? CenterlineMaterial
                : CreateUnlit(CenterlineColor, "JunctionTurnLaneMat");
            mr.sharedMaterials = new[] { mat };
        }

        // One tapered ribbon (a quad) of width `2*half`, centered on the
        // line whose lateral offset goes offA→offB along [alongA, alongB].
        // Winding matches RoadRenderer.AppendQuad (CW from +Y → faces up).
        static void AddRibbon(List<Vector3> verts, List<int> tris,
            Vector3 origin, Vector3 fwd, Vector3 rt,
            float alongA, float alongB, float len,
            float offStart, float offEnd, float half)
        {
            float tA = len > 0f ? alongA / len : 0f;
            float tB = len > 0f ? alongB / len : 1f;
            float offA = Mathf.Lerp(offStart, offEnd, tA);
            float offB = Mathf.Lerp(offStart, offEnd, tB);
            int b = verts.Count;
            verts.Add(origin + fwd * alongA + rt * (offA - half));
            verts.Add(origin + fwd * alongB + rt * (offB - half));
            verts.Add(origin + fwd * alongB + rt * (offB + half));
            verts.Add(origin + fwd * alongA + rt * (offA + half));
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        void AddDashed(List<Vector3> verts, List<int> tris,
            Vector3 origin, Vector3 fwd, Vector3 rt, float len,
            float offStart, float offEnd, float half)
        {
            if (DashLength <= 0f) return;
            float period = DashLength + Mathf.Max(0f, DashGap);
            float pos = 0f;
            int safety = 0;
            while (pos < len && safety < 100000)
            {
                float ds = pos;
                float de = Mathf.Min(pos + DashLength, len);
                if (de > ds)
                    AddRibbon(verts, tris, origin, fwd, rt, ds, de, len,
                        offStart, offEnd, half);
                pos += period;
                safety++;
            }
        }

        static Material CreateUnlit(Color c, string name)
        {
            Shader sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Standard");
            return new Material(sh) { name = name, color = c };
        }
    }
}

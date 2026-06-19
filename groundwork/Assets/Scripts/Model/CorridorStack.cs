// The authored cross-section model for the Transportation Corridor Designer: an ORDERED STACK of typed
// corridor segments per direction. This replaces RoadProfile as the source of truth — RoadProfile is now a
// DERIVED projection (see ToRoadProfile) that the car-agent sim, GeometryResolver and RoadNetworkBridge keep
// reading unchanged, while the renderer reads the stack directly (it can express things RoadProfile can't:
// arbitrary ordering, per-segment fence/parapet/guardrail/HOV, rail bands).
//
// AB / BA are ordered from the CENTERLINE OUTWARD (index 0 = innermost). The on-axis treatment (median / turn)
// is just the innermost Median/Turn segment; there is no separate centre list (the designer only has A→B / B→A).

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NetworkDesigner.Model
{
    /// <summary>A corridor segment's transportation kind. Drivable-by-cars = Traffic only (Turn is the centre lane).</summary>
    public enum CorridorType { Traffic, Turn, Bike, Shoulder, Sidewalk, Median, Rail }

    /// <summary>One band in a cross-section stack: a kind + width, plus per-segment attributes.</summary>
    [Serializable]
    public class CorridorSegment
    {
        public CorridorType Type = CorridorType.Traffic;
        public float Width = 4f;
        [JsonProperty("hov")] public bool HOV;   // Traffic only: high-occupancy lane
        public bool Fence;           // a fence on this band
        public bool Parapet;         // a parapet wall on this band
        public float ParapetHeight = 1f;
        public bool Guardrail;       // a guardrail on this band

        public CorridorSegment() { }
        public CorridorSegment(CorridorType type, float width) { Type = type; Width = width; }
    }

    /// <summary>The full authored cross-section: an AB and a BA stack, each ordered centreline→outer.</summary>
    [Serializable]
    public class CorridorStack
    {
        // Segments that STRADDLE the centreline (median / central rail / turn lane) — the midline runs through
        // their middle, so they belong to neither direction. Rendered between the BA and AB stacks.
        [JsonProperty("center")] public List<CorridorSegment> Center = new List<CorridorSegment>();
        [JsonProperty("ab")] public List<CorridorSegment> AB = new List<CorridorSegment>();   // centreline → A-B outer edge
        [JsonProperty("ba")] public List<CorridorSegment> BA = new List<CorridorSegment>();   // centreline → B-A outer edge

        /// <summary>Standard default width (m) for a freshly-added segment of each type (from the mockup).</summary>
        public static float DefaultWidth(CorridorType t) => t switch
        {
            CorridorType.Traffic => 4f,
            CorridorType.Turn => 4f,
            CorridorType.Bike => 1.5f,
            CorridorType.Shoulder => 1.5f,
            CorridorType.Sidewalk => 2f,
            CorridorType.Median => 1.5f,
            CorridorType.Rail => 5f,
            _ => 3f,
        };

        // ── derived projection: the lane view the existing consumers read (cars / GeometryResolver / bridge) ──
        // Only Traffic segments are car lanes; an innermost Median/Turn → centre strip; shoulder = ALL non-traffic
        // width outboard of the outermost traffic lane (so junction outer edges stay correct even with bike/sidewalk
        // present). Rail/Bike/Sidewalk are not lanes — they only contribute width. (Navigable rail from stack
        // segments is Phase 2.)
        public RoadProfile ToRoadProfile(string id)
        {
            var p = new RoadProfile { Id = id, AB = new Side(), BA = new Side() };

            int ai = 0;
            foreach (CorridorSegment s in AB) if (s.Type == CorridorType.Traffic) p.AB.Lanes.Add(new Lane { Id = "a" + ai++, Width = s.Width });
            int bi = 0;
            foreach (CorridorSegment s in BA) if (s.Type == CorridorType.Traffic) p.BA.Lanes.Add(new Lane { Id = "b" + bi++, Width = s.Width });

            // Centre treatment = the straddling Center segments: a Turn there → turn lane, else any Median/Rail
            // → a median-width centre gap. Width = total Center width so the lanes are offset correctly.
            float centerW = 0f; bool hasTurn = false, hasMedianish = false;
            foreach (CorridorSegment s in Center)
            {
                centerW += s.Width;
                if (s.Type == CorridorType.Turn) hasTurn = true;
                else if (s.Type == CorridorType.Median || s.Type == CorridorType.Rail) hasMedianish = true;
            }
            if (hasTurn) p.TurnLane = new TurnLane { Width = centerW };
            else if (hasMedianish) p.Median = new Median { Width = centerW };

            p.ShoulderAB = new Shoulder { Width = OutboardNonTrafficWidth(AB) };
            p.ShoulderBA = new Shoulder { Width = OutboardNonTrafficWidth(BA) };

            p.Curbs = HasType(AB, CorridorType.Sidewalk) || HasType(BA, CorridorType.Sidewalk);
            p.Sidewalks = p.Curbs;
            p.Elevated = AnyAttr(a => a.Parapet);
            p.Guardrails = AnyAttr(a => a.Guardrail);
            return p;
        }

        // Total width of segments outboard of (after) the last Traffic lane in a centreline→outer stack.
        static float OutboardNonTrafficWidth(List<CorridorSegment> side)
        {
            int last = -1;
            for (int i = 0; i < side.Count; i++) if (side[i].Type == CorridorType.Traffic) last = i;
            float w = 0f;
            for (int i = last + 1; i < side.Count; i++) w += side[i].Width;
            return w;
        }

        static bool HasType(List<CorridorSegment> side, CorridorType t)
        {
            foreach (CorridorSegment s in side) if (s.Type == t) return true;
            return false;
        }

        bool AnyAttr(Func<CorridorSegment, bool> pred)
        {
            foreach (CorridorSegment s in Center) if (pred(s)) return true;
            foreach (CorridorSegment s in AB) if (pred(s)) return true;
            foreach (CorridorSegment s in BA) if (pred(s)) return true;
            return false;
        }

        // ── migration: build an editable stack from an existing RoadProfile (so old saved profiles open here) ──
        public static CorridorStack FromRoadProfile(RoadProfile p)
        {
            var st = new CorridorStack();
            if (p == null) return st;

            // Centre (turn lane or median, mutually exclusive) straddles the axis → Center.
            if (p.TurnLane != null) st.Center.Add(new CorridorSegment(CorridorType.Turn, p.TurnLane.Width));
            else if (p.Median != null) st.Center.Add(new CorridorSegment(CorridorType.Median, p.Median.Width));

            foreach (Lane l in p.AB.Lanes) st.AB.Add(MakeLane(l.Width, p));
            AddEdge(st.AB, p, p.ShoulderAB);
            foreach (Lane l in p.BA.Lanes) st.BA.Add(MakeLane(l.Width, p));
            AddEdge(st.BA, p, p.ShoulderBA);

            // Rail corridor add-on (the pre-stack rail feature) → a Rail segment on its side (centre → Center).
            if (p.RailCorridor != null && p.RailCorridor.Tracks > 0)
            {
                var rail = new CorridorSegment(CorridorType.Rail, Math.Max(2f, p.RailCorridor.TrackSpacing * p.RailCorridor.Tracks));
                switch (p.RailCorridor.Side)
                {
                    case RailCorridorSide.Right: st.BA.Add(rail); break;
                    case RailCorridorSide.Left: st.AB.Add(rail); break;
                    default: st.Center.Add(rail); break;
                }
            }
            return st;
        }

        static CorridorSegment MakeLane(float w, RoadProfile p) =>
            new CorridorSegment(CorridorType.Traffic, w) { Parapet = p.Elevated && !p.Guardrails, Guardrail = p.Guardrails };

        static void AddEdge(List<CorridorSegment> side, RoadProfile p, Shoulder sh)
        {
            float w = sh != null ? sh.Width : 0f;
            if (w <= 0.01f && !p.Sidewalks) return;
            var seg = new CorridorSegment(p.Sidewalks && !p.Elevated ? CorridorType.Sidewalk : CorridorType.Shoulder,
                                          w > 0.01f ? w : 2f)
            { Parapet = p.Elevated && !p.Guardrails, Guardrail = p.Guardrails };
            side.Add(seg);
        }
    }
}

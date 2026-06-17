// The single source of truth for a road profile's cross-section layout: an ordered list of (width, kind)
// strips across the section, left→right. Shared by the 3D designer preview, the 2D cross-section strip /
// thumbnails, AND the plan corridor schematic, so they all reflect the same features (curbs, sidewalks,
// parapets, guardrails) and the corridor footprint is the true full width.

using System.Collections.Generic;
using UnityEngine;
using NetworkDesigner.Model;

namespace NetworkDesigner.Roads
{
    public static class RoadLayout
    {
        public const int Guard = 0, Parapet = 1, Edge = 2, Curb = 3, LaneBA = 4, Median = 5, TurnLane = 6, LaneAB = 7;

        public const float GuardWidth = 0.4f, ParapetWidth = 0.3f, CurbWidth = 0.5f;

        public static bool IsLane(int k) => k == LaneBA || k == LaneAB;

        // Strips across the section. Guard (shoulders only) replaces the parapet when elevated; curbs add a
        // band each side; the centre is a median, a turn lane, or nothing.
        public static List<(float w, int k)> Of(RoadProfile p)
        {
            var s = new List<(float, int)>();
            void A(float w, int k) { if (w > 0.01f) s.Add((w, k)); }

            bool sidewalkEdge = p.Sidewalks && !p.Elevated;
            bool guard = p.Guardrails && !sidewalkEdge;   // guardrails need shoulders
            float shBA = p.ShoulderBA != null ? p.ShoulderBA.Width : 0f;
            float shAB = p.ShoulderAB != null ? p.ShoulderAB.Width : 0f;

            if (guard) A(GuardWidth, Guard);
            else if (p.Elevated) A(ParapetWidth, Parapet);
            A(shBA, Edge);
            if (p.Curbs) A(CurbWidth, Curb);
            for (int i = p.BA.Lanes.Count - 1; i >= 0; i--) A(p.BA.Lanes[i].Width, LaneBA);
            if (p.Median != null) A(p.Median.Width, Median);
            else if (p.TurnLane != null) A(p.TurnLane.Width, TurnLane);
            for (int i = 0; i < p.AB.Lanes.Count; i++) A(p.AB.Lanes[i].Width, LaneAB);
            if (p.Curbs) A(CurbWidth, Curb);
            A(shAB, Edge);
            if (guard) A(GuardWidth, Guard);
            else if (p.Elevated) A(ParapetWidth, Parapet);
            return s;
        }

        // Full corridor footprint width — lanes + shoulders/sidewalks + curbs + parapets/guardrails.
        public static float Width(RoadProfile p)
        {
            float t = 0f;
            foreach (var (w, _) in Of(p)) t += w;
            return t;
        }

        // Strip colour for the 2D cross-section preview / thumbnails.
        public static Color KindColor(int k, bool sidewalk)
        {
            switch (k)
            {
                case Guard:    return new Color(0.56f, 0.57f, 0.60f);   // steel
                case Parapet:  return new Color(0.50f, 0.51f, 0.54f);   // concrete
                case Edge:     return sidewalk ? new Color(0.85f, 0.85f, 0.86f) : new Color(0.20f, 0.21f, 0.22f);
                case Curb:     return new Color(0.72f, 0.72f, 0.72f);   // light gray
                case Median:   return new Color(0.32f, 0.42f, 0.26f);   // planted
                case TurnLane: return new Color(0.42f, 0.36f, 0.14f);   // amber
                default:       return new Color(0.30f, 0.31f, 0.33f);   // lanes
            }
        }
    }
}

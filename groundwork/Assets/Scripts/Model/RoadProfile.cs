// C# strawman port of road-designer/src/model/types.ts.
//
// JSON wire format: matches road-config.json produced by the React
// tool. The importer in NetworkDesigner.Import.ConfigImporter applies a
// CamelCaseNamingStrategy to map PascalCase field names to camelCase
// JSON keys, so most fields need no annotations. The few that DO need
// explicit [JsonProperty] are ones where camelCase rules can't recover
// the JSON name from the C# name — specifically two-letter acronyms
// like AB/BA which would otherwise come out as "aB"/"bA".

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NetworkDesigner.Model
{
    /// <summary>
    /// A single lane in a road's cross-section. Carries no spatial
    /// awareness — whether it ends up left or right of the centerline
    /// is decided by the Network's DriveSide at render time.
    /// </summary>
    [Serializable]
    public class Lane
    {
        public string Id;
        /// <summary>Lane width in meters.</summary>
        public float Width;
    }

    /// <summary>
    /// One side of the road, carrying 0..N lanes for one travel direction.
    /// Index 0 is innermost (closest to the centerline).
    /// </summary>
    [Serializable]
    public class Side
    {
        public List<Lane> Lanes = new List<Lane>();
    }

    /// <summary>
    /// Shoulder strip on the outer edge of a side, away from the centerline.
    /// </summary>
    [Serializable]
    public class Shoulder
    {
        /// <summary>Shoulder width in meters.</summary>
        public float Width;
    }

    /// <summary>
    /// Median strip centered on the centerline. Only present on two-way
    /// roads; one-way roads must have no median (enforced at the network
    /// level — see Network.cs and the React tool's validation).
    /// </summary>
    [Serializable]
    public class Median
    {
        /// <summary>Median width in meters.</summary>
        public float Width;
    }

    /// <summary>
    /// Two-way left-turn lane (TWLTL / "suicide lane") centered on the
    /// road. Drivable asphalt strip flanked by yellow markings — used
    /// for in-segment left turns and curb cuts. Mutually exclusive with
    /// Median (a road has either a median, a turn lane, or neither).
    /// As of this writing, agents don't yet USE the turn lane for
    /// turning — it's rendered now and gets traffic logic when curb
    /// cuts + mid-segment left turns land.
    /// </summary>
    [Serializable]
    public class TurnLane
    {
        /// <summary>Turn-lane width in meters.</summary>
        public float Width;
    }

    /// <summary>Which side of the road a rail corridor runs on.</summary>
    public enum RailCorridorSide { Left, Right, Center }

    /// <summary>
    /// An optional rail corridor carried by the road: one or more parallel
    /// tracks generated alongside (Left/Right) or down the middle (Center)
    /// of the road when it's built. Null = no rail. The tracks become real
    /// edges in the rail network (rendered + navigable); see
    /// RailTrackLayer.RegenerateRoadCorridors.
    /// </summary>
    [Serializable]
    public class RailCorridor
    {
        /// <summary>Number of parallel tracks (>= 1).</summary>
        public int Tracks = 1;
        /// <summary>Which side of the road the corridor runs on.</summary>
        public RailCorridorSide Side = RailCorridorSide.Right;
        /// <summary>Standard track-to-track centre spacing, meters.</summary>
        public float TrackSpacing = 5f;
        /// <summary>Gap from the outer lane edge to the nearest track centre (Left/Right only), meters.</summary>
        public float LaneClearance = 3f;
    }

    /// <summary>
    /// A road's cross-section profile: lanes per direction, optional
    /// median, and shoulders. Reused by NetworkRoad.Profile in the
    /// network model.
    /// </summary>
    [Serializable]
    public class RoadProfile
    {
        public string Id;
        /// <summary>Lanes carrying traffic from vertex A toward vertex B.</summary>
        [JsonProperty("ab")] public Side AB = new Side();
        /// <summary>Lanes carrying traffic from vertex B toward vertex A.</summary>
        [JsonProperty("ba")] public Side BA = new Side();
        /// <summary>Null when the road has no median (always null for one-way roads).</summary>
        public Median Median;
        /// <summary>Null when the road has no turn lane. Mutually exclusive with Median.</summary>
        public TurnLane TurnLane;
        /// <summary>Null when the road carries no rail. Generates real, navigable rail edges on build.</summary>
        public RailCorridor RailCorridor;
        public Shoulder ShoulderAB = new Shoulder { Width = 1f };
        public Shoulder ShoulderBA = new Shoulder { Width = 1f };
        /// <summary>Raised curb (0.25 m high × 0.5 m wide) between the outer lanes and the edge.</summary>
        public bool Curbs;
        /// <summary>Edge is a raised sidewalk (white) instead of a flat shoulder; shoulder width is reused as the sidewalk width.</summary>
        public bool Sidewalks;
        /// <summary>Road sits on a deck with 1 m edge parapets. Forces shoulders (no sidewalks); curbs still allowed.</summary>
        public bool Elevated;
        /// <summary>When elevated, the parapets are metal guardrails instead of the default concrete parapets.</summary>
        public bool Guardrails;

        /// <summary>True when one of the two sides has zero lanes.</summary>
        [JsonIgnore] public bool IsOneWay => AB.Lanes.Count == 0 || BA.Lanes.Count == 0;

        /// <summary>
        /// Width of the center strip (median or turn lane) in meters,
        /// or 0 if neither is present. Median + TurnLane are mutually
        /// exclusive; if both are somehow set, Median takes precedence
        /// (caller logged for diagnosis).
        /// </summary>
        [JsonIgnore] public float CenterStripWidth
        {
            get
            {
                if (Median != null) return Median.Width;
                if (TurnLane != null) return TurnLane.Width;
                return 0f;
            }
        }

        /// <summary>Total cross-section width (shoulder + lanes + center strip + lanes + shoulder), meters.</summary>
        [JsonIgnore] public float TotalWidth
        {
            get
            {
                float w = ShoulderAB.Width + ShoulderBA.Width;
                foreach (Lane l in AB.Lanes) w += l.Width;
                foreach (Lane l in BA.Lanes) w += l.Width;
                w += CenterStripWidth;
                return w;
            }
        }

        /// <summary>
        /// Structural cross-section equality, used to decide whether two
        /// roads meeting at a collinear vertex form a seamless joint or
        /// need a transition (lane drop / width change). Orientation-aware:
        /// a road drawn in the opposite direction has its AB/BA sides (and
        /// shoulders) swapped, so we accept either the same-facing pairing
        /// (AB↔AB, BA↔BA) or the flipped one (AB↔BA, BA↔AB). The center
        /// strip is centered and compared by kind (median / turn lane /
        /// neither) and width. Orientation-naive on mirror-asymmetric
        /// profiles is acceptable for now (Phase 1 limitation).
        /// </summary>
        public bool Matches(RoadProfile other)
        {
            if (other == null) return false;
            if (!CenterStripMatches(other)) return false;

            bool sameFacing =
                SidesEqual(AB, other.AB) && SidesEqual(BA, other.BA)
                && Approx(ShoulderAB.Width, other.ShoulderAB.Width)
                && Approx(ShoulderBA.Width, other.ShoulderBA.Width);
            bool flipped =
                SidesEqual(AB, other.BA) && SidesEqual(BA, other.AB)
                && Approx(ShoulderAB.Width, other.ShoulderBA.Width)
                && Approx(ShoulderBA.Width, other.ShoulderAB.Width);
            return sameFacing || flipped;
        }

        bool CenterStripMatches(RoadProfile other)
        {
            bool kindEqual =
                (Median != null) == (other.Median != null)
                && (TurnLane != null) == (other.TurnLane != null);
            return kindEqual && Approx(CenterStripWidth, other.CenterStripWidth);
        }

        static bool SidesEqual(Side a, Side b)
        {
            if (a.Lanes.Count != b.Lanes.Count) return false;
            for (int i = 0; i < a.Lanes.Count; i++)
                if (!Approx(a.Lanes[i].Width, b.Lanes[i].Width)) return false;
            return true;
        }

        static bool Approx(float a, float b) => System.Math.Abs(a - b) < 1e-4f;
    }
}

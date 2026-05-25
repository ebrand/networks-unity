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
        public Shoulder ShoulderAB = new Shoulder { Width = 1f };
        public Shoulder ShoulderBA = new Shoulder { Width = 1f };

        /// <summary>True when one of the two sides has zero lanes.</summary>
        [JsonIgnore] public bool IsOneWay => AB.Lanes.Count == 0 || BA.Lanes.Count == 0;

        /// <summary>Total cross-section width (shoulder + lanes + median + lanes + shoulder), meters.</summary>
        [JsonIgnore] public float TotalWidth
        {
            get
            {
                float w = ShoulderAB.Width + ShoulderBA.Width;
                foreach (Lane l in AB.Lanes) w += l.Width;
                foreach (Lane l in BA.Lanes) w += l.Width;
                if (Median != null) w += Median.Width;
                return w;
            }
        }
    }
}

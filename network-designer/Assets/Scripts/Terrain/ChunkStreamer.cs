// Drives ChunkWorld streaming: keep the resident chunk bubble centred on the camera, only
// re-evaluating when the camera crosses into a new chunk (cheap — avoids per-frame churn).
// Created by TerrainDesigner when chunk-test mode starts; removed when it stops.

using UnityEngine;

namespace NetworkDesigner.Terrain
{
    public class ChunkStreamer : MonoBehaviour
    {
        public Camera Cam;

        void Update()
        {
            if (!ChunkWorld.Active) return;
            if (Cam == null) { Cam = Camera.main; if (Cam == null) return; }
            // Hold SPACE to stream/reposition the bubble (when it's locked); release to re-lock.
            ChunkWorld.StreamHeld = Input.GetKey(KeyCode.Space);
            // Tick every frame: recomputes the resident set when streaming, else just re-LODs the
            // frozen set as you pan/zoom; drains the budgeted build queue continuously.
            ChunkWorld.Tick(Cam);
        }
    }
}

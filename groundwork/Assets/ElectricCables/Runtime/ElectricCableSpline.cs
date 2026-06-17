using UnityEngine;

namespace Game.Spline
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class ElectricCableSpline : MonoBehaviour
    {
        public enum CableMode
        {
            TransformEndpoints,
            LocalPoints
        }

        public enum CableVisualMode
        {
            LineRenderer,
            MeshTube
        }

        [Header("Endpoints")]
        [SerializeField] private CableMode mode = CableMode.TransformEndpoints;
        [SerializeField] private Transform startPoint;
        [SerializeField] private Transform endPoint;
        [SerializeField] private Vector3 localStartPoint = new Vector3(-2f, 0f, 0f);
        [SerializeField] private Vector3 localEndPoint = new Vector3(2f, 0f, 0f);

        [Header("Endpoint Rotation")]
        [SerializeField] private bool useEndpointRotation = true;
        [SerializeField] private float startRollDegrees;
        [SerializeField] private float endRollDegrees;

        [Header("Cable Shape")]
        [SerializeField] [Min(2)] private int segments = 24;
        [SerializeField] [Min(0f)] private float sag = 0.65f;
        [SerializeField] private Vector3 worldSideOffset;
        [SerializeField] private bool autoSagFromDistance = true;
        [SerializeField] [Min(0f)] private float sagPerMeter = 0.08f;
        [SerializeField] [Min(0f)] private float maxAutoSag = 2.5f;

        [Header("Broken Cable")]
        [SerializeField] private bool brokenCable;
        [SerializeField] [Range(0.05f, 0.95f)] private float breakNormalizedPosition = 0.5f;
        [SerializeField] [Range(0.01f, 0.35f)] private float breakGapNormalized = 0.08f;
        [SerializeField] [Min(0f)] private float brokenEndDrop = 0.45f;
        [SerializeField] private Vector3 brokenEndSideOffset = new Vector3(0.12f, 0f, 0f);

        [Header("Wind Sway")]
        [SerializeField] private bool windSway = true;
        [SerializeField] [Min(0f)] private float windAmplitude = 0.08f;
        [SerializeField] [Min(0f)] private float windSpeed = 1.3f;
        [SerializeField] [Min(0f)] private float windSpatialFrequency = 1.6f;
        [SerializeField] private Vector3 windDirection = new Vector3(1f, 0f, 0.25f);
        [SerializeField] [Range(0f, 1f)] private float endpointWindLock = 1f;

        [Header("LOD")]
        [SerializeField] private bool useDistanceLod = true;
        [SerializeField] private Camera lodCamera;
        [SerializeField] [Min(0f)] private float nearDistance = 35f;
        [SerializeField] [Min(0f)] private float midDistance = 80f;
        [SerializeField] [Min(0f)] private float farDistance = 140f;
        [SerializeField] [Min(2)] private int nearSegments = 24;
        [SerializeField] [Min(2)] private int midSegments = 14;
        [SerializeField] [Min(2)] private int farSegments = 8;
        [SerializeField] [Min(3)] private int nearRadialSegments = 8;
        [SerializeField] [Min(3)] private int midRadialSegments = 6;
        [SerializeField] [Min(3)] private int farRadialSegments = 4;
        [SerializeField] private bool disableBeyondFarDistance;

        [Header("Visual")]
        [SerializeField] private CableVisualMode visualMode = CableVisualMode.MeshTube;
        [SerializeField] private LineRenderer lineRenderer;
        [SerializeField] private MeshFilter meshFilter;
        [SerializeField] private MeshRenderer meshRenderer;
        [SerializeField] [Min(0.001f)] private float cableWidth = 0.045f;
        [SerializeField] [Min(3)] private int radialSegments = 8;
        [SerializeField] private Material cableMaterial;
        [SerializeField] private Color cableColor = new Color(0.03f, 0.03f, 0.03f, 1f);
        [SerializeField] private bool useWorldSpace = true;

        [Header("Bake")]
        [SerializeField] private bool bakedStatic;
        [SerializeField] private bool bakedKeepsWind = true;
        [SerializeField] private Mesh bakedMesh;
        [SerializeField] private int bakedSegments;
        [SerializeField] private int bakedRadialSegments;

        [Header("Editor")]
        [SerializeField] private bool rebuildInEditMode = true;
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color gizmoColor = new Color(1f, 0.75f, 0.2f, 1f);

        private Vector3[] cachedPoints;
        private Mesh generatedMesh;
        private Vector3[] meshVertices;
        private Vector3[] meshNormals;
        private Vector2[] meshUvs;
        private int[] meshTriangles;

        public Transform StartPoint
        {
            get => startPoint;
            set
            {
                startPoint = value;
                Rebuild();
            }
        }

        public Transform EndPoint
        {
            get => endPoint;
            set
            {
                endPoint = value;
                Rebuild();
            }
        }

        public LineRenderer Renderer => lineRenderer;
        public MeshFilter CableMeshFilter => meshFilter;
        public MeshRenderer CableMeshRenderer => meshRenderer;
        public bool IsBakedStatic => bakedStatic;
        public bool IsBrokenCable => brokenCable;

        private void Reset()
        {
            EnsureVisualComponents();
            Rebuild();
        }

        private void Awake()
        {
            EnsureVisualComponents();
            Rebuild();
        }

        private void OnEnable()
        {
            EnsureVisualComponents();
            Rebuild();
        }

        private void OnDisable()
        {
            if (generatedMesh == null || generatedMesh == bakedMesh)
            {
                return;
            }

            DestroyMesh(generatedMesh);
            generatedMesh = null;
        }

        private void OnValidate()
        {
            segments = Mathf.Max(2, segments);
            sag = Mathf.Max(0f, sag);
            sagPerMeter = Mathf.Max(0f, sagPerMeter);
            maxAutoSag = Mathf.Max(0f, maxAutoSag);
            cableWidth = Mathf.Max(0.001f, cableWidth);
            radialSegments = Mathf.Max(3, radialSegments);
            windAmplitude = Mathf.Max(0f, windAmplitude);
            windSpeed = Mathf.Max(0f, windSpeed);
            windSpatialFrequency = Mathf.Max(0f, windSpatialFrequency);
            nearSegments = Mathf.Max(2, nearSegments);
            midSegments = Mathf.Max(2, midSegments);
            farSegments = Mathf.Max(2, farSegments);
            nearRadialSegments = Mathf.Max(3, nearRadialSegments);
            midRadialSegments = Mathf.Max(3, midRadialSegments);
            farRadialSegments = Mathf.Max(3, farRadialSegments);
            nearDistance = Mathf.Max(0f, nearDistance);
            midDistance = Mathf.Max(nearDistance, midDistance);
            farDistance = Mathf.Max(midDistance, farDistance);
            bakedSegments = Mathf.Max(0, bakedSegments);
            bakedRadialSegments = Mathf.Max(0, bakedRadialSegments);

            if (!Application.isPlaying && !rebuildInEditMode)
            {
                return;
            }

            EnsureVisualComponents();
            Rebuild();
        }

        private void Update()
        {
            if (bakedStatic)
            {
                if ((Application.isPlaying || rebuildInEditMode) && bakedKeepsWind && windSway)
                {
                    RebuildInternal(true, true);
                }
                else
                {
                    EnsureVisualComponents();
                }

                return;
            }

            if (Application.isPlaying || rebuildInEditMode)
            {
                Rebuild();
            }
        }

        [ContextMenu("Rebuild Cable")]
        public void Rebuild()
        {
            RebuildInternal(false, true);
        }

        [ContextMenu("Bake Mesh Snapshot")]
        public void BakeMeshSnapshot()
        {
            bool previousBroken = brokenCable;
            bool previousUseDistanceLod = useDistanceLod;

            if (TryGetEndpoints(out Vector3 start, out Vector3 end))
            {
                bakedSegments = GetEffectiveSegments(start, end);
                bakedRadialSegments = GetEffectiveRadialSegments(start, end);
            }
            else
            {
                bakedSegments = segments;
                bakedRadialSegments = radialSegments;
            }

            RebuildInternal(true, false);

            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return;
            }

            if (bakedMesh != null && bakedMesh != generatedMesh)
            {
                DestroyMesh(bakedMesh);
            }

            bakedMesh = Instantiate(meshFilter.sharedMesh);
            bakedMesh.name = name + "_BakedCableMesh";
            bakedStatic = true;
            rebuildInEditMode = bakedKeepsWind;
            brokenCable = previousBroken;
            useDistanceLod = previousUseDistanceLod;

            EnsureMeshComponents();
            meshFilter.sharedMesh = bakedMesh;
            generatedMesh = bakedMesh;
            if (lineRenderer != null)
            {
                lineRenderer.enabled = false;
            }
        }

        [ContextMenu("Return To Dynamic")]
        public void ReturnToDynamic()
        {
            bakedStatic = false;
            rebuildInEditMode = true;
            bakedSegments = 0;
            bakedRadialSegments = 0;

            if (bakedMesh != null)
            {
                if (generatedMesh == bakedMesh)
                {
                    generatedMesh = null;
                }

                DestroyMesh(bakedMesh);
                bakedMesh = null;
            }

            if (generatedMesh != null)
            {
                generatedMesh.Clear();
            }

            EnsureVisualComponents();
            RebuildInternal(true, true);
        }

        public Vector3 EvaluatePoint(float t)
        {
            if (!TryGetEndpoints(out Vector3 start, out Vector3 end))
            {
                return transform.position;
            }

            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            return EvaluatePoint(start, end, Mathf.Clamp01(t), ResolveSag(start, end), time, true, false, 0f);
        }

        public bool TryGetEndpoints(out Vector3 start, out Vector3 end)
        {
            if (mode == CableMode.TransformEndpoints)
            {
                if (startPoint == null || endPoint == null)
                {
                    start = default;
                    end = default;
                    return false;
                }

                start = startPoint.position;
                end = endPoint.position;
                return true;
            }

            start = transform.TransformPoint(localStartPoint);
            end = transform.TransformPoint(localEndPoint);
            return true;
        }

        [ContextMenu("Create/Assign Visual Components")]
        public void EnsureVisualComponents()
        {
            if (bakedStatic)
            {
                EnsureMeshComponents();
                if (lineRenderer != null)
                {
                    lineRenderer.enabled = false;
                }

                if (meshFilter != null && bakedMesh != null)
                {
                    meshFilter.sharedMesh = bakedMesh;
                }

                return;
            }

            if (visualMode == CableVisualMode.LineRenderer)
            {
                EnsureLineRenderer();
                if (meshRenderer != null)
                {
                    meshRenderer.enabled = false;
                }

                return;
            }

            EnsureMeshComponents();
            if (lineRenderer != null)
            {
                lineRenderer.enabled = false;
            }
        }

        [ContextMenu("Create/Assign Line Renderer")]
        public void EnsureLineRenderer()
        {
            if (lineRenderer == null)
            {
                lineRenderer = GetComponent<LineRenderer>();
            }

            if (lineRenderer == null)
            {
                lineRenderer = gameObject.AddComponent<LineRenderer>();
            }

            lineRenderer.enabled = !bakedStatic && visualMode == CableVisualMode.LineRenderer;
            lineRenderer.useWorldSpace = useWorldSpace;
            lineRenderer.widthMultiplier = cableWidth;
            lineRenderer.numCapVertices = 6;
            lineRenderer.numCornerVertices = 4;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            lineRenderer.receiveShadows = true;

            if (cableMaterial != null)
            {
                lineRenderer.sharedMaterial = cableMaterial;
            }

            lineRenderer.startColor = cableColor;
            lineRenderer.endColor = cableColor;
        }

        [ContextMenu("Create/Assign Mesh Components")]
        public void EnsureMeshComponents()
        {
            if (meshFilter == null)
            {
                meshFilter = GetComponent<MeshFilter>();
            }

            if (meshFilter == null)
            {
                meshFilter = gameObject.AddComponent<MeshFilter>();
            }

            if (meshRenderer == null)
            {
                meshRenderer = GetComponent<MeshRenderer>();
            }

            if (meshRenderer == null)
            {
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
            }

            meshRenderer.enabled = bakedStatic || visualMode == CableVisualMode.MeshTube;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            meshRenderer.receiveShadows = true;

            if (cableMaterial != null)
            {
                meshRenderer.sharedMaterial = cableMaterial;
            }

            if (bakedStatic && bakedMesh != null)
            {
                generatedMesh = bakedMesh;
                meshFilter.sharedMesh = bakedMesh;
                return;
            }

            if (generatedMesh == null || generatedMesh == bakedMesh)
            {
                generatedMesh = new Mesh
                {
                    name = "Generated Electric Cable Mesh"
                };
                generatedMesh.MarkDynamic();
            }

            meshFilter.sharedMesh = generatedMesh;
        }

        private void RebuildInternal(bool forceEvenIfBaked, bool includeWind)
        {
            if (bakedStatic && !forceEvenIfBaked)
            {
                EnsureVisualComponents();
                return;
            }

            if (!TryGetEndpoints(out Vector3 start, out Vector3 end))
            {
                ClearVisuals();
                return;
            }

            if (!bakedStatic && disableBeyondFarDistance && IsBeyondFarDistance(start, end))
            {
                ClearVisuals();
                return;
            }

            EnsureVisualComponents();

            int effectiveSegments = bakedStatic && bakedSegments > 0 ? bakedSegments : GetEffectiveSegments(start, end);
            int effectiveRadialSegments = bakedStatic && bakedRadialSegments > 0 ? bakedRadialSegments : GetEffectiveRadialSegments(start, end);
            int pointCount = effectiveSegments + 1;
            if (cachedPoints == null || cachedPoints.Length != pointCount)
            {
                cachedPoints = new Vector3[pointCount];
            }

            float resolvedSag = ResolveSag(start, end);
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            for (int i = 0; i < pointCount; i++)
            {
                float t = i / (float)effectiveSegments;
                cachedPoints[i] = EvaluatePoint(start, end, t, resolvedSag, time, includeWind, false, 0f);
            }

            ApplyVisuals(cachedPoints, effectiveSegments, effectiveRadialSegments, includeWind);
        }

        private void ClearVisuals()
        {
            if (lineRenderer != null)
            {
                lineRenderer.positionCount = 0;
            }

            if (generatedMesh != null)
            {
                generatedMesh.Clear();
            }
        }

        private float ResolveSag(Vector3 start, Vector3 end)
        {
            if (!autoSagFromDistance)
            {
                return sag;
            }

            float distance = Vector3.Distance(start, end);
            return Mathf.Min(maxAutoSag, sag + distance * sagPerMeter);
        }

        private Vector3 EvaluatePoint(Vector3 start, Vector3 end, float t, float resolvedSag, float time, bool includeWind, bool brokenEnd, float brokenSign)
        {
            Vector3 point = Vector3.Lerp(start, end, t);
            float sagCurve = 4f * t * (1f - t);
            point += Vector3.down * (resolvedSag * sagCurve);
            point += worldSideOffset * sagCurve;

            if (brokenEnd)
            {
                point += Vector3.down * brokenEndDrop;
                point += brokenEndSideOffset * brokenSign;
            }

            if (includeWind && windSway && windAmplitude > 0f)
            {
                Vector3 wind = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
                float endpointMask = Mathf.Pow(sagCurve, Mathf.Lerp(1f, 3f, endpointWindLock));
                float phase = t * windSpatialFrequency * Mathf.PI * 2f + time * windSpeed;
                float wave = Mathf.Sin(phase) + Mathf.Sin(phase * 0.43f + 1.7f) * 0.35f;
                point += wind * (wave * windAmplitude * endpointMask);
            }

            return point;
        }

        private void ApplyVisuals(Vector3[] points, int effectiveSegments, int effectiveRadialSegments, bool includeWind)
        {
            if (visualMode == CableVisualMode.MeshTube || brokenCable || bakedStatic)
            {
                ApplyMeshTube(points, effectiveSegments, effectiveRadialSegments, includeWind);
            }
            else
            {
                ApplyLineRenderer(points);
            }
        }

        private void ApplyLineRenderer(Vector3[] points)
        {
            EnsureLineRenderer();
            lineRenderer.useWorldSpace = useWorldSpace;
            lineRenderer.widthMultiplier = cableWidth;
            lineRenderer.startColor = cableColor;
            lineRenderer.endColor = cableColor;

            if (cableMaterial != null)
            {
                lineRenderer.sharedMaterial = cableMaterial;
            }

            lineRenderer.positionCount = points.Length;

            if (useWorldSpace)
            {
                lineRenderer.SetPositions(points);
                return;
            }

            for (int i = 0; i < points.Length; i++)
            {
                lineRenderer.SetPosition(i, transform.InverseTransformPoint(points[i]));
            }
        }

        private void ApplyMeshTube(Vector3[] worldPoints, int effectiveSegments, int effectiveRadialSegments, bool includeWind)
        {
            EnsureMeshComponents();
            if (generatedMesh == null || worldPoints == null || worldPoints.Length < 2)
            {
                return;
            }

            if (brokenCable)
            {
                ApplyBrokenMeshTube(effectiveSegments, effectiveRadialSegments, includeWind);
            }
            else
            {
                ApplyContinuousMeshTube(worldPoints, effectiveRadialSegments);
            }
        }

        private void ApplyBrokenMeshTube(int effectiveSegments, int effectiveRadialSegments, bool includeWind)
        {
            if (!TryGetEndpoints(out Vector3 start, out Vector3 end))
            {
                return;
            }

            float gap = breakGapNormalized * 0.5f;
            float leftEnd = Mathf.Clamp01(breakNormalizedPosition - gap);
            float rightStart = Mathf.Clamp01(breakNormalizedPosition + gap);
            if (leftEnd <= 0.02f || rightStart >= 0.98f || leftEnd >= rightStart)
            {
                ApplyContinuousMeshTube(cachedPoints, effectiveRadialSegments);
                return;
            }

            int leftSegments = Mathf.Max(2, Mathf.RoundToInt(effectiveSegments * leftEnd));
            int rightSegments = Mathf.Max(2, Mathf.RoundToInt(effectiveSegments * (1f - rightStart)));
            Vector3[] leftPoints = BuildPointRange(start, end, 0f, leftEnd, leftSegments, includeWind, true, -1f);
            Vector3[] rightPoints = BuildPointRange(start, end, rightStart, 1f, rightSegments, includeWind, true, 1f);
            ApplyMultiSpanMeshTube(new[] { leftPoints, rightPoints }, effectiveRadialSegments);
        }

        private Vector3[] BuildPointRange(Vector3 start, Vector3 end, float t0, float t1, int segmentCount, bool includeWind, bool brokenEnd, float brokenSign)
        {
            Vector3[] points = new Vector3[segmentCount + 1];
            float resolvedSag = ResolveSag(start, end);
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
            for (int i = 0; i <= segmentCount; i++)
            {
                float localT = i / (float)segmentCount;
                float t = Mathf.Lerp(t0, t1, localT);
                bool isLooseEnd = brokenEnd && ((brokenSign < 0f && i == segmentCount) || (brokenSign > 0f && i == 0));
                points[i] = EvaluatePoint(start, end, t, resolvedSag, time, includeWind, isLooseEnd, brokenSign);
            }

            return points;
        }

        private void ApplyContinuousMeshTube(Vector3[] worldPoints, int effectiveRadialSegments)
        {
            ApplyMultiSpanMeshTube(new[] { worldPoints }, effectiveRadialSegments);
        }

        private void ApplyMultiSpanMeshTube(Vector3[][] spans, int effectiveRadialSegments)
        {
            int totalRings = 0;
            int totalSegments = 0;
            for (int i = 0; i < spans.Length; i++)
            {
                if (spans[i] == null || spans[i].Length < 2)
                {
                    continue;
                }

                totalRings += spans[i].Length;
                totalSegments += spans[i].Length - 1;
            }

            if (totalRings <= 1)
            {
                generatedMesh.Clear();
                return;
            }

            int vertexCount = totalRings * effectiveRadialSegments;
            int triangleIndexCount = totalSegments * effectiveRadialSegments * 6;

            if (meshVertices == null || meshVertices.Length != vertexCount)
            {
                meshVertices = new Vector3[vertexCount];
                meshNormals = new Vector3[vertexCount];
                meshUvs = new Vector2[vertexCount];
            }

            if (meshTriangles == null || meshTriangles.Length != triangleIndexCount)
            {
                meshTriangles = new int[triangleIndexCount];
            }

            float radius = cableWidth * 0.5f;
            int vertexCursor = 0;
            int triangleCursor = 0;

            for (int spanIndex = 0; spanIndex < spans.Length; spanIndex++)
            {
                Vector3[] points = spans[spanIndex];
                if (points == null || points.Length < 2)
                {
                    continue;
                }

                float totalLength = CalculatePolylineLength(points);
                float traversedLength = 0f;
                int spanVertexStart = vertexCursor;

                for (int ring = 0; ring < points.Length; ring++)
                {
                    Vector3 point = points[ring];
                    Vector3 tangent = CalculateTangent(points, ring);
                    GetFrame(tangent, ring / (float)(points.Length - 1), out Vector3 normal, out Vector3 binormal);

                    if (ring > 0)
                    {
                        traversedLength += Vector3.Distance(points[ring - 1], point);
                    }

                    float v = totalLength > 0.0001f ? traversedLength / totalLength : 0f;

                    for (int side = 0; side < effectiveRadialSegments; side++)
                    {
                        float angle = (side / (float)effectiveRadialSegments) * Mathf.PI * 2f;
                        Vector3 radial = normal * Mathf.Cos(angle) + binormal * Mathf.Sin(angle);
                        Vector3 worldVertex = point + radial * radius;
                        meshVertices[vertexCursor] = transform.InverseTransformPoint(worldVertex);
                        meshNormals[vertexCursor] = transform.InverseTransformDirection(radial).normalized;
                        meshUvs[vertexCursor] = new Vector2(side / (float)effectiveRadialSegments, v);
                        vertexCursor++;
                    }
                }

                for (int ring = 0; ring < points.Length - 1; ring++)
                {
                    for (int side = 0; side < effectiveRadialSegments; side++)
                    {
                        int current = spanVertexStart + ring * effectiveRadialSegments + side;
                        int next = spanVertexStart + ring * effectiveRadialSegments + (side + 1) % effectiveRadialSegments;
                        int currentUp = spanVertexStart + (ring + 1) * effectiveRadialSegments + side;
                        int nextUp = spanVertexStart + (ring + 1) * effectiveRadialSegments + (side + 1) % effectiveRadialSegments;

                        meshTriangles[triangleCursor++] = current;
                        meshTriangles[triangleCursor++] = currentUp;
                        meshTriangles[triangleCursor++] = next;
                        meshTriangles[triangleCursor++] = next;
                        meshTriangles[triangleCursor++] = currentUp;
                        meshTriangles[triangleCursor++] = nextUp;
                    }
                }
            }

            generatedMesh.Clear();
            generatedMesh.vertices = meshVertices;
            generatedMesh.normals = meshNormals;
            generatedMesh.uv = meshUvs;
            generatedMesh.triangles = meshTriangles;
            generatedMesh.RecalculateBounds();
        }

        private int GetEffectiveSegments(Vector3 start, Vector3 end)
        {
            if (!useDistanceLod)
            {
                return segments;
            }

            float distance = GetLodDistance(start, end);
            if (distance <= nearDistance)
            {
                return nearSegments;
            }

            if (distance <= midDistance)
            {
                return midSegments;
            }

            return farSegments;
        }

        private int GetEffectiveRadialSegments(Vector3 start, Vector3 end)
        {
            if (!useDistanceLod)
            {
                return radialSegments;
            }

            float distance = GetLodDistance(start, end);
            if (distance <= nearDistance)
            {
                return nearRadialSegments;
            }

            if (distance <= midDistance)
            {
                return midRadialSegments;
            }

            return farRadialSegments;
        }

        private bool IsBeyondFarDistance(Vector3 start, Vector3 end)
        {
            return useDistanceLod && GetLodDistance(start, end) > farDistance;
        }

        private float GetLodDistance(Vector3 start, Vector3 end)
        {
            Camera camera = lodCamera != null ? lodCamera : Camera.main;
            if (camera == null)
            {
                return 0f;
            }

            Vector3 midpoint = (start + end) * 0.5f;
            return Vector3.Distance(camera.transform.position, midpoint);
        }

        private void GetFrame(Vector3 tangent, float t, out Vector3 normal, out Vector3 binormal)
        {
            tangent = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;

            Vector3 startUp = GetEndpointUp(true, tangent);
            Vector3 endUp = GetEndpointUp(false, tangent);
            Vector3 up = Vector3.Slerp(startUp, endUp, Mathf.Clamp01(t));
            up = ProjectOnPlaneSafe(up, tangent, Vector3.up);

            float roll = Mathf.Lerp(startRollDegrees, endRollDegrees, Mathf.Clamp01(t));
            if (Mathf.Abs(roll) > 0.001f)
            {
                up = Quaternion.AngleAxis(roll, tangent) * up;
            }

            normal = up.normalized;
            binormal = Vector3.Cross(tangent, normal).normalized;
            normal = Vector3.Cross(binormal, tangent).normalized;
        }

        private Vector3 GetEndpointUp(bool start, Vector3 tangent)
        {
            if (!useEndpointRotation)
            {
                return ProjectOnPlaneSafe(Vector3.up, tangent, Vector3.forward);
            }

            Transform endpoint = start ? startPoint : endPoint;
            Vector3 sourceUp = endpoint != null ? endpoint.up : transform.up;
            return ProjectOnPlaneSafe(sourceUp, tangent, Vector3.up);
        }

        private static Vector3 ProjectOnPlaneSafe(Vector3 vector, Vector3 planeNormal, Vector3 fallback)
        {
            Vector3 projected = Vector3.ProjectOnPlane(vector, planeNormal);
            if (projected.sqrMagnitude > 0.0001f)
            {
                return projected.normalized;
            }

            projected = Vector3.ProjectOnPlane(fallback, planeNormal);
            if (projected.sqrMagnitude > 0.0001f)
            {
                return projected.normalized;
            }

            return Vector3.Cross(planeNormal, Vector3.right).sqrMagnitude > 0.0001f
                ? Vector3.Cross(planeNormal, Vector3.right).normalized
                : Vector3.Cross(planeNormal, Vector3.forward).normalized;
        }

        private static Vector3 CalculateTangent(Vector3[] points, int index)
        {
            if (index <= 0)
            {
                return (points[1] - points[0]).normalized;
            }

            if (index >= points.Length - 1)
            {
                return (points[index] - points[index - 1]).normalized;
            }

            return (points[index + 1] - points[index - 1]).normalized;
        }

        private static float CalculatePolylineLength(Vector3[] points)
        {
            float length = 0f;
            for (int i = 1; i < points.Length; i++)
            {
                length += Vector3.Distance(points[i - 1], points[i]);
            }

            return length;
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(mesh);
            }
            else
            {
                DestroyImmediate(mesh);
            }
        }

        private void OnDrawGizmos()
        {
            if (!drawGizmos || !TryGetEndpoints(out Vector3 start, out Vector3 end))
            {
                return;
            }

            Gizmos.color = brokenCable ? Color.red : gizmoColor;
            Vector3 previousPoint = start;
            float resolvedSag = ResolveSag(start, end);
            int gizmoSegments = bakedStatic && bakedSegments > 0 ? Mathf.Max(bakedSegments, 8) : Mathf.Max(GetEffectiveSegments(start, end), 8);
            float time = Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

            for (int i = 1; i <= gizmoSegments; i++)
            {
                float t = i / (float)gizmoSegments;
                if (brokenCable)
                {
                    float gap = breakGapNormalized * 0.5f;
                    if (t > breakNormalizedPosition - gap && t < breakNormalizedPosition + gap)
                    {
                        previousPoint = EvaluatePoint(start, end, Mathf.Clamp01(breakNormalizedPosition + gap), resolvedSag, time, true, true, 1f);
                        continue;
                    }
                }

                Vector3 point = EvaluatePoint(start, end, t, resolvedSag, time, true, false, 0f);
                Gizmos.DrawLine(previousPoint, point);
                previousPoint = point;
            }

            Gizmos.DrawSphere(start, 0.08f);
            Gizmos.DrawSphere(end, 0.08f);
        }
    }
}

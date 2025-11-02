// Assets/Script/SceneReplayerV2.cs
// Author: ChatGPT x Celine
// Purpose: Rebuild scene from MRUK M0 JSON and ALIGN to the current MRUK Room
// Method A: Use system Scene Anchors (MRUKAnchor.Anchor.Uuid) — scene_anchor_uuid
//
// How it works
// 1) Build anchors from JSON (use (u,v,n) plane frame for rotation; boundary_world for mesh)
// 2) Find live MRUK anchors whose UUID == recorded scene_anchor_uuid (UUID normalization included)
// 3) Compute rigid transform:  T_world = T_live * inv(T_recorded)
// 4) Apply T_world to the replay root so everything snaps to the real room
//
// Notes
// - Requires running on device with MRUK v65+ and the SAME Space (same scene model)
// - Does not require creating your own Spatial Anchors

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;
using Meta.XR.MRUtilityKit; // MRUK Room & Anchors

public class SceneReplayerV2 : MonoBehaviour
{
    [Header("Diagnostics")]
    public bool logAllAtStartup = true;     // dump recorded/live UUIDs at boot
    public bool spawnLiveMarkers = false;   // place small markers for live anchors (runtime visible)
    public int maxMarkers = 50;             // limit to avoid spam

    [Header("Input JSON (pick one)")]
    [Tooltip("Absolute or relative path under Application.persistentDataPath")]
    //public string jsonPath = "D:\\e-diploma\\Context-Aware-Generate-and-Place\\Assets\\RegionDumps\\mruk_dump_20251101_200909_e6b4db05a31e417ea033e7affdeb40e4.json";
    public string jsonPath = "RegionDumps/mruk_dump_20251101_200909_e6b4db05a31e417ea033e7affdeb40e4.json";
    [Tooltip("If set, this TextAsset is used and jsonPath is ignored")]
    public TextAsset jsonAsset;

    [Header("Build Options")]
    public bool autoLoadOnStart = true;
    public bool addMeshCollider = true;
    public float gizmoNormalLength = 0.12f;

    [Tooltip("Always use plane frame (u,v,n) to orient anchors (recommended for room-consistent frames).")]
    public bool usePlaneFrameRotation = true;

    [Header("Alignment (Method A: MRUK scene anchors)")]
    [Tooltip("After building from JSON, automatically align to the live MRUK room using scene_anchor_uuid match.")]
    public bool autoAlignToLiveRoom = true;
    [Tooltip("Label preference when picking an anchor to align (first match wins). Higher priority first.")]
    public string[] alignLabelPriority = new[] { "WALL_FACE", "DOOR_FRAME", "WINDOW_FRAME", "FLOOR", "TABLE" };

    [Header("Materials (optional)")]
    public Material defaultMat;
    public Material floorMat;
    public Material wallMat;
    public Material tableMat;
    public Material screenMat;

    // lookup for quick access
    private readonly Dictionary<string, GameObject> uuid2go = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

    // root created at runtime
    private Transform sceneRoot;

    // keep last dump for alignment
    private R_SceneDump lastDump;

    [Header("Filters")]
    [Tooltip("Skip GLOBAL_MESH anchors entirely (no render, no UUID).")]
    public bool excludeGlobalMesh = true;

    // ---------------------- Lifecycle ----------------------
    void Start()
    {
        StartCoroutine(Boot());
    }

    private System.Collections.IEnumerator Boot()
    {
        // Wait for MRUK to be ready so we don't align before room exists
        yield return WaitForMRUKReady(12f);

        if (autoLoadOnStart)
        {
            TryLoadAndBuild();
            if (logAllAtStartup)
                DebugDumpUUIDs();
            if (spawnLiveMarkers)
                SpawnLiveMarkers();
            if (autoAlignToLiveRoom)
            {
                try
                {
                    AlignToLiveRoom();
                }
                catch (Exception ex) { Debug.LogWarning($"[SceneReplayerV2] Align exception: {ex.Message}"); }
            }
        }
    }

    private System.Collections.IEnumerator WaitForMRUKReady(float timeoutSec)
    {
        float t0 = Time.realtimeSinceStartup;
        while (MRUK.Instance == null || MRUK.Instance.GetCurrentRoom() == null)
        {
            if (Time.realtimeSinceStartup - t0 > timeoutSec)
            {
                Debug.LogWarning("[SceneReplayerV2] MRUK room not ready (timeout). Proceeding without live alignment.");
                yield break;
            }
            yield return null;
        }
        var room = MRUK.Instance.GetCurrentRoom();
        Debug.Log($"[SceneReplayerV2] MRUK room ready. Live anchors: {room.Anchors?.Count ?? 0}");
    }

    // ---------------------- Build ----------------------
    [ContextMenu("Rebuild From JSON")]
    public void TryLoadAndBuild()
    {
        string json = null;
        if (jsonAsset != null)
        {
            json = jsonAsset.text;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(jsonPath))
            {
                Debug.LogWarning("[SceneReplayerV2] No jsonPath or jsonAsset provided.");
                return;
            }
            string path = jsonPath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Application.persistentDataPath, jsonPath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SceneReplayerV2] JSON file not found: {path}");
                return;
            }
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SceneReplayerV2] Read JSON failed: {ex.Message}");
                return;
            }
        }

        var dump = JsonUtility.FromJson<R_SceneDump>(json);
        if (dump == null)
        {
            Debug.LogError("[SceneReplayerV2] Failed to parse JSON.");
            return;
        }
        lastDump = dump;

        // clean previous
        if (sceneRoot != null)
        {
            DestroyImmediate(sceneRoot.gameObject);
        }
        uuid2go.Clear();

        var rootGO = new GameObject($"ReplayedScene_{Short(dump.scene_uuid)}");
        sceneRoot = rootGO.transform;

        // Build by label groups
        if (dump.anchorsByLabel != null)
        {
            foreach (var grp in dump.anchorsByLabel)
            {
                if (grp == null || grp.anchors == null || grp.anchors.Count == 0)
                    continue;

                // NEW: skip GLOBAL_MESH group
                if (excludeGlobalMesh && string.Equals(grp.label, "GLOBAL_MESH", StringComparison.OrdinalIgnoreCase))
                    continue;

                var catRoot = new GameObject(grp.label ?? "(null)").transform;
                catRoot.SetParent(sceneRoot, false);

                foreach (var a in grp.anchors)
                {
                    if (a == null)
                        continue;

                    // NEW: double guard in case label set per-anchor
                    if (excludeGlobalMesh && string.Equals(a.label, "GLOBAL_MESH", StringComparison.OrdinalIgnoreCase))
                        continue;

                    BuildOneAnchor(catRoot, grp.label, a);
                }
            }
        }

        Debug.Log($"[SceneReplayerV2] Built anchors: {uuid2go.Count} with UUIDs. \n Scene UUID: {dump.scene_uuid}");
    }

    private void BuildOneAnchor(Transform parent, string label, R_Anchor a)
    {
        string shortUuid = string.IsNullOrEmpty(a.scene_anchor_uuid) ? "" : Short(a.scene_anchor_uuid);
        var go = new GameObject(string.IsNullOrEmpty(shortUuid) ? $"{label}_{a.name}" : $"{label}_{a.name}_{shortUuid}");
        go.transform.SetParent(parent, false);

        // 1) Position
        go.transform.position = a.p0.ToV3();

        // 2) Rotation (always prefer plane frame for room-consistency)
        if (usePlaneFrameRotation)
        {
            var pose = ComputeRecordedPose(a);
            go.transform.rotation = pose.rotation;
        }
        else
        {
            if (a.rot.HasValue())
                go.transform.rotation = a.rot.ToQ();
            else
                go.transform.rotation = Quaternion.LookRotation(a.u.ToV3Safe(Vector3.right), a.n.ToV3Safe(Vector3.up));
        }

        // 3) Convert boundary from WORLD -> LOCAL ... 
        var vertsWorld = a.boundary_world != null ? a.boundary_world.Select(v => v.ToV3()).ToList() : null;
        if (vertsWorld != null && vertsWorld.Count >= 3)
        {
            var vertsLocal = new List<Vector3>(vertsWorld.Count);
            for (int i = 0; i < vertsWorld.Count; i++)
                vertsLocal.Add(go.transform.InverseTransformPoint(vertsWorld[i]));

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = PickMat(label);
            mf.sharedMesh = BuildFanMeshLocal(vertsLocal);
            if (addMeshCollider)
            {
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
            }
        }



        // store lookup by scene_anchor_uuid
        if (!string.IsNullOrEmpty(a.scene_anchor_uuid))
            uuid2go[NormalizeUuid(a.scene_anchor_uuid)] = go;

        // Optional: draw normal gizmo helper (local-space normal)
        var giz = go.AddComponent<_AnchorGizmo>();
        giz.normal = go.transform.InverseTransformDirection(a.n.ToV3Safe(Vector3.up)).normalized;
        giz.length = gizmoNormalLength;
    }


    //private bool IsGlobalMeshAnchor(R_Anchor a)
    //{
    //    if (a == null) return false;
    //    // UUID 比對
    //    if (!string.IsNullOrEmpty(globalMeshUUID) &&
    //        NormalizeUuid(a.scene_anchor_uuid) == NormalizeUuid(globalMeshUUID))
    //        return true;

    //    // 有些 dump 會用 label/來源來標示
    //    var L = (a.label ?? "").ToUpperInvariant();
    //    if (L.Contains("GLOBAL")) return true;
    //    if (!string.IsNullOrEmpty(a.boundary_source) &&
    //        a.boundary_source.ToLowerInvariant().Contains("scene_mesh"))
    //        return true;

    //    return false;
    //}


    // ---------------------- Alignment ----------------------
    [ContextMenu("Align To Live Room (UUID)")]
    public bool AlignToLiveRoom()
    {
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null || sceneRoot == null || lastDump == null)
        {
            Debug.LogWarning("[SceneReplayerV2] Missing MRUK room or scene root or dump.");
            return false;
        }

        // Build a lookup of live anchors by normalized UUID
        var liveMap = new Dictionary<string, MRUKAnchor>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in room.Anchors)
        {
            try
            {
                liveMap[NormalizeUuid(a.Anchor.Uuid.ToString())] = a;
            }
            catch { }
        }
        Debug.Log($"[SceneReplayerV2] Live anchors available: {liveMap.Count}");

        // Collect recorded anchors with UUIDs (priority first)
        var candidates = new List<R_Anchor>();
        var pri = PickRecordedAnchorByPriority(lastDump);
        if (pri != null)
            candidates.Add(pri);
        if (lastDump.anchorsByLabel != null)
        {
            foreach (var grp in lastDump.anchorsByLabel)
            {
                if (grp?.anchors == null)
                    continue;
                foreach (var a in grp.anchors)
                {
                    if (a == null || string.IsNullOrEmpty(a.scene_anchor_uuid))
                        continue;
                    if (pri != null && ReferenceEquals(a, pri))
                        continue;
                    candidates.Add(a);
                }
            }
        }

        foreach (var rec in candidates)
        {
            var key = NormalizeUuid(rec.scene_anchor_uuid);
            if (!liveMap.TryGetValue(key, out var live))
                continue;

            // recorded pose (plane-frame)
            Pose T_rec = ComputeRecordedPose(rec);

            // live pose: use the SAME plane-frame convention as exporter/JSON
            var at = TypeFromLabel(rec.label);
            GetPlaneFrameFromTransform(at, live.transform, out var ln, out var lu, out var lv);
            var R_live = Quaternion.LookRotation(lv, ln);
            var P_live = live.transform.position;
            Pose T_live = new Pose(P_live, R_live);

            // Debug normals to verify bases are consistent
            Vector3 recUp = T_rec.rotation * Vector3.up;
            Vector3 liveUp = T_live.rotation * Vector3.up;
            Debug.Log($"[SceneReplayerV2] Basis check: recUp={recUp:F3} liveUp={liveUp:F3}");

            // Before applying, reset root so we don't accumulate transforms
            sceneRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // rigid transform T_world = T_live * inv(T_rec)
            Matrix4x4 M_rec = Matrix4x4.TRS(T_rec.position, T_rec.rotation, Vector3.one);
            Matrix4x4 M_live = Matrix4x4.TRS(T_live.position, T_live.rotation, Vector3.one);
            Matrix4x4 M_world = M_live * M_rec.inverse;

            // Apply to root (from origin)
            sceneRoot.SetPositionAndRotation(
                M_world.MultiplyPoint3x4(Vector3.zero),
                M_world.rotation
            );

            Debug.Log($"[SceneReplayerV2] Aligned using '{rec.label}/{rec.name}' UUID={Short(rec.scene_anchor_uuid)}.");
            return true;
        }

        // If we get here, no UUID matched. Dump a few examples to help debug and to file.
        DebugDumpUUIDs();
        Debug.LogWarning("[SceneReplayerV2] No matching UUID found. Make sure this device is in the SAME Space used to record the JSON.");
        return false;
    }

    private R_Anchor PickRecordedAnchorByPriority(R_SceneDump dump)
    {
        if (dump?.anchorsByLabel == null)
            return null;
        // First pass: follow priority labels
        foreach (var want in alignLabelPriority)
        {
            foreach (var grp in dump.anchorsByLabel)
            {
                if (grp.label == null)
                    continue;
                if (!grp.label.Equals(want, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(grp.label, "GLOBAL_MESH", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (grp.anchors == null)
                    continue;
                var found = grp.anchors.FirstOrDefault(a => !string.IsNullOrEmpty(a.scene_anchor_uuid));
                if (found != null)
                    return found;
            }
        }
        // Fallback: any label with a UUID
        foreach (var grp in dump.anchorsByLabel)
        {
            if (grp.anchors == null)
                continue;
            var any = grp.anchors.FirstOrDefault(a => !string.IsNullOrEmpty(a.scene_anchor_uuid));
            if (any != null)
                return any;
        }
        return null;
    }

    private Pose ComputeRecordedPose(R_Anchor a)
    {
        // Use the same plane-frame rotation logic used to place the GO
        Vector3 n = a.n.ToV3Safe(Vector3.up).normalized;
        Vector3 u = a.u.ToV3Safe(Vector3.right).normalized;
        if (u.sqrMagnitude < 1e-6f)
            u = Vector3.right;
        if (n.sqrMagnitude < 1e-6f)
            n = Vector3.up;
        Vector3 v = Vector3.Cross(n, u).normalized;
        if (v.sqrMagnitude < 1e-6f)
        {
            // rebuild orthonormal basis if needed
            OrthoNormalizeRef(ref n, ref u);
            v = Vector3.Cross(n, u).normalized;
        }
        Quaternion R = Quaternion.LookRotation(v, n);
        Vector3 P = a.p0.ToV3();
        return new Pose(P, R);
    }

    private void OrthoNormalizeRef(ref Vector3 n, ref Vector3 u)
    {
        n = n.sqrMagnitude < 1e-6f ? Vector3.up : n.normalized;
        u = (u - Vector3.Dot(u, n) * n);
        if (u.sqrMagnitude < 1e-6f)
        {
            var any = Mathf.Abs(Vector3.Dot(n, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward;
            u = (any - Vector3.Dot(any, n) * n).normalized;
        }
        else
            u = u.normalized;
    }

    // ---------------------- Plane-frame helpers (match exporter) ----------------------
    private enum AnchorType
    {
        Floor, Wall, Ceiling, Horizontal, Vertical, Other, Table
    }

    private AnchorType TypeFromLabel(string label)
    {
        var L = (label ?? "").ToUpperInvariant();
        if (L.Contains("FLOOR"))
            return AnchorType.Floor;
        if (L.Contains("CEILING"))
            return AnchorType.Ceiling;
        if (L.Contains("WALL"))
            return AnchorType.Wall;
        if (L.Contains("TABLE"))
            return AnchorType.Table;
        if (L.Contains("DOOR_FRAME") || L.Contains("WINDOW_FRAME"))
            return AnchorType.Vertical;
        if (L.Contains("SCREEN") || L.Contains("BED") || L.Contains("COUCH") || L.Contains("STORAGE") || L.Contains("LAMP") || L.Contains("PLANT"))
            return AnchorType.Horizontal;
        return AnchorType.Other;
    }

    private void GetPlaneFrameFromTransform(AnchorType t, Transform tr, out Vector3 n, out Vector3 u, out Vector3 v)
    {
        // Same rules used in exporter GetPlaneFrame
        Vector3 seedN = (t == AnchorType.Wall || t == AnchorType.Vertical) ? tr.forward
                        : (t == AnchorType.Ceiling ? -tr.up : tr.up);
        if (seedN.sqrMagnitude < 1e-6f)
            seedN = Vector3.up;
        Vector3 seedU = tr.right.sqrMagnitude > 1e-6f ? tr.right : Vector3.right;

        if (t == AnchorType.Wall || t == AnchorType.Vertical)
        {
            n = seedN;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.forward;
            n.Normalize();
            u = seedU;
            Vector3.OrthoNormalize(ref n, ref u);
            v = Vector3.Cross(n, u).normalized;
            return;
        }

        n = Vector3.Dot(seedN, Vector3.up) >= 0 ? Vector3.up : Vector3.down;
        u = seedU - Vector3.Dot(seedU, n) * n;
        if (u.sqrMagnitude < 1e-6f)
        {
            var any = Mathf.Abs(Vector3.Dot(n, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward;
            u = (any - Vector3.Dot(any, n) * n).normalized;
        }
        else
            u = u.normalized;
        v = Vector3.Cross(n, u).normalized;
    }

    // ---------------------- Visual helpers & debug ----------------------
    private Material PickMat(string label)
    {
        string l = (label ?? string.Empty).ToUpperInvariant();
        if (l.Contains("FLOOR") && floorMat)
            return floorMat;
        if (l.Contains("WALL") && wallMat)
            return wallMat;
        if (l.Contains("TABLE") && tableMat)
            return tableMat;
        if (l.Contains("SCREEN") && screenMat)
            return screenMat;
        if (defaultMat)
            return defaultMat;
        // fallback lightly colored material
        var mat = new Material(Shader.Find("Standard"));
        mat.color = Color.HSVToRGB(UnityEngine.Random.value, 0.35f, 0.9f);
        return mat;
    }

    // Simple convex-fan triangulation from LOCAL vertices
    private Mesh BuildFanMeshLocal(List<Vector3> vertsLocal)
    {
        var m = new Mesh();
        if (vertsLocal == null || vertsLocal.Count < 3)
            return m;

        // Ensure clockwise winding for Unity's default front-face (optional)
        if (AreaSignedXZ(vertsLocal) < 0f)
            vertsLocal.Reverse();

        m.SetVertices(vertsLocal);
        var tris = new List<int>();
        for (int i = 1; i < vertsLocal.Count - 1; i++)
        {
            tris.Add(0);
            tris.Add(i);
            tris.Add(i + 1);
        }
        m.SetTriangles(tris, 0);

        // UVs: simple planar mapping in local XZ
        Vector3 origin = vertsLocal[0];
        var uvs = new List<Vector2>(vertsLocal.Count);
        for (int i = 0; i < vertsLocal.Count; i++)
        {
            var d = vertsLocal[i] - origin;
            uvs.Add(new Vector2(d.x, d.z));
        }
        m.SetUVs(0, uvs);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private float AreaSignedXZ(List<Vector3> verts)
    {
        float a = 0f;
        for (int i = 0, j = verts.Count - 1; i < verts.Count; j = i++)
        {
            a += verts[j].x * verts[i].z - verts[i].x * verts[j].z;
        }
        return 0.5f * a;
    }

    public GameObject FindAnchorBySceneUUID(string uuid)
    {
        string key = NormalizeUuid(uuid);
        if (!string.IsNullOrEmpty(key) && uuid2go.TryGetValue(key, out var go))
            return go;
        return null;
    }

    [ContextMenu("Debug Dump UUIDs")]
    public void DebugDumpUUIDs()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SceneReplayerV2 UUID Debug ===");
        if (lastDump != null)
        {
            sb.AppendLine($"Recorded scene_uuid: {lastDump.scene_uuid}");
            sb.AppendLine("Recorded anchors with scene_anchor_uuid:");
            int rc = 0;
            if (lastDump.anchorsByLabel != null)
            {
                foreach (var grp in lastDump.anchorsByLabel)
                {
                    if (grp?.anchors == null)
                        continue;
                    if (excludeGlobalMesh && string.Equals(grp.label, "GLOBAL_MESH", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var a in grp.anchors)
                    {
                        if (a == null || string.IsNullOrEmpty(a.scene_anchor_uuid))
                            continue;
                        sb.AppendLine($"  {grp.label}/{a.name}: {Short(a.scene_anchor_uuid)}");
                        rc++;
                    }
                }
            }
            sb.AppendLine($"Recorded UUID count: {rc}");
        }
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room != null)
        {
            sb.AppendLine($"Live anchors in room: {room.Anchors?.Count ?? 0}");
            int lc = 0;
            foreach (var la in room.Anchors)
            {
                try
                {
                    sb.AppendLine($"  {la.Label}/{la.name}: {Short(la.Anchor.Uuid.ToString())}");
                    lc++;
                }
                catch { }
                if (lc >= 60)
                    break;
            }
        }
        string path = Path.Combine(Application.persistentDataPath, "uuid_debug.txt");
        try
        {
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[SceneReplayerV2] Wrote UUID debug to: {path}");
        }
        catch (Exception ex) { Debug.LogWarning($"[SceneReplayerV2] Write debug failed: {ex.Message}"); }
        Debug.Log(sb.ToString());
    }

    [ContextMenu("Spawn Live Markers")]
    public void SpawnLiveMarkers()
    {
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room == null)
        {
            Debug.LogWarning("[SceneReplayerV2] No MRUK room for markers.");
            return;
        }
        var parent = new GameObject("LiveAnchorMarkers").transform;
        int c = 0;
        foreach (var a in room.Anchors)
        {
            if (c >= Mathf.Max(1, maxMarkers))
                break;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(parent, false);
            cube.transform.position = a.transform.position;
            cube.transform.rotation = a.transform.rotation;
            cube.transform.localScale = Vector3.one * 0.05f;
            var tm = new GameObject("id").AddComponent<TextMesh>();
            tm.text = Short(a.Anchor.Uuid.ToString());
            tm.characterSize = 0.02f;
            tm.fontSize = 64;
            tm.anchor = TextAnchor.LowerCenter;
            tm.transform.SetParent(cube.transform, false);
            tm.transform.localPosition = new Vector3(0, 0.05f, 0);
            c++;
        }
        Debug.Log($"[SceneReplayerV2] Spawned {c} live anchor markers.");
    }

    private static string Short(string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
            return "";
        string n = NormalizeUuid(uuid);
        return n.Length > 8 ? n.Substring(0, 8) : n;
    }

    private static string NormalizeUuid(string s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        var arr = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                arr.Append(char.ToLowerInvariant(c));
        }
        return arr.ToString();
    }

    // ===================== Data Models (match exporter) =====================

    [Serializable]
    public class R_SceneDump
    {
        public int version;
        public string device;
        public string persistentPath;
        public string root_dir;
        public string scene_uuid;     // export-session id
        public string captured_at_utc;
        public float floor_world_y;

        public int sceneMeshCount;
        public R_AABB sceneMeshAABB;
        public string scene_mesh_path;

        public List<R_LabelCount> labelStats;
        public List<R_AnchorGroup> anchorsByLabel;

        // convenience lists (may be null)
        public List<R_Anchor> floors, tables, walls;
        public List<R_Anchor> ceilings, screens, couches, beds, storages, lamps, plants;

        public R_CameraDump capture_camera;
        public R_OcclusionDump occlusion;

        public string sdk_version;
        public string unity_version;
    }

    [Serializable]
    public class R_AABB
    {
        public R_V3 min; public R_V3 max;
    }
    [Serializable]
    public class R_LabelCount
    {
        public string label; public int count;
    }
    [Serializable]
    public class R_AnchorGroup
    {
        public string label; public List<R_Anchor> anchors;
    }

    [Serializable]
    public class R_Anchor
    {
        public string name;
        public string label;
        public R_V3 p0;    // world pos
        public R_V4 rot;   // world rot
        public R_V3 n;     // plane normal (world)
        public R_V3 u;     // plane axis u (world)
        public R_V3 v;     // plane axis v (world)
        public string shape; // "plane"
        public R_V3 size;    // plane dims
        public float height_from_floor;
        public List<R_V3> boundary_world;
        public string boundary_source;

        // system scene anchor UUID from exporter (MRUKAnchor.Anchor.Uuid)
        public string scene_anchor_uuid;

        // Optional (not used here): your own Spatial Anchor info
        public string spatial_uuid;
        public R_V3 rel_pos;
        public R_V4 rel_rot;
        public string anchor_mode;

        // Optional semantics
        public List<R_OpenVocab> open_vocab;
        public float semantic_conf;
        public float[] mu_rgb;
        public float[] mu_feat;
    }

    [Serializable]
    public class R_OpenVocab
    {
        public string label; public float conf;
    }
    [Serializable]
    public class R_CameraDump
    {
        public string name; public R_V3 pos; public R_V4 rot; public R_CamIntrinsics K;
    }
    [Serializable]
    public class R_CamIntrinsics
    {
        public int w, h; public float fx, fy, cx, cy; public float[] dist;
    }
    [Serializable]
    public class R_OcclusionDump
    {
        public string mode; public string depth_path; public int w, h;
    }

    [Serializable]
    public struct R_V3
    {
        public float x, y, z;
        public Vector3 ToV3() => new Vector3(x, y, z);
        public Vector3 ToV3Safe(Vector3 fallback) => new Vector3(float.IsNaN(x) ? fallback.x : x, float.IsNaN(y) ? fallback.y : y, float.IsNaN(z) ? fallback.z : z);
    }
    [Serializable]
    public struct R_V4
    {
        public float x, y, z, w;
        public bool HasValue() => !(x == 0f && y == 0f && z == 0f && Math.Abs(w) < 1e-6f);
        public Quaternion ToQ() => new Quaternion(x, y, z, w);
    }

    // Small gizmo helper
    private class _AnchorGizmo : MonoBehaviour
    {
        public Vector3 normal = Vector3.up;
        public float length = 0.1f;
        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Gizmos.DrawLine(transform.position, transform.position + normal.normalized * length);
        }
    }

    // 隱藏/停用 MRUK 自己產生的房間可視與碰撞（只保留資料，供對齊用）
    public void OnMRUKSceneLoaded(Meta.XR.MRUtilityKit.MRUKRoom room)
    {
        if (room == null)
            return;
        // 1) 最乾脆：整個關掉
        room.gameObject.SetActive(false);

        // 若你只想「可視隱藏但保留啟用」，改用下面這段（擇一）：
        /*
        foreach (var r in room.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (var c in room.GetComponentsInChildren<Collider>(true)) c.enabled = false;
        */
        Debug.Log("[SceneReplayerV2] Hid MRUK live room visuals; using it only for alignment.");
    }

}

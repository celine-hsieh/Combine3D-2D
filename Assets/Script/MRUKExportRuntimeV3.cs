// Assets/Script/MRUKExportRuntimeV3_2.cs
// Purpose: Export an "M0 Conditioning Bundle" from MR Utility Kit (MRUK) with per-anchor Scene UUID
// V3.2 updates:
//  - Capture system scene anchor UUID (OVRSceneAnchor.Uuid) into AnchorDump.scene_anchor_uuid
//  - Default createSpatialAnchors = false (you can toggle on if needed)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using UnityEngine;
using Meta.XR.MRUtilityKit;   // MRUK v65+

public class MRUKExportRuntimeV3_2 : MonoBehaviour
{
    [Header("Export")]
    public string exportFolder = "RegionDumps";
    public string sceneMeshFilePrefix = "scene_"; // will write .obj
    public bool exportGlobalMeshOBJ = true;

    [Header("Occlusion (Depth Proxy)")]
    public bool exportDepthProxy = false;           // off by default; enable if you add a depth-only pass
    public int depthWidth = 640;
    public int depthHeight = 480;

    [Header("Spatial Anchors (optional)")]
    public bool createSpatialAnchors = false;       // default off (your current workflow)
    public bool useCloudAnchors = false;            // local/cloud (placeholder; integrate cloud SDK if needed)
    [Tooltip("If true, only create spatial anchors for major labels (floor/wall/table/bed/couch/door/window/storage). If false, create for ALL scene anchors.")]
    public bool filterSpatialAnchorsToMajorLabels = false;   // default false = ALL anchors
    public float saveAnchorTimeoutSec = 12f;        // per-anchor timeout
    public bool logVerbose = false;

    [Header("Trigger (either will work)")]
    public OVRInput.Button triggerA = OVRInput.Button.One;                    // Right A
    public OVRInput.Button triggerIndex = OVRInput.Button.PrimaryIndexTrigger;// Index trigger

    bool _ready;

    void Start()
    {
        Debug.Log($"[MRUK] persistentDataPath = {Application.persistentDataPath}");
        StartCoroutine(WaitForMRUK());
    }

    IEnumerator WaitForMRUK()
    {
        while (MRUK.Instance == null || MRUK.Instance.GetCurrentRoom() == null)
        {
            Debug.Log("[MRUK] waiting for room...");
            yield return new WaitForSeconds(0.5f);
        }
        _ready = true;
        Debug.Log("[MRUK] room ready. Press A or Index Trigger to export.");
    }

    void Update()
    {
        if (!_ready)
            return;
        if (OVRInput.GetDown(triggerA) || OVRInput.GetDown(triggerIndex))
            StartCoroutine(ExportNowCoroutine());
    }

    [ContextMenu("Export Now")]
    void _MenuExportNow() => StartCoroutine(ExportNowCoroutine());

    IEnumerator ExportNowCoroutine()
    {
        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[MRUK] no room.");
            yield break;
        }

        // Create export root dir
        string root = Path.Combine(Application.persistentDataPath, exportFolder);
        Directory.CreateDirectory(root);

        // Scene ID & timestamp (export session id)
        string sceneUUID = Guid.NewGuid().ToString("N");
        string capturedUTC = DateTime.UtcNow.ToString("o");

        // Floor world y estimate (for convenience)
        float floorY = EstimateFloorWorldY(room);

        // Gather anchors, label stats
        var anchors = room.Anchors;
        var labelStatsList = anchors
            .GroupBy(a => a.Label.ToString())
            .Select(g => new LabelCount { label = g.Key, count = g.Count() })
            .ToList();
        Debug.Log("[MRUK] labels = " + string.Join(", ", labelStatsList.Select(kv => $"{kv.label}:{kv.count}")));

        // Pre-pack anchors → AnchorDump map (spatial_uuid will be backfilled if we save spatial anchors)
        var anchorMap = new Dictionary<MRUKAnchor, AnchorDump>();
        var grouped = anchors.GroupBy(a => a.Label.ToString())
            .Select(g => new AnchorGroup
            {
                label = g.Key,
                anchors = g.Select(a =>
                {
                    var t = AnchorTypeFromLabel(a.Label);
                    var dump = PackAnchor(a, t, floorY);   // <-- now captures scene_anchor_uuid
                    anchorMap[a] = dump;
                    return dump;
                }).ToList()
            }).ToList();

        // Convenience groups
        var floors = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR).Select(a => anchorMap[a]).ToList();
        var tables = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.TABLE).Select(a => anchorMap[a]).ToList();
        var walls = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.WALL_FACE).Select(a => anchorMap[a]).ToList();
        var ceilings = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.CEILING).Select(a => anchorMap[a]).ToList();
        var screens = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.SCREEN).Select(a => anchorMap[a]).ToList();
        var couches = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.COUCH).Select(a => anchorMap[a]).ToList();
        var beds = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.BED).Select(a => anchorMap[a]).ToList();
        var storages = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.STORAGE).Select(a => anchorMap[a]).ToList();
        var lamps = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.LAMP).Select(a => anchorMap[a]).ToList();
        var plants = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.PLANT).Select(a => anchorMap[a]).ToList();

        // Create & save OVRSpatialAnchors (optional)
        if (createSpatialAnchors)
        {
            yield return StartCoroutine(CreateAndSaveSpatialAnchors(room, anchorMap));
        }

        // Scene Mesh (optional)
        var mfsAll = room.transform.GetComponentsInChildren<MeshFilter>(true)
            .Where(mf => mf.sharedMesh != null).ToList();
        string sceneMeshRelPath = "";
        if (exportGlobalMeshOBJ)
        {
            string objName = $"{sceneMeshFilePrefix}{sceneUUID}.obj";
            string objPath = Path.Combine(root, objName);
            WriteCombinedOBJ(objPath, mfsAll);
            sceneMeshRelPath = Path.Combine(exportFolder, objName); // relative
        }

        // Camera intrinsics/extrinsics at capture time
        var cam = Camera.main;
        var camDump = new CameraDump
        {
            name = cam != null ? cam.name : "MainCamera",
            pos = cam != null ? ToV3(cam.transform.position) : new V3(),
            rot = cam != null ? ToV4(cam.transform.rotation) : new V4 { w = 1f },
            K = (cam != null ? BuildIntrinsicsFromProjection(cam) : new CamIntrinsics { w = 0, h = 0, fx = 0, fy = 0, cx = 0, cy = 0, dist = new float[0] })
        };

        // Occlusion (depth proxy) — stub
        OcclusionDump occ = null;
        if (exportDepthProxy)
        {
            occ = new OcclusionDump
            {
                mode = "depth_proxy",
                depth_path = "", // TODO: fill after you implement depth export
                w = depthWidth,
                h = depthHeight
            };
        }

        // Compose JSON dump
        var dump = new SceneDump
        {
            version = 4,
            device = SystemInfo.deviceModel,
            persistentPath = Application.persistentDataPath,
            root_dir = Application.persistentDataPath,

            scene_uuid = sceneUUID,
            captured_at_utc = capturedUTC,
            floor_world_y = floorY,

            sceneMeshCount = mfsAll.Count,
            sceneMeshAABB = ToAABBSerializable(GetGlobalAABB(mfsAll)),
            scene_mesh_path = sceneMeshRelPath,

            labelStats = labelStatsList,
            anchorsByLabel = grouped,

            floors = floors,
            tables = tables,
            walls = walls,

            ceilings = ceilings,
            screens = screens,
            couches = couches,
            beds = beds,
            storages = storages,
            lamps = lamps,
            plants = plants,

            capture_camera = camDump,
            occlusion = occ,

            sdk_version = "unknown",
            unity_version = Application.unityVersion
        };

        // Serialize
        string jsonName = $"mruk_dump_{DateTime.Now:yyyyMMdd_HHmmss}_{sceneUUID}.json";
        string jsonPath = Path.Combine(root, jsonName);
        File.WriteAllText(jsonPath, JsonUtility.ToJson(dump, true));

        Debug.Log($"[MRUK] Exported JSON: {jsonPath}");
        if (!string.IsNullOrEmpty(sceneMeshRelPath))
            Debug.Log($"[MRUK] Exported OBJ : {Path.Combine(Application.persistentDataPath, sceneMeshRelPath)}");

        StartCoroutine(Toast($"已儲存 ✔\n{jsonPath}", 2.5f));
        OVRInput.SetControllerVibration(0.3f, 0.2f, OVRInput.Controller.RTouch);
        StartCoroutine(StopHaptics(0.15f));
    }

    // =====================================================
    // Spatial Anchor create/save & backfill UUID / relative pose
    // =====================================================
    IEnumerator CreateAndSaveSpatialAnchors(MRUKRoom room, Dictionary<MRUKAnchor, AnchorDump> anchorMap)
    {
        var saRoot = new GameObject($"SpatialAnchors_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        saRoot.transform.SetParent(room.transform, false);

        List<KeyValuePair<MRUKAnchor, AnchorDump>> targets = anchorMap
            .Where(kv => !filterSpatialAnchorsToMajorLabels || IsMajorLabel(kv.Key.Label))
            .ToList();

        int total = targets.Count;
        if (total == 0)
        {
            Debug.Log("[MRUK] No anchors selected for spatial anchors.");
            yield break;
        }

        int success = 0;

        foreach (var kv in targets)
        {
            var mruk = kv.Key;
            var dump = kv.Value;

            var go = new GameObject($"SA_{mruk.Label}_{mruk.name}");
            go.transform.SetParent(saRoot.transform, false);
            go.transform.position = mruk.transform.position;
            go.transform.rotation = mruk.transform.rotation;

            var sa = go.AddComponent<OVRSpatialAnchor>();

            // per-anchor deadline
            float deadline = Time.realtimeSinceStartup + Mathf.Max(4f, saveAnchorTimeoutSec);

            // New API: SaveAnchorAsync()
            var saveTask = sa.SaveAnchorAsync();
            while (!saveTask.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;

            bool ok = false;
            if (saveTask.IsCompleted)
            {
                try
                {
                    // TryGetResult(out T) or Result==bool fallback
                    var tryGet = saveTask.GetType().GetMethod("TryGetResult");
                    if (tryGet != null)
                    {
                        object[] args = new object[] { null };
                        bool got = (bool)tryGet.Invoke(saveTask, args);
                        if (got && args[0] != null)
                        {
                            var resObj = args[0];
                            var successProp = resObj.GetType().GetProperty("Success");
                            if (successProp != null)
                                ok = (bool)successProp.GetValue(resObj);
                        }
                    }
                    else
                    {
                        var resultProp = saveTask.GetType().GetProperty("Result");
                        if (resultProp != null && resultProp.PropertyType == typeof(bool))
                            ok = (bool)resultProp.GetValue(saveTask);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MRUK] SaveAnchorAsync parse failed: " + ex.Message);
                    ok = false;
                }
            }

            if (ok)
            {
                success++;
                dump.spatial_uuid = sa.Uuid.ToString();
                dump.rel_pos = new V3 { x = 0, y = 0, z = 0 }; // identity attach mode
                dump.rel_rot = new V4 { x = 0, y = 0, z = 0, w = 1 };
                dump.anchor_mode = "identity_attach";
                if (logVerbose)
                    Debug.Log($"[MRUK] SA saved: {dump.label}/{dump.name} -> {dump.spatial_uuid}");
            }
            else
            {
                Debug.LogWarning($"[MRUK] SA save FAILED: {dump.label}/{dump.name}");
            }
        }
        Debug.Log($"[MRUK] Spatial Anchors saved {success}/{total} (timeout per-anchor={saveAnchorTimeoutSec:F1}s)");
    }

    bool IsMajorLabel(MRUKAnchor.SceneLabels l)
    {
        return l == MRUKAnchor.SceneLabels.WALL_FACE
            || l == MRUKAnchor.SceneLabels.FLOOR
            || l == MRUKAnchor.SceneLabels.TABLE
            || l == MRUKAnchor.SceneLabels.BED
            || l == MRUKAnchor.SceneLabels.COUCH
            || l == MRUKAnchor.SceneLabels.DOOR_FRAME
            || l == MRUKAnchor.SceneLabels.WINDOW_FRAME
            || l == MRUKAnchor.SceneLabels.STORAGE;
    }

    // --------- Geometry framing ---------
    enum AnchorType
    {
        Floor, Wall, Ceiling, Horizontal, Vertical, Other, Table
    }

    AnchorType AnchorTypeFromLabel(MRUKAnchor.SceneLabels label)
    {
        switch (label)
        {
            case MRUKAnchor.SceneLabels.FLOOR:
                return AnchorType.Floor;
            case MRUKAnchor.SceneLabels.CEILING:
                return AnchorType.Ceiling;
            case MRUKAnchor.SceneLabels.WALL_FACE:
            case MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE:
            case MRUKAnchor.SceneLabels.WALL_ART:
                return AnchorType.Wall;
            case MRUKAnchor.SceneLabels.TABLE:
                return AnchorType.Table;
            case MRUKAnchor.SceneLabels.BED:
            case MRUKAnchor.SceneLabels.COUCH:
            case MRUKAnchor.SceneLabels.STORAGE:
            case MRUKAnchor.SceneLabels.SCREEN:
            case MRUKAnchor.SceneLabels.LAMP:
            case MRUKAnchor.SceneLabels.PLANT:
                return AnchorType.Horizontal;
            case MRUKAnchor.SceneLabels.DOOR_FRAME:
            case MRUKAnchor.SceneLabels.WINDOW_FRAME:
                return AnchorType.Vertical;
            default:
                return AnchorType.Other;
        }
    }

    AnchorDump PackAnchor(MRUKAnchor a, AnchorType t, float floorY)
    {
        // --- read system scene anchor UUID if present ---
        string sceneUUID = "";
        try
        {
            var ovr = a.Anchor;                // OVRAnchor
            var guid = ovr.Uuid;               // Guid
            if (guid != System.Guid.Empty)
                sceneUUID = guid.ToString("N");
        }
        catch { sceneUUID = ""; }

        GetPlaneFrame(t, a.transform, out var n, out var uAxis, out var vAxis);
        var boundary = GetWorldRectPolygonOnPlane(a, t, n, uAxis, vAxis, out var guessedSize, out string source);
        float height = a.transform.position.y - floorY;

        return new AnchorDump
        {
            name = a.name,
            label = a.Label.ToString(),

            p0 = ToV3(a.transform.position),
            rot = ToV4(a.transform.rotation),

            n = ToV3(n),
            u = ToV3(uAxis),
            v = ToV3(vAxis),

            shape = "plane",
            size = ToV3(guessedSize),

            height_from_floor = height,
            boundary_world = boundary.Select(ToV3).ToList(),
            boundary_source = source,

            // System scene anchor UUID (new in V3.2)
            scene_anchor_uuid = sceneUUID,

            // Spatial anchor backfill (if you choose to create your own)
            spatial_uuid = "",
            rel_pos = new V3 { x = 0, y = 0, z = 0 },
            rel_rot = new V4 { x = 0, y = 0, z = 0, w = 1 },
            anchor_mode = "identity_attach",

            // M0 semantics/placeholders
            open_vocab = new List<OpenVocab>(),
            semantic_conf = 1f,
            mu_rgb = new float[3] { 0f, 0f, 0f },
            mu_feat = null
        };
    }

    void GetPlaneFrame(AnchorType t, Transform tr, out Vector3 n, out Vector3 u, out Vector3 v)
    {
        // seed：牆/垂直 → forward；天花板 → -up；其他（地板/桌/床/沙發/櫃子/螢幕等）→ up
        Vector3 seedN = (t == AnchorType.Wall || t == AnchorType.Vertical) ? tr.forward
                         : (t == AnchorType.Ceiling ? -tr.up : tr.up);
        if (seedN.sqrMagnitude < 1e-6f)
            seedN = Vector3.up;

        // 先用物件自身 right 當 u，之後正交化
        Vector3 seedU = tr.right.sqrMagnitude > 1e-6f ? tr.right : Vector3.right;

        if (t == AnchorType.Wall || t == AnchorType.Vertical)
        {
            // 牆面法線水平化（投影到 XZ）
            n = seedN;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.forward;
            n = n.normalized;

            // u 取物件 right，對 n 做正交化
            u = seedU;
            Vector3 tmpN = n, tmpU = u;
            Vector3.OrthoNormalize(ref tmpN, ref tmpU);
            n = tmpN;
            u = tmpU;
            v = Vector3.Cross(n, u).normalized;
            return;
        }

        // 水平類（地板/桌面/床/沙發/櫃子/螢幕底面/一般水平）
        n = Vector3.Dot(seedN, Vector3.up) >= 0f ? Vector3.up : Vector3.down;

        // u 用物件 right 投影到水平面，並正交化
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


    List<Vector3> GetWorldRectPolygonOnPlane(MRUKAnchor a, AnchorType t, Vector3 n, Vector3 uAxis, Vector3 vAxis, out Vector3 sizeOut, out string source)
    {
        var poly = new List<Vector3>(4);
        float sizeU, sizeV;

        var box = a.GetComponent<BoxCollider>();
        if (box != null)
        {
            var center = box.transform.TransformPoint(box.center);
            if (t == AnchorType.Wall || t == AnchorType.Vertical)
            {
                sizeU = box.size.x;
                sizeV = box.size.y;
            }
            else
            {
                sizeU = box.size.x;
                sizeV = box.size.z;
            }

            float halfU = sizeU * 0.5f, halfV = sizeV * 0.5f;
            poly.Add(center + (-halfU) * uAxis + (-halfV) * vAxis);
            poly.Add(center + (+halfU) * uAxis + (-halfV) * vAxis);
            poly.Add(center + (+halfU) * uAxis + (+halfV) * vAxis);
            poly.Add(center + (-halfU) * uAxis + (+halfV) * vAxis);

            sizeOut = new Vector3(sizeU, sizeV, 0f);
            source = "box_bounds";
            return poly;
        }

        var mf = a.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var corners = BoundsCorners(mf.sharedMesh.bounds).Select(c => a.transform.TransformPoint(c)).ToList();
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            Vector3 o = a.transform.position;

            foreach (var w in corners)
            {
                Vector3 d = w - o;
                float uu = Vector3.Dot(d, uAxis);
                float vv = Vector3.Dot(d, vAxis);
                if (uu < min.x)
                    min.x = uu;
                if (uu > max.x)
                    max.x = uu;
                if (vv < min.y)
                    min.y = vv;
                if (vv > max.y)
                    max.y = vv;
            }
            poly.Add(o + min.x * uAxis + min.y * vAxis);
            poly.Add(o + max.x * uAxis + min.y * vAxis);
            poly.Add(o + max.x * uAxis + max.y * vAxis);
            poly.Add(o + min.x * uAxis + max.y * vAxis);

            sizeOut = new Vector3(max.x - min.x, max.y - min.y, 0f);
            source = "mesh_bounds";
            return poly;
        }

        // fallback
        float s = 0.2f;
        var origin = a.transform.position;
        poly.Add(origin + (-s) * uAxis + (-s) * vAxis);
        poly.Add(origin + (+s) * uAxis + (-s) * vAxis);
        poly.Add(origin + (+s) * uAxis + (+s) * vAxis);
        poly.Add(origin + (-s) * uAxis + (+s) * vAxis);
        sizeOut = new Vector3(2 * s, 2 * s, 0f);
        source = "fallback_square";
        return poly;
    }

    float EstimateFloorWorldY(MRUKRoom room)
    {
        var floors = room.Anchors.Where(x => x.Label == MRUKAnchor.SceneLabels.FLOOR).ToList();
        if (floors.Count == 0)
            return 0f;

        var ys = new List<float>();
        foreach (var f in floors)
        {
            var box = f.GetComponent<BoxCollider>();
            if (box != null)
            {
                var center = box.transform.TransformPoint(box.center);
                float halfY = box.size.y * 0.5f;
                float y = (center - f.transform.up.normalized * halfY).y;
                ys.Add(y);
                continue;
            }
            var mf = f.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                foreach (var c in BoundsCorners(mf.sharedMesh.bounds))
                    ys.Add(f.transform.TransformPoint(c).y);
                continue;
            }
            ys.Add(f.transform.position.y);
        }
        return ys.Average();
    }

    Bounds GetGlobalAABB(List<MeshFilter> mfs)
    {
        bool started = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var mf in mfs)
        {
            var mb = mf.sharedMesh.bounds;
            foreach (var c in BoundsCorners(mb))
            {
                var w = mf.transform.TransformPoint(c);
                if (!started)
                {
                    b = new Bounds(w, Vector3.zero);
                    started = true;
                }
                else
                {
                    b.Encapsulate(w);
                }
            }
        }
        return b;
    }

    IEnumerable<Vector3> BoundsCorners(Bounds bb)
    {
        Vector3 min = bb.min, max = bb.max;
        yield return new Vector3(min.x, min.y, min.z);
        yield return new Vector3(max.x, min.y, min.z);
        yield return new Vector3(max.x, min.y, max.z);
        yield return new Vector3(min.x, min.y, max.z);
        yield return new Vector3(min.x, max.y, min.z);
        yield return new Vector3(max.x, max.y, min.z);
        yield return new Vector3(max.x, max.y, max.z);
        yield return new Vector3(min.x, max.y, max.z);
    }

    // ---- OBJ Export ----
    void WriteCombinedOBJ(string path, List<MeshFilter> mfs)
    {
        var sb = new StringBuilder(1 << 20);
        sb.AppendLine("# MRUK Export OBJ");
        int vertOffset = 0;

        foreach (var mf in mfs)
        {
            var mesh = mf.sharedMesh;
            var trs = mf.transform;
            var name = mf.name.Replace(' ', '_');
            sb.AppendLine($"o {name}");

            foreach (var v in mesh.vertices)
            {
                var w = trs.TransformPoint(v);
                sb.AppendLine($"v {w.x:F6} {w.y:F6} {w.z:F6}");
            }
            // normals
            var normals = mesh.normals;
            if (normals == null || normals.Length != mesh.vertexCount)
                normals = new Vector3[mesh.vertexCount];
            foreach (var n in normals)
            {
                var wn = (n == Vector3.zero ? Vector3.up : trs.TransformDirection(n)).normalized;
                sb.AppendLine($"vn {wn.x:F6} {wn.y:F6} {wn.z:F6}");
            }
            // uvs (fill 0 0 if missing)
            var uvs = mesh.uv;
            if (uvs == null || uvs.Length != mesh.vertexCount)
                for (int i = 0; i < mesh.vertexCount; i++)
                    sb.AppendLine("vt 0 0");
            else
                foreach (var uv in uvs)
                    sb.AppendLine($"vt {uv.x:F6} {uv.y:F6}");

            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var indices = mesh.GetTriangles(sm);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = vertOffset + indices[i] + 1;
                    int b = vertOffset + indices[i + 1] + 1;
                    int c = vertOffset + indices[i + 2] + 1;
                    sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }
            }
            vertOffset += mesh.vertexCount;
        }
        File.WriteAllText(path, sb.ToString());
    }

    // ---- Camera intrinsics from Unity projection ----
    CamIntrinsics BuildIntrinsicsFromProjection(Camera cam)
    {
        var K = new CamIntrinsics();
        K.w = cam.pixelWidth;
        K.h = cam.pixelHeight;
        var P = cam.projectionMatrix;
        // Approximate mapping from Unity projection to pixel intrinsics
        K.fx = P[0, 0] * K.w * 0.5f;
        K.fy = P[1, 1] * K.h * 0.5f;
        K.cx = (1f - P[0, 2]) * K.w * 0.5f;
        K.cy = (1f - P[1, 2]) * K.h * 0.5f;
        K.dist = new float[0];
        return K;
    }

    // --------- Serialization types ---------
    [Serializable]
    public struct V3
    {
        public float x, y, z;
    }
    [Serializable]
    public struct V4
    {
        public float x, y, z, w;
    }
    V3 ToV3(Vector3 v) => new V3 { x = v.x, y = v.y, z = v.z };
    V4 ToV4(Quaternion q) => new V4 { x = q.x, y = q.y, z = q.z, w = q.w };

    [Serializable]
    public class AABB
    {
        public V3 min; public V3 max;
    }
    AABB ToAABBSerializable(Bounds b) => new AABB { min = ToV3(b.min), max = ToV3(b.max) };

    [Serializable]
    public class AnchorGroup
    {
        public string label; public List<AnchorDump> anchors;
    }
    [Serializable]
    public class LabelCount
    {
        public string label; public int count;
    }

    [Serializable]
    public class CamIntrinsics
    {
        public int w, h; public float fx, fy, cx, cy; public float[] dist;
    }
    [Serializable]
    public class CameraDump
    {
        public string name; public V3 pos; public V4 rot; public CamIntrinsics K;
    }
    [Serializable]
    public class OcclusionDump
    {
        public string mode; public string depth_path; public int w, h;
    }
    [Serializable]
    public class OpenVocab
    {
        public string label; public float conf;
    }

    [Serializable]
    public class AnchorDump
    {
        public string name;
        public string label;

        public V3 p0;    // world pos
        public V4 rot;   // world rot

        public V3 n;     // plane normal (world)
        public V3 u;     // plane axis u (world)
        public V3 v;     // plane axis v (world)

        public string shape; // "plane"
        public V3 size;      // plane: wall=width,height,0; horizontal=width,depth,0

        public float height_from_floor;
        public List<V3> boundary_world; // polygon vertices (world)
        public string boundary_source;   // box_bounds | mesh_bounds | scene_polygon | fallback_square

        // NEW: system Scene API anchor id
        public string scene_anchor_uuid; // from OVRSceneAnchor.Uuid (stable per-scene anchor id)

        // Optional: your own Spatial Anchor info (if you enable createSpatialAnchors)
        public string spatial_uuid;  // OVRSpatialAnchor UUID (may be empty if not created)
        public V3 rel_pos;           // content pose relative to anchor
        public V4 rel_rot;           // content rot relative to anchor
        public string anchor_mode;   // identity_attach | relative_pose

        // M0 semantics / stats (to be filled later by 2D×3D fusion)
        public List<OpenVocab> open_vocab;
        public float semantic_conf;
        public float[] mu_rgb;
        public float[] mu_feat;
    }

    [Serializable]
    public class SceneDump
    {
        public int version;

        public string device;
        public string persistentPath;
        public string root_dir; // compose relative paths

        public string scene_uuid;      // export-session id
        public string captured_at_utc;
        public float floor_world_y;

        public int sceneMeshCount;
        public AABB sceneMeshAABB;
        public string scene_mesh_path; // relative to root_dir

        public List<LabelCount> labelStats;
        public List<AnchorGroup> anchorsByLabel;

        public List<AnchorDump> floors, tables, walls;

        // convenience lists
        public List<AnchorDump> ceilings, screens, couches, beds, storages, lamps, plants;

        public CameraDump capture_camera; // or List<CameraDump> if multi-view
        public OcclusionDump occlusion;   // optional

        public string sdk_version;   // optional
        public string unity_version; // Application.unityVersion
    }

    // --- Simple HUD & haptics ---
    IEnumerator Toast(string message, float seconds)
    {
        var cam = Camera.main != null ? Camera.main.transform : this.transform;
        var go = new GameObject("SavedToast");
        var tm = go.AddComponent<TextMesh>();
        tm.text = message;
        tm.characterSize = 0.006f;
        tm.fontSize = 64;
        tm.anchor = TextAnchor.MiddleCenter;
        go.transform.position = cam.position + cam.forward * 0.7f;
        go.transform.rotation = Quaternion.LookRotation(cam.forward, Vector3.up);

        float t = 0f;
        Color c = tm.color;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float a = 1f - (t / seconds);
            tm.color = new Color(c.r, c.g, c.b, a);
            yield return null;
        }
        Destroy(go);
    }

    IEnumerator StopHaptics(float delay)
    {
        yield return new WaitForSeconds(delay);
        OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
    }
}

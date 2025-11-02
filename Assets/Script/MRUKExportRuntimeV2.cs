using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using UnityEngine;
using Meta.XR.MRUtilityKit;   // MRUK v65+

public class MRUKExportRuntimeV2 : MonoBehaviour
{
    [Header("Export")]
    public string exportFolder = "RegionDumps";
    public string sceneMeshFilePrefix = "scene_";        // 會寫 .obj
    public bool exportGlobalMeshOBJ = true;

    [Header("Trigger (either will work)")]
    public OVRInput.Button triggerA = OVRInput.Button.One;                // 右手 A
    public OVRInput.Button triggerIndex = OVRInput.Button.PrimaryIndexTrigger; // 食指扳機

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
            ExportNow();
    }

    [ContextMenu("Export Now")]
    void ExportNow()
    {
        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[MRUK] no room.");
            return;
        }

        string root = Path.Combine(Application.persistentDataPath, exportFolder);
        Directory.CreateDirectory(root);

        // 估地板世界 y
        float floorY = EstimateFloorWorldY(room);

        // 生成場景 ID 與時間戳
        string sceneUUID = Guid.NewGuid().ToString("N");
        string capturedUTC = DateTime.UtcNow.ToString("o");

        // ---- Anchors ----
        var anchors = room.Anchors;
        var labelStatsList = anchors
            .GroupBy(a => a.Label.ToString())
            .Select(g => new LabelCount { label = g.Key, count = g.Count() })
            .ToList();

        Debug.Log("[MRUK] labels = " + string.Join(", ", labelStatsList.Select(kv => $"{kv.label}:{kv.count}")));

        var grouped = anchors.GroupBy(a => a.Label.ToString())
                             .Select(g => new AnchorGroup
                             {
                                 label = g.Key,
                                 anchors = g.Select(a => PackAnchor(a, AnchorTypeFromLabel(a.Label), floorY)).ToList()
                             }).ToList();

        var floors = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR)
                            .Select(a => PackAnchor(a, AnchorType.Floor, floorY)).ToList();
        var tables = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.TABLE)
                            .Select(a => PackAnchor(a, AnchorType.Table, floorY)).ToList();
        var walls = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.WALL_FACE)
                            .Select(a => PackAnchor(a, AnchorType.Wall, floorY)).ToList();

        // ---- Scene Model（Scene Mesh） ----
        var mfs = room.transform.GetComponentsInChildren<MeshFilter>(true)
            .Where(mf => mf.sharedMesh != null).ToList();

        string sceneMeshPath = "";
        if (exportGlobalMeshOBJ)
        {
            string objName = $"{sceneMeshFilePrefix}{sceneUUID}.obj";
            string objPath = Path.Combine(root, objName);
            WriteCombinedOBJ(objPath, mfs);
            sceneMeshPath = objPath;
        }

        var dump = new SceneDump
        {
            version = 2,
            device = SystemInfo.deviceModel,
            persistentPath = Application.persistentDataPath,
            scene_uuid = sceneUUID,
            captured_at_utc = capturedUTC,
            floor_world_y = floorY,

            sceneMeshCount = mfs.Count,
            sceneMeshAABB = ToAABBSerializable(GetGlobalAABB(mfs)),
            scene_mesh_path = sceneMeshPath,

            labelStats = labelStatsList,
            anchorsByLabel = grouped,
            floors = floors,
            tables = tables,
            walls = walls
        };

        string jsonPath = Path.Combine(root, $"mruk_dump_{DateTime.Now:yyyyMMdd_HHmmss}_{sceneUUID}.json");
        File.WriteAllText(jsonPath, JsonUtility.ToJson(dump, true));

        Debug.Log($"[MRUK] Exported JSON: {jsonPath}");
        if (!string.IsNullOrEmpty(sceneMeshPath))
            Debug.Log($"[MRUK] Exported OBJ : {sceneMeshPath}");

        StartCoroutine(Toast($"已儲存 ✔\n{jsonPath}", 2.5f));
        OVRInput.SetControllerVibration(0.3f, 0.2f, OVRInput.Controller.RTouch);
        StartCoroutine(StopHaptics(0.15f));
    }

    // --------- 幾何設定 ---------
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
        // 穩定平面坐標系（n/u/v）
        GetPlaneFrame(t, a.transform, out var n, out var uAxis, out var vAxis);

        // 邊界用 (u,v) 平面生成矩形，保證共平面
        var boundary = GetWorldRectPolygonOnPlane(a, t, n, uAxis, vAxis, out var guessedSize);

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

            shape = (t == AnchorType.Wall || t == AnchorType.Vertical) ? "plane" :
                    (t == AnchorType.Floor || t == AnchorType.Table || t == AnchorType.Horizontal || t == AnchorType.Ceiling) ? "plane" : "plane",
            size = ToV3(guessedSize),        // w,h,thickness-ish  (對牆面是 width,height,0)

            height_from_floor = height,
            boundary_world = boundary.Select(ToV3).ToList()
        };
    }

    void GetPlaneFrame(AnchorType t, Transform tr, out Vector3 n, out Vector3 u, out Vector3 v)
    {
        Vector3 seedN;
        switch (t)
        {
            case AnchorType.Wall:
            case AnchorType.Vertical:
                seedN = tr.forward;
                break;
            case AnchorType.Ceiling:
                seedN = -tr.up;
                break;
            default:
                seedN = tr.up;
                break;
        }
        if (seedN.sqrMagnitude < 1e-6f)
            seedN = Vector3.up;

        u = tr.right.sqrMagnitude > 1e-6f ? tr.right : Vector3.right;
        n = seedN.normalized;
        Vector3 tmpN = n, tmpU = u;
        Vector3.OrthoNormalize(ref tmpN, ref tmpU);
        n = tmpN;
        u = tmpU;
        v = Vector3.Cross(n, u).normalized;

        if (t == AnchorType.Wall || t == AnchorType.Vertical)
        {
            n.y = 0f;
            n = n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.forward;
            tmpN = n;
            tmpU = u;
            Vector3.OrthoNormalize(ref tmpN, ref tmpU);
            n = tmpN;
            u = tmpU;
            v = Vector3.Cross(n, u).normalized;
        }
        if (t == AnchorType.Floor || t == AnchorType.Table || t == AnchorType.Horizontal || t == AnchorType.Ceiling)
        {
            n = Vector3.Dot(n, Vector3.up) >= 0 ? Vector3.up : Vector3.down;
            u = Vector3.right;
            v = Vector3.Cross(n, u).normalized;
        }
    }

    List<Vector3> GetWorldRectPolygonOnPlane(MRUKAnchor a, AnchorType t, Vector3 n, Vector3 uAxis, Vector3 vAxis, out Vector3 sizeOut)
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
                float u = Vector3.Dot(d, uAxis);
                float v = Vector3.Dot(d, vAxis);
                if (u < min.x)
                    min.x = u;
                if (u > max.x)
                    max.x = u;
                if (v < min.y)
                    min.y = v;
                if (v > max.y)
                    max.y = v;
            }
            poly.Add(o + min.x * uAxis + min.y * vAxis);
            poly.Add(o + max.x * uAxis + min.y * vAxis);
            poly.Add(o + max.x * uAxis + max.y * vAxis);
            poly.Add(o + min.x * uAxis + max.y * vAxis);

            sizeOut = new Vector3(max.x - min.x, max.y - min.y, 0f);
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
                    b.Encapsulate(w);
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

            // vertices
            foreach (var v in mesh.vertices)
            {
                var w = trs.TransformPoint(v);
                sb.AppendLine($"v {w.x:F6} {w.y:F6} {w.z:F6}");
            }

            // normals（可省略；這裡也寫出）
            foreach (var n in mesh.normals.Length > 0 ? mesh.normals : new Vector3[mesh.vertexCount])
            {
                var wn = (n == Vector3.zero ? Vector3.up : trs.TransformDirection(n)).normalized;
                sb.AppendLine($"vn {wn.x:F6} {wn.y:F6} {wn.z:F6}");
            }

            // UV（可省略；這裡簡單輸出 0,0 以保持 face 語法完整）
            bool hasUV = mesh.uv != null && mesh.uv.Length == mesh.vertexCount;
            if (!hasUV)
            {
                for (int i = 0; i < mesh.vertexCount; i++)
                    sb.AppendLine("vt 0 0");
            }
            else
            {
                foreach (var uv in mesh.uv)
                    sb.AppendLine($"vt {uv.x:F6} {uv.y:F6}");
            }

            // faces（以 submesh 為單位）
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var indices = mesh.GetTriangles(sm);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    // OBJ 索引從 1 開始；用 v/vt/vn
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

    // --------- 序列化 ---------
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
    public class AnchorDump
    {
        public string name;
        public string label;

        public V3 p0;
        public V4 rot;

        public V3 n;                 // plane normal (unit)
        public V3 u;                 // plane axis u (unit)
        public V3 v;                 // plane axis v (unit)

        public string shape;         // "plane"（可擴充 "box","poly"...）
        public V3 size;              // 平面：width,height,0（牆）或 width,depth,0（水平）

        public float height_from_floor;
        public List<V3> boundary_world; // 共平面矩形四點（世界座標）
    }

    [Serializable]
    public class AABB
    {
        public V3 min; public V3 max;
    }
    AABB ToAABBSerializable(Bounds b) => new AABB { min = ToV3(b.min), max = ToV3(b.max) };

    [Serializable]
    public class AnchorGroup
    {
        public string label;
        public List<AnchorDump> anchors;
    }

    [Serializable]
    public class LabelCount
    {
        public string label; public int count;
    }

    [Serializable]
    public class SceneDump
    {
        public int version;

        public string device;
        public string persistentPath;

        public string scene_uuid;
        public string captured_at_utc;
        public float floor_world_y;

        public int sceneMeshCount;
        public AABB sceneMeshAABB;
        public string scene_mesh_path;       // .obj 路徑（可空）

        public List<LabelCount> labelStats;
        public List<AnchorGroup> anchorsByLabel;

        public List<AnchorDump> floors, tables, walls;
    }

    // --- 簡易 HUD & 震動 ---
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

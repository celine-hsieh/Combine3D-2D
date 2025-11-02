// Assets/Script/MRUKExportRuntime.cs
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using Meta.XR.MRUtilityKit;   // MRUK v65+

public class MRUKExportRuntime : MonoBehaviour
{
    [Header("Export")]
    public string exportFolder = "RegionDumps";

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

    void ExportNow()
    {
        var room = MRUK.Instance.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[MRUK] no room.");
            return;
        }

        // 準備目錄 + hello.txt
        string root = Path.Combine(Application.persistentDataPath, exportFolder);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello quest");

        // ---- Anchors ----
        var anchors = room.Anchors;
        var labelStats = anchors.GroupBy(a => a.Label.ToString())
                                .ToDictionary(g => g.Key, g => g.Count());

        Debug.Log("[MRUK] labels = " + string.Join(", ", labelStats.Select(kv => $"{kv.Key}:{kv.Value}")));

        // 依 label 分組輸出（JsonUtility 不支援 Dictionary，做成列表）
        var grouped = anchors.GroupBy(a => a.Label.ToString())
                             .Select(g => new AnchorGroup
                             {
                                 label = g.Key,
                                 anchors = g.Select(a => PackAnchor(a, AnchorTypeFromLabel(a.Label))).ToList()
                             }).ToList();

        // 常用三類（保留，方便你快速取用）
        var floors = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR)
                            .Select(a => PackAnchor(a, AnchorType.Floor)).ToList();
        var tables = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.TABLE)
                            .Select(a => PackAnchor(a, AnchorType.Table)).ToList();
        var walls = anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.WALL_FACE)
                            .Select(a => PackAnchor(a, AnchorType.Wall)).ToList();

        // ---- Scene Model（Scene Mesh） ----
        var mfs = room.transform.GetComponentsInChildren<MeshFilter>(true)
            .Where(mf => mf.sharedMesh != null).ToList();

        var dump = new SceneDump
        {
            device = SystemInfo.deviceModel,
            persistentPath = Application.persistentDataPath,
            sceneMeshCount = mfs.Count,
            sceneMeshAABB = ToAABBSerializable(GetGlobalAABB(mfs)),
            labelStats = labelStats,
            anchorsByLabel = grouped,
            floors = floors,
            tables = tables,
            walls = walls
        };

        string path = Path.Combine(root, $"mruk_dump_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(path, JsonUtility.ToJson(dump, true));

        Debug.Log($"[MRUK] Exported: {path}");
        StartCoroutine(Toast($"已儲存 ✔\n{path}", 2.5f));
        OVRInput.SetControllerVibration(0.3f, 0.2f, OVRInput.Controller.RTouch);
        StartCoroutine(StopHaptics(0.15f));
    }

    // --------- 幾何打包 ---------
    enum AnchorType
    {
        Floor, Wall, Ceiling, Horizontal, Vertical, Other, Table
    }

    // 依 MRUK label 給合理的平面類型（決定法向/UV 軸）
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

    AnchorDump PackAnchor(MRUKAnchor a, AnchorType t)
    {
        // 法向：牆/垂直→forward；天花板→-up；地板/水平→up；其它→up 近似
        Vector3 n;
        switch (t)
        {
            case AnchorType.Wall:
            case AnchorType.Vertical:
                n = a.transform.forward;
                break;
            case AnchorType.Ceiling:
                n = -a.transform.up;
                break;
            default:
                n = a.transform.up;
                break;
        }
        n.Normalize();

        var boundary = GetWorldRectPolygon(a, t);
        float floorY = GetNearestFloorY(a.transform.position.y,
            MRUK.Instance.GetCurrentRoom().Anchors.Where(x => x.Label == MRUKAnchor.SceneLabels.FLOOR));
        float height = a.transform.position.y - floorY;

        return new AnchorDump
        {
            name = a.name,
            label = a.Label.ToString(),
            p0 = ToV3(a.transform.position),
            n = ToV3(n),
            height_from_floor = height,
            boundary_world = boundary.Select(ToV3).ToList()
        };
    }

    // BoxCollider 優先；否則 Mesh.bounds → 目標平面投影成矩形
    List<Vector3> GetWorldRectPolygon(MRUKAnchor a, AnchorType t)
    {
        var poly = new List<Vector3>(4);

        var box = a.GetComponent<BoxCollider>();
        if (box != null)
        {
            var center = box.transform.TransformPoint(box.center);
            GetPlaneAxes(t, a.transform, out var uAxis, out var vAxis);
            float u = box.size.x;
            float v = (t == AnchorType.Wall || t == AnchorType.Vertical) ? box.size.y : box.size.z;

            float halfU = u * 0.5f, halfV = v * 0.5f;
            poly.Add(center + (-halfU) * uAxis + (-halfV) * vAxis);
            poly.Add(center + (+halfU) * uAxis + (-halfV) * vAxis);
            poly.Add(center + (+halfU) * uAxis + (+halfV) * vAxis);
            poly.Add(center + (-halfU) * uAxis + (+halfV) * vAxis);
            return poly;
        }

        var mf = a.GetComponentInChildren<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            var corners = BoundsCorners(mf.sharedMesh.bounds).Select(c => a.transform.TransformPoint(c)).ToList();
            GetPlaneAxes(t, a.transform, out var uA, out var vA);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            Vector3 o = a.transform.position;

            foreach (var w in corners)
            {
                Vector3 d = w - o;
                float u = Vector3.Dot(d, uA);
                float v = Vector3.Dot(d, vA);
                if (u < min.x)
                    min.x = u;
                if (u > max.x)
                    max.x = u;
                if (v < min.y)
                    min.y = v;
                if (v > max.y)
                    max.y = v;
            }

            poly.Add(o + min.x * uA + min.y * vA);
            poly.Add(o + max.x * uA + min.y * vA);
            poly.Add(o + max.x * uA + max.y * vA);
            poly.Add(o + min.x * uA + max.y * vA);
            return poly;
        }

        // fallback 小矩形
        float s = 0.2f;
        GetPlaneAxes(t, a.transform, out var uB, out var vB);
        var origin = a.transform.position;
        poly.Add(origin + (-s) * uB + (-s) * vB);
        poly.Add(origin + (+s) * uB + (-s) * vB);
        poly.Add(origin + (+s) * uB + (+s) * vB);
        poly.Add(origin + (-s) * uB + (+s) * vB);
        return poly;
    }

    // 桌/地/水平→ right & forward；牆/垂直→ right & up；天花板→ right & (-forward)（維持右手系）
    void GetPlaneAxes(AnchorType t, Transform tr, out Vector3 uAxis, out Vector3 vAxis)
    {
        switch (t)
        {
            case AnchorType.Wall:
            case AnchorType.Vertical:
                uAxis = tr.right.normalized;
                vAxis = tr.up.normalized;
                break;
            case AnchorType.Ceiling:
                uAxis = tr.right.normalized;
                vAxis = (-tr.forward).normalized;
                break;
            default:
                uAxis = tr.right.normalized;
                vAxis = tr.forward.normalized;
                break;
        }
    }

    float GetNearestFloorY(float y, IEnumerable<MRUKAnchor> floors)
    {
        var ys = floors.Select(f => f.transform.position.y).ToList();
        if (ys.Count == 0)
            return 0f;
        return ys.OrderBy(v => Mathf.Abs(v - y)).First();
    }

    Bounds GetGlobalAABB(List<MeshFilter> mfs)
    {
        bool started = false;
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var mf in mfs)
        {
            var mb = mf.sharedMesh.bounds; // local
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

    // --------- 序列化 ---------
    [System.Serializable]
    public struct V3
    {
        public float x, y, z;
    }
    V3 ToV3(Vector3 v) => new V3 { x = v.x, y = v.y, z = v.z };

    [System.Serializable]
    public class AnchorDump
    {
        public string name;
        public string label;
        public V3 p0;
        public V3 n;
        public float height_from_floor;
        public List<V3> boundary_world; // 世界座標（矩形近似）
    }

    [System.Serializable]
    public class AABB
    {
        public V3 min; public V3 max;
    }
    AABB ToAABBSerializable(Bounds b) => new AABB { min = ToV3(b.min), max = ToV3(b.max) };

    [System.Serializable]
    public class AnchorGroup
    {
        public string label;
        public List<AnchorDump> anchors;
    }

    [System.Serializable]
    public class SceneDump
    {
        public string device;
        public string persistentPath;
        public int sceneMeshCount;
        public AABB sceneMeshAABB;

        public Dictionary<string, int> labelStats; // 各類別數量
        public List<AnchorGroup> anchorsByLabel;  // 依 label 分組（通用）

        // 方便取用的三類
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

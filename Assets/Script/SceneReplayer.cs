// Assets/Script/SceneReplayer.cs
// Purpose: Rebuild a lightweight scene visualization from an MRUK M0 dump JSON
//          (reads per-anchor scene_anchor_uuid from MRUKAnchor.Anchor.Uuid)
// Notes:
//  - No dependency on Spatial Anchors; only consumes scene_anchor_uuid written by exporter
//  - Builds simple planar meshes from boundary_world (fan triangulation)
//  - Creates per-label parent GameObjects and a UUID→GO lookup
//  - Safe defaults; compiles clean even if a dump omits optional fields

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;

public class SceneReplayer : MonoBehaviour
{
    [Header("Input JSON (pick one)")]
    [Tooltip("Absolute or relative path under Application.persistentDataPath")]
    public string jsonPath = "D:\\e-diploma\\Context-Aware-Generate-and-Place\\Assets\\RegionDumps\\mruk_dump_20251101_200909_e6b4db05a31e417ea033e7affdeb40e4.json";
    [Tooltip("If set, this TextAsset is used and jsonPath is ignored")]
    public TextAsset jsonAsset;

    [Header("Options")]
    public bool autoLoadOnStart = true;
    public bool addMeshCollider = true;
    public float gizmoNormalLength = 0.15f;

    [Tooltip("If true, use plane frame (u,v,n) to set rotation (recommended). If false, use provided rot quaternion.")]
    public bool usePlaneFrameRotation = true;

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

    void Start()
    {
        if (autoLoadOnStart)
        {
            TryLoadAndBuild();
        }
    }

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
                Debug.LogWarning("[SceneReplayer] No jsonPath or jsonAsset provided.");
                return;
            }
            string path = jsonPath;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Application.persistentDataPath, jsonPath);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SceneReplayer] JSON file not found: {path}");
                return;
            }
            json = File.ReadAllText(path);
        }

        var dump = JsonUtility.FromJson<R_SceneDump>(json);
        if (dump == null)
        {
            Debug.LogError("[SceneReplayer] Failed to parse JSON.");
            return;
        }

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
                var catRoot = new GameObject(grp.label ?? "(null)").transform;
                catRoot.SetParent(sceneRoot, false);

                foreach (var a in grp.anchors)
                {
                    if (a == null)
                        continue;
                    BuildOneAnchor(catRoot, grp.label, a);
                }
            }
        }

        Debug.Log($"[SceneReplayer] Built anchors: {uuid2go.Count} with UUIDs.\nScene UUID: {dump.scene_uuid}");
    }

    private void BuildOneAnchor(Transform parent, string label, R_Anchor a)
    {
        string shortUuid = string.IsNullOrEmpty(a.scene_anchor_uuid) ? "" : Short(a.scene_anchor_uuid);
        var go = new GameObject(string.IsNullOrEmpty(shortUuid) ? $"{label}_{a.name}" : $"{label}_{a.name}_{shortUuid}");
        go.transform.SetParent(parent, false);

        // 1) Position
        go.transform.position = a.p0.ToV3();

        // 2) Rotation
        if (usePlaneFrameRotation)
        {
            Vector3 n = a.n.ToV3Safe(Vector3.up).normalized;
            Vector3 u = a.u.ToV3Safe(Vector3.right).normalized;
            if (u.sqrMagnitude < 1e-6f)
                u = Vector3.right;
            if (n.sqrMagnitude < 1e-6f)
                n = Vector3.up;
            Vector3 v = Vector3.Cross(n, u).normalized; // plane forward
            if (v.sqrMagnitude < 1e-6f)
            {
                // fallback: rebuild an orthonormal basis
                (n, u) = OrthoNormalizeSafe(n, u);
                v = Vector3.Cross(n, u).normalized;
            }
            go.transform.rotation = Quaternion.LookRotation(v, n);
        }
        else
        {
            if (a.rot.HasValue())
                go.transform.rotation = a.rot.ToQ();
            else
                go.transform.rotation = Quaternion.LookRotation(a.u.ToV3Safe(Vector3.right), a.n.ToV3Safe(Vector3.up));
        }

        // 3) Convert boundary from WORLD -> LOCAL to avoid double-transform
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
            uuid2go[a.scene_anchor_uuid] = go;

        // Optional: draw normal gizmo helper (local-space normal)
        var giz = go.AddComponent<_AnchorGizmo>();
        giz.normal = go.transform.InverseTransformDirection(a.n.ToV3Safe(Vector3.up)).normalized;
        giz.length = gizmoNormalLength;
    }

    private (Vector3 n, Vector3 u) OrthoNormalizeSafe(Vector3 n, Vector3 u)
    {
        n = n.sqrMagnitude < 1e-6f ? Vector3.up : n.normalized;
        // remove n-component from u, then normalize
        u = (u - Vector3.Dot(u, n) * n);
        if (u.sqrMagnitude < 1e-6f)
        {
            // pick any axis not parallel to n
            u = Mathf.Abs(Vector3.Dot(n, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward;
            u = (u - Vector3.Dot(u, n) * n).normalized;
        }
        else
            u = u.normalized;
        return (n, u);
    }

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
        if (!string.IsNullOrEmpty(uuid) && uuid2go.TryGetValue(uuid, out var go))
            return go;
        return null;
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

    private static string Short(string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
            return "";
        return uuid.Length > 8 ? uuid.Substring(0, 8) : uuid;
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
}

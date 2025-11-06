#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneDiffWindow : EditorWindow
{
    [SerializeField] private SceneAsset sceneAsset;
    [SerializeField] private string searchFilter = "";
    [SerializeField] private bool showAdded = true, showRemoved = true, showModified = true;
    [SerializeField] private Vector2 scroll;
    [SerializeField] private DiffResult lastDiff;

    // Tracked objects filter
    [SerializeField] private List<string> trackedPaths = new List<string>(); // name[siblingIndex]/... path
    [SerializeField] private bool includeChildrenOfTracked = true;
    [SerializeField] private Vector2 trackedScroll;

    private const string BASELINE_DIR = "Assets/SceneDiff/Baselines";

    [MenuItem("Window/Scene Diff")]
    public static void ShowWindow()
    {
        var win = GetWindow<SceneDiffWindow>("Scene Diff");
        win.minSize = new Vector2(720, 560);
        win.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scene Snapshot & Diff (Git-like)", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            sceneAsset = (SceneAsset)EditorGUILayout.ObjectField("Scene Asset", sceneAsset, typeof(SceneAsset), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = sceneAsset != null;
                if (GUILayout.Button("Save State", GUILayout.Height(26))) TrySaveBaseline();
                if (GUILayout.Button("Compare", GUILayout.Height(26))) TryCompare();
                GUI.enabled = true;
                if (GUILayout.Button("Open Baseline Folder", GUILayout.Height(26)))
                {
                    EnsureBaselineDir();
                    EditorUtility.RevealInFinder(BASELINE_DIR);
                }
                if (GUILayout.Button("Rescan Baselines", GUILayout.Height(26)))
                {
                    AssetDatabase.Refresh();
                    ShowNotification(new GUIContent("Baselines rescanned."));
                }
            }
        }

        DrawTrackedObjectsBlock();

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Filters", EditorStyles.boldLabel);
            searchFilter = EditorGUILayout.TextField("Search", searchFilter);
            using (new EditorGUILayout.HorizontalScope())
            {
                showAdded = GUILayout.Toggle(showAdded, "Added", "Button");
                showRemoved = GUILayout.Toggle(showRemoved, "Removed", "Button");
                showModified = GUILayout.Toggle(showModified, "Modified", "Button");
            }
        }

        EditorGUILayout.Space();
        DrawDiffResults();
    }

    // ========================= Tracked Objects UI =========================
    private void DrawTrackedObjectsBlock()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField("Tracked Objects (Drag from Hierarchy)", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                includeChildrenOfTracked = EditorGUILayout.ToggleLeft("Include children", includeChildrenOfTracked, GUILayout.Width(140));

                if (GUILayout.Button("Add Selection", GUILayout.Height(22)))
                    AddCurrentSelectionToTracked();

                if (GUILayout.Button("Clear All", GUILayout.Height(22)))
                    trackedPaths.Clear();
            }

            var dropRect = GUILayoutUtility.GetRect(0, 99999, 64, 64, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, GUIContent.none);
            var inner = dropRect; inner.xMin += 6; inner.yMin += 6; inner.width -= 12; inner.height -= 12;

            using (var sv = new GUI.ScrollViewScope(inner, trackedScroll, new Rect(0, 0, inner.width - 16, Mathf.Max(64, trackedPaths.Count * 22 + 4))))
            {
                trackedScroll = sv.scrollPosition;

                if (trackedPaths.Count == 0)
                {
                    GUI.Label(new Rect(4, 4, inner.width - 16, 20), "Drop GameObjects here…", EditorStyles.wordWrappedMiniLabel);
                }
                else
                {
                    int y = 4;
                    for (int i = 0; i < trackedPaths.Count; i++)
                    {
                        var path = trackedPaths[i];

                        // Click label to ping/select
                        var labelRect = new Rect(6, y, inner.width - 60, 18);
                        if (GUI.Button(labelRect, path, EditorStyles.label))
                            TryPingAndSelect(path);

                        // Per-item remove (✕)
                        var xRect = new Rect(inner.width - 48, y, 40, 18);
                        if (GUI.Button(xRect, "✕"))
                        {
                            trackedPaths.RemoveAt(i);
                            GUI.FocusControl(null);
                            Repaint();
                            break;
                        }

                        y += 20;
                    }
                }
            }

            HandleTrackedDragAndDrop(dropRect);
        }
    }

    private void HandleTrackedDragAndDrop(Rect dropRect)
    {
        var evt = Event.current;
        if (!dropRect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            bool anyGo = DragAndDrop.objectReferences.Any(o => o is GameObject);
            if (anyGo)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                        if (obj is GameObject go) TryAddTracked(go);
                    Event.current.Use();
                }
                else
                {
                    Event.current.Use();
                }
            }
        }
    }

    private void AddCurrentSelectionToTracked()
    {
        foreach (var tr in Selection.transforms)
            TryAddTracked(tr.gameObject);
    }

    private void TryAddTracked(GameObject go)
    {
        if (sceneAsset == null) return;

        var targetScenePath = GetScenePath(sceneAsset);
        if (go.scene.path != targetScenePath)
        {
            ShowNotification(new GUIContent("Object is not in the selected scene"));
            return;
        }

        var path = GetHierarchyPathWithIndex(go.transform);
        if (!trackedPaths.Contains(path))
            trackedPaths.Add(path);
    }

    // ========================= Diff Results UI =========================
    private void DrawDiffResults()
    {
        if (lastDiff == null)
        {
            EditorGUILayout.HelpBox("No diff yet. Click ‘Compare’ after saving a baseline.", MessageType.Info);
            return;
        }

        using (var scrollScope = new EditorGUILayout.ScrollViewScope(scroll))
        {
            scroll = scrollScope.scrollPosition;

            EditorGUILayout.LabelField("Results", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Added: {lastDiff.Added.Count}   Removed: {lastDiff.Removed.Count}   Modified: {lastDiff.Modified.Count}");

            DrawSection("Added", showAdded, lastDiff.Added, new Color(0.70f, 1f, 0.70f));
            DrawSection("Removed", showRemoved, lastDiff.Removed, new Color(1f, 0.70f, 0.70f));
            DrawModifiedSection();
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            var scenePath = sceneAsset ? GetScenePath(sceneAsset) : null;
            GUI.enabled = sceneAsset != null && BaselineAsset.ExistsForScene(scenePath);
            if (GUILayout.Button("Overwrite Baseline", GUILayout.Height(24))) TrySaveBaseline(true);
            GUI.enabled = true;
            if (GUILayout.Button("Clear Results", GUILayout.Height(24))) lastDiff = null;
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = lastDiff != null;
            if (GUILayout.Button("Export JSON", GUILayout.Height(24))) ExportDiff("json");
            if (GUILayout.Button("Export Markdown", GUILayout.Height(24))) ExportDiff("md");
            GUI.enabled = true;
        }
    }

    private void DrawSection(string sectionTitle, bool visible, List<ChangeItem> items, Color tint)
    {
        if (!visible) return;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField(sectionTitle, EditorStyles.boldLabel);

        var filtered = items.Where(ItemPassesAllFilters).ToList();
        if (filtered.Count == 0)
        {
            EditorGUILayout.LabelField("— none —");
            return;
        }

        foreach (var item in filtered)
            DrawChangeItem(item, tint);
    }

    private void DrawModifiedSection()
    {
        if (!showModified) return;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Modified", EditorStyles.boldLabel);

        var filtered = lastDiff.Modified.Where(ModifiedPassesAllFilters).ToList();
        if (filtered.Count == 0)
        {
            EditorGUILayout.LabelField("— none —");
            return;
        }

        foreach (var mod in filtered)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                var content = new GUIContent(mod.GameObjectPath);
                var oldColor = GUI.color;
                GUI.color = new Color(1f, 1f, 0.6f);
                if (GUILayout.Button(content, EditorStyles.boldLabel))
                    TryPingAndSelect(mod.GameObjectPath);
                GUI.color = oldColor;

                EditorGUILayout.LabelField($"{mod.ComponentType} · {mod.PropertyPath}");
                EditorGUILayout.LabelField($"Before: {mod.Before ?? "null"}".Replace(" ","")); // keep compact
                EditorGUILayout.LabelField($"After:  {mod.After ?? "null"}");
            }
        }
    }

    private bool ItemPassesAllFilters(ChangeItem i)
    {
        if (!PassesSearch(i.GameObjectPath, i.ComponentType, i.PropertyPath)) return false;
        if (trackedPaths.Count == 0) return true;
        return MatchesTracked(i.GameObjectPath);
    }

    private bool ModifiedPassesAllFilters(ModifiedItem i)
    {
        if (!PassesSearch(i.GameObjectPath, i.ComponentType, i.PropertyPath)) return false;
        if (trackedPaths.Count == 0) return true;
        return MatchesTracked(i.GameObjectPath);
    }

    private bool PassesSearch(string path, string compType, string prop)
    {
        if (string.IsNullOrEmpty(searchFilter)) return true;
        var f = searchFilter;
        bool ok = false;
        if (!string.IsNullOrEmpty(path) && path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ok = true;
        if (!ok && !string.IsNullOrEmpty(compType) && compType.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ok = true;
        if (!ok && !string.IsNullOrEmpty(prop) && prop.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ok = true;
        return ok;
    }

    private bool MatchesTracked(string itemPath)
    {
        foreach (var t in trackedPaths)
        {
            if (includeChildrenOfTracked)
            {
                if (itemPath == t || itemPath.StartsWith(t + "/", StringComparison.Ordinal)) return true;
            }
            else
            {
                if (itemPath == t) return true;
            }
        }
        return false;
    }

    private void DrawChangeItem(ChangeItem item, Color tint)
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            var content = new GUIContent(item.GameObjectPath);
            var oldColor = GUI.color;
            GUI.color = tint;
            if (GUILayout.Button(content, EditorStyles.boldLabel))
                TryPingAndSelect(item.GameObjectPath);
            GUI.color = oldColor;

            if (!string.IsNullOrEmpty(item.ComponentType) && item.ComponentType != "(GameObject)")
                EditorGUILayout.LabelField(item.ComponentType);

            if (!string.IsNullOrEmpty(item.PropertyPath))
                EditorGUILayout.LabelField($"Property: {item.PropertyPath}");
        }
    }

    private void TryPingAndSelect(string goPath)
    {
        if (sceneAsset == null) return;
        var scene = SceneManager.GetSceneByPath(GetScenePath(sceneAsset));
        if (!scene.IsValid() || !scene.isLoaded)
            return;

        var target = FindByHierarchyPath(scene, goPath);
        if (target != null)
        {
            EditorGUIUtility.PingObject(target);
            Selection.activeGameObject = target;
        }
    }

    private static GameObject FindByHierarchyPath(Scene scene, string path)
    {
        var segments = path.Split('/');
        if (segments.Length == 0) return null;

        GameObject current = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (NameWithIndex(root.transform) == segments[0])
            {
                current = root;
                break;
            }
        }
        if (current == null) return null;

        for (int i = 1; i < segments.Length; i++)
        {
            var t = current.transform;
            Transform next = null;
            for (int c = 0; c < t.childCount; c++)
            {
                var child = t.GetChild(c);
                if (NameWithIndex(child) == segments[i])
                {
                    next = child;
                    break;
                }
            }
            if (next == null) return null;
            current = next.gameObject;
        }
        return current;
    }

    private static string GetScenePath(SceneAsset sa) => AssetDatabase.GetAssetPath(sa);

    private static void EnsureBaselineDir()
    {
        if (!AssetDatabase.IsValidFolder("Assets/SceneDiff"))
            AssetDatabase.CreateFolder("Assets", "SceneDiff");
        if (!AssetDatabase.IsValidFolder(BASELINE_DIR))
            AssetDatabase.CreateFolder("Assets/SceneDiff", "Baselines");
    }

    // Name with [siblingIndex] for stable hierarchy addressing
    private static string NameWithIndex(Transform t) => $"{t.name}[{t.GetSiblingIndex()}]";
    private static string GetHierarchyPathWithIndex(Transform t)
    {
        var stack = new Stack<string>();
        while (t != null)
        {
            stack.Push(NameWithIndex(t));
            t = t.parent;
        }
        return string.Join("/", stack);
    }

    // ========================= Save / Compare / Export =========================

    private void TrySaveBaseline(bool overwrite = false)
    {
        if (sceneAsset == null) return;
        var scenePath = GetScenePath(sceneAsset);
        if (string.IsNullOrEmpty(scenePath))
        {
            EditorUtility.DisplayDialog("Scene Diff", "Invalid scene asset.", "OK");
            return;
        }

        EnsureBaselineDir();

        try
        {
            var snap = SnapshotBuilder.BuildForScenePath(scenePath);
            var json = JsonUtility.ToJson(snap, false);

            var existing = BaselineAsset.LoadOrFindForScene(scenePath);
            if (existing != null && !overwrite)
            {
                if (!EditorUtility.DisplayDialog("Overwrite baseline?", "A baseline already exists for this scene. Overwrite it?", "Overwrite", "Cancel"))
                    return;
            }

            var asset = existing ?? ScriptableObject.CreateInstance<BaselineAsset>();
            asset.ScenePath = scenePath;
            asset.SceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
            asset.SnapshotJson = json;
            asset.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var savePath = BaselineAsset.GetAssetPathForScene(scenePath);
            if (existing == null) AssetDatabase.CreateAsset(asset, savePath);

            // sidecar for future-proof recovery
            SceneDiffStorageUtil.WriteSidecarJson(savePath, asset);

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Scene Diff", "Baseline saved.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Scene Diff", "Failed to save baseline:\n" + ex.Message, "OK");
        }
    }

    private void TryCompare()
    {
        if (sceneAsset == null) return;

        var scenePath = GetScenePath(sceneAsset);
        var baseline = BaselineAsset.LoadOrFindForScene(scenePath);
        if (baseline == null)
        {
            AssetDatabase.Refresh();
            baseline = BaselineAsset.LoadOrFindForScene(scenePath);
        }

        if (baseline == null)
        {
            EditorUtility.DisplayDialog("Scene Diff",
                "No baseline saved for this scene yet. (Tip: click ‘Save State’. If you already have a .asset in the Baselines folder, click ‘Rescan Baselines’ and try again.)",
                "OK");
            return;
        }

        try
        {
            var current = SnapshotBuilder.BuildForScenePath(scenePath);
            var baseSnap = JsonUtility.FromJson<SceneSnapshot>(baseline.SnapshotJson);
            lastDiff = DiffEngine.Diff(baseSnap, current);
            Repaint();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Scene Diff", $"Compare failed:\n{ex.Message}", "OK");
        }
    }

    private void ExportDiff(string format)
    {
        if (lastDiff == null) return;
        var sceneName = sceneAsset ? sceneAsset.name : "Scene";
        var outPath = EditorUtility.SaveFilePanel($"Export Diff ({format})", "", $"{sceneName}_diff.{format}", format);
        if (string.IsNullOrEmpty(outPath)) return;

        try
        {
            if (format == "json") File.WriteAllText(outPath, JsonUtility.ToJson(lastDiff, true));
            else File.WriteAllText(outPath, DiffToMarkdown(lastDiff, sceneName));
            EditorUtility.RevealInFinder(outPath);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Scene Diff", "Export failed:\n" + ex.Message, "OK");
        }
    }

    private static string DiffToMarkdown(DiffResult diff, string sceneName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Scene Diff — {sceneName}");
        sb.AppendLine();
        sb.AppendLine($"**Added:** {diff.Added.Count} | **Removed:** {diff.Removed.Count} | **Modified:** {diff.Modified.Count}");
        sb.AppendLine();

        void Section(string t, IEnumerable<string> lines)
        {
            sb.AppendLine($"## {t}");
            foreach (var l in lines) sb.AppendLine($"- {l}");
            sb.AppendLine();
        }

        Section("Added", diff.Added.Select(a => $"{a.GameObjectPath} ({a.ComponentType}){(a.PropertyPath != null ? $" :: {a.PropertyPath}" : "")}"));
        Section("Removed", diff.Removed.Select(a => $"{a.GameObjectPath} ({a.ComponentType}){(a.PropertyPath != null ? $" :: {a.PropertyPath}" : "")}"));
        Section("Modified", diff.Modified.Select(m => $"{m.GameObjectPath} ({m.ComponentType}) {m.PropertyPath}: `{m.Before}` → `{m.After}`"));

        return sb.ToString();
    }
}

/* ---------- Shared storage/recovery helpers (public so other classes can call) ---------- */
internal static class SceneDiffStorageUtil
{
    public static void WriteSidecarJson(string assetPath, BaselineAsset asset)
    {
        try
        {
            var jsonPath = Path.ChangeExtension(assetPath, ".json");
            var abs = ToAbsolutePath(jsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            var sidecar = new BaselineSidecar
            {
                SceneGuid = asset.SceneGuid,
                ScenePath = asset.ScenePath,
                SnapshotJson = asset.SnapshotJson,
                Timestamp = asset.Timestamp
            };
            File.WriteAllText(abs, JsonUtility.ToJson(sidecar, true));
        }
        catch { /* best effort */ }
    }

    public static BaselineAsset TryReadSidecarJson(string assetPath)
    {
        try
        {
            var jsonPath = Path.ChangeExtension(assetPath, ".json");
            var abs = ToAbsolutePath(jsonPath);
            if (!File.Exists(abs)) return null;
            var txt = File.ReadAllText(abs);
            var sc = JsonUtility.FromJson<BaselineSidecar>(txt);
            if (sc == null || string.IsNullOrEmpty(sc.SnapshotJson)) return null;

            var a = ScriptableObject.CreateInstance<BaselineAsset>();
            a.SceneGuid = sc.SceneGuid;
            a.ScenePath = sc.ScenePath;
            a.SnapshotJson = sc.SnapshotJson;
            a.Timestamp = sc.Timestamp;
            return a;
        }
        catch { return null; }
    }

    public static string ToAbsolutePath(string assetPath)
    {
        var root = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
        return Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar));
    }
}

[Serializable] internal class BaselineSidecar { public string SceneGuid; public string ScenePath; public string SnapshotJson; public string Timestamp; }

#region Snapshot Data & Storage

[Serializable]
public class SceneSnapshot
{
    public string ScenePath;
    public List<GameObjectSnapshot> GameObjects = new List<GameObjectSnapshot>();
}

[Serializable]
public class GameObjectSnapshot
{
    public string Path;
    public List<ComponentSnapshot> Components = new List<ComponentSnapshot>();
}

[Serializable]
public class ComponentSnapshot
{
    public string Type;
    public int Order;
    public List<PropertyKV> Properties = new List<PropertyKV>();
}

[Serializable]
public class PropertyKV
{
    public string PropertyPath;
    public string Value;
}

public class BaselineAsset : ScriptableObject
{
    public string SceneGuid;
    public string ScenePath;
    [TextArea(3, 10)] public string SnapshotJson;
    public string Timestamp;

    public static string GetAssetPathForScene(string scenePath)
    {
        var guid = AssetDatabase.AssetPathToGUID(scenePath);
        return $"Assets/SceneDiff/Baselines/{guid}_baseline.asset";
    }

    public static BaselineAsset LoadForScene(string scenePath)
    {
        var path = GetAssetPathForScene(scenePath);
        return AssetDatabase.LoadAssetAtPath<BaselineAsset>(path);
    }

    /// Robust loader:
    /// 1) direct .asset load
    /// 2) sidecar JSON
    /// 3) scan folder for t:BaselineAsset matches by SceneGuid/ScenePath
    /// 4) parse YAML of expected .asset to recover data (handles Missing Script cases)
    public static BaselineAsset LoadOrFindForScene(string scenePath)
    {
        var path = GetAssetPathForScene(scenePath);

        // 1) Direct load
        var direct = AssetDatabase.LoadAssetAtPath<BaselineAsset>(path);
        if (direct != null) return direct;

        // 2) Sidecar
        var fromSidecar = SceneDiffStorageUtil.TryReadSidecarJson(path);
        if (fromSidecar != null) return fromSidecar;

        // 3) Scan folder
        var sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
        if (AssetDatabase.IsValidFolder("Assets/SceneDiff/Baselines"))
        {
            var guids = AssetDatabase.FindAssets("t:BaselineAsset", new[] { "Assets/SceneDiff/Baselines" });
            foreach (var g in guids)
            {
                var ap = AssetDatabase.GUIDToAssetPath(g);
                var a = AssetDatabase.LoadAssetAtPath<BaselineAsset>(ap);
                if (a != null && (a.SceneGuid == sceneGuid || a.ScenePath == scenePath))
                    return a;
            }
        }

        // 4) YAML recovery
        var abs = SceneDiffStorageUtil.ToAbsolutePath(path);
        if (File.Exists(abs))
        {
            var txt = File.ReadAllText(abs);
            var recovered = TryRecoverFromYaml(txt);
            if (recovered != null) return recovered;
        }

        return null;
    }

    public static bool ExistsForScene(string scenePath) => LoadOrFindForScene(scenePath) != null;

    private static BaselineAsset TryRecoverFromYaml(string yaml)
    {
        try
        {
            string sceneGuid = MatchScalar(yaml, @"SceneGuid:\s*(.+)");
            string scenePath = MatchScalar(yaml, @"ScenePath:\s*(.+)");

            string snapshot = null;

            var m1 = Regex.Match(yaml, @"SnapshotJson:\s*""(?<json>.*?)""\s*(\r?\n|$)", RegexOptions.Singleline);
            if (m1.Success) snapshot = Regex.Unescape(m1.Groups["json"].Value);

            if (string.IsNullOrEmpty(snapshot))
            {
                var m2 = Regex.Match(yaml, @"SnapshotJson:\s*\|\s*\r?\n(?<block>(\s{2,}.+\r?\n)+)", RegexOptions.Singleline);
                if (m2.Success)
                {
                    var block = m2.Groups["block"].Value;
                    var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(l => l.TrimStart());
                    snapshot = string.Join("\n", lines);
                }
            }

            if (string.IsNullOrEmpty(snapshot)) return null;

            var a = ScriptableObject.CreateInstance<BaselineAsset>();
            a.SceneGuid = sceneGuid?.Trim();
            a.ScenePath = scenePath?.Trim();
            a.SnapshotJson = snapshot;
            a.Timestamp = MatchScalar(yaml, @"Timestamp:\s*(.+)")?.Trim();
            return a;
        }
        catch { return null; }
    }

    private static string MatchScalar(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}

#endregion

#region Snapshot Builder

public static class SnapshotBuilder
{
    public static SceneSnapshot BuildForScenePath(string scenePath)
    {
        var alreadyLoaded = Enumerable.Range(0, SceneManager.sceneCount)
            .Select(SceneManager.GetSceneAt)
            .FirstOrDefault(s => s.path == scenePath);

        bool openedTemp = false;
        Scene sceneToScan;

        if (alreadyLoaded.IsValid() && alreadyLoaded.isLoaded)
        {
            sceneToScan = alreadyLoaded;
        }
        else
        {
            var opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            sceneToScan = opened;
            openedTemp = true;
        }

        try
        {
            return Build(sceneToScan);
        }
        finally
        {
            if (openedTemp && sceneToScan.IsValid())
                EditorSceneManager.CloseScene(sceneToScan, removeScene: true);
        }
    }

    private static SceneSnapshot Build(Scene scene)
    {
        var snap = new SceneSnapshot { ScenePath = scene.path };
        var roots = scene.GetRootGameObjects();

        foreach (var root in roots)
            WalkGO(root.transform, null, 0, snap);

        return snap;
    }

    private static void WalkGO(Transform t, string parentPath, int siblingIndexUnused, SceneSnapshot snap)
    {
        var go = t.gameObject;
        var thisPath = string.IsNullOrEmpty(parentPath)
            ? $"{go.name}[{t.GetSiblingIndex()}]"
            : $"{parentPath}/{go.name}[{t.GetSiblingIndex()}]";

        var goSnap = new GameObjectSnapshot { Path = thisPath };

        var comps = go.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null) continue;
            var compSnap = new ComponentSnapshot { Type = c.GetType().FullName, Order = i };

            try
            {
                var so = new SerializedObject(c);
                var it = so.GetIterator();

                var enterChildren = true;
                while (it.Next(enterChildren))
                {
                    enterChildren = false;
                    if (!it.editable) continue;
                    if (it.propertyPath == "m_Script") continue;
                    if (IsVolatileProperty(it)) continue;

                    compSnap.Properties.Add(new PropertyKV
                    {
                        PropertyPath = it.propertyPath,
                        Value = ReadPropertyAsStableString(it)
                    });
                }
            }
            catch (Exception ex)
            {
                compSnap.Properties.Add(new PropertyKV { PropertyPath = "_error", Value = ex.GetType().Name + ":" + ex.Message });
            }

            goSnap.Components.Add(compSnap);
        }

        snap.GameObjects.Add(goSnap);

        for (int i = 0; i < t.childCount; i++)
            WalkGO(t.GetChild(i), thisPath, i, snap);
    }

    private static bool IsVolatileProperty(SerializedProperty p)
    {
        string path = p.propertyPath;
        if (path.EndsWith(".m_LocalIdentfierInFile", StringComparison.Ordinal)) return true;
        if (path.EndsWith(".m_FileID", StringComparison.Ordinal)) return true;
        if (path == "m_CorrespondingSourceObject") return true;
        if (path == "m_PrefabInstance" || path == "m_PrefabAsset") return true;
        if (path == "m_InstanceID") return true;
        if (path == "m_LocalGUID") return true;
        if (path == "m_ObjectHideFlags") return true;
        return false;
    }

    private static string ReadPropertyAsStableString(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
            case SerializedPropertyType.Float: return p.floatValue.ToString("R");
            case SerializedPropertyType.String: return p.stringValue ?? "";
            case SerializedPropertyType.Color:
                { var c = p.colorValue; return $"({c.r:R},{c.g:R},{c.b:R},{c.a:R})"; }
            case SerializedPropertyType.ObjectReference: return ObjectReferenceToStableString(p.objectReferenceValue);
            case SerializedPropertyType.LayerMask: return p.intValue.ToString();
            case SerializedPropertyType.Enum: return p.enumDisplayNames != null && p.enumValueIndex >= 0 ? p.enumDisplayNames[p.enumValueIndex] : p.enumValueIndex.ToString();
            case SerializedPropertyType.Vector2:
                { var v = p.vector2Value; return $"({v.x:F6},{v.y:F6})"; }
            case SerializedPropertyType.Vector3:
                { var v = p.vector3Value; return $"({v.x:F6},{v.y:F6},{v.z:F6})"; }
            case SerializedPropertyType.Vector4:
                { var v = p.vector4Value; return $"({v.x:F6},{v.y:F6},{v.z:F6},{v.w:F6})"; }
            case SerializedPropertyType.Rect:
                { var r = p.rectValue; return $"({r.x:F6},{r.y:F6},{r.width:F6},{r.height:F6})"; }
            case SerializedPropertyType.Character: return p.intValue.ToString();
            case SerializedPropertyType.AnimationCurve: return CurveToString(p.animationCurveValue);
            case SerializedPropertyType.Bounds:
                { var b = p.boundsValue; return $"center({b.center.x:F6},{b.center.y:F6},{b.center.z:F6}) size({b.size.x:F6},{b.size.y:F6},{b.size.z:F6})"; }
            case SerializedPropertyType.Quaternion:
                { var e = p.quaternionValue.eulerAngles; return $"euler({e.x:F6},{e.y:F6},{e.z:F6})"; }
#if UNITY_2021_2_OR_NEWER
            case SerializedPropertyType.Vector2Int:
                { var v = p.vector2IntValue; return $"({v.x},{v.y})"; }
            case SerializedPropertyType.Vector3Int:
                { var v = p.vector3IntValue; return $"({v.x},{v.y},{v.z})"; }
            case SerializedPropertyType.RectInt:
                { var r = p.rectIntValue; return $"({r.x},{r.y},{r.width},{r.height})"; }
            case SerializedPropertyType.BoundsInt:
                { var b = p.boundsIntValue; return $"center({b.position.x},{b.position.y},{b.position.z}) size({b.size.x},{b.size.y},{b.size.z})"; }
#endif
            case SerializedPropertyType.Generic:
            default: return "";
        }
    }

    private static string ObjectReferenceToStableString(UnityEngine.Object obj)
    {
        if (obj == null) return "null";
        var path = AssetDatabase.GetAssetPath(obj);
        if (!string.IsNullOrEmpty(path))
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            long localId;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out localId);
            return $"asset:{guid}:{localId}";
        }
        if (obj is Component c) return $"sceneComp:{c.GetType().FullName}@{GetTransformPath(c.transform)}";
        if (obj is GameObject go) return $"sceneGO:{GetTransformPath(go.transform)}";
        return $"sceneObj:{obj.name}:{obj.GetType().FullName}";
    }

    private static string GetTransformPath(Transform t)
    {
        var stack = new Stack<string>();
        while (t != null)
        {
            stack.Push($"{t.name}[{t.GetSiblingIndex()}]");
            t = t.parent;
        }
        return string.Join("/", stack);
    }

    private static string CurveToString(AnimationCurve curve)
    {
        if (curve == null) return "null";
        var keys = curve.keys;
        return "keys:" + string.Join("|", keys.Select(k => $"{k.time:F6},{k.value:F6},{k.inTangent:F6},{k.outTangent:F6}"));
    }
}

#endregion

#region Diff Engine

[Serializable]
public class DiffResult
{
    public List<ChangeItem> Added = new List<ChangeItem>();
    public List<ChangeItem> Removed = new List<ChangeItem>();
    public List<ModifiedItem> Modified = new List<ModifiedItem>();
}

[Serializable]
public class ChangeItem
{
    public string GameObjectPath;
    public string ComponentType;
    public int ComponentOrder;
    public string PropertyPath; // null for whole component/GO add/remove
}

[Serializable]
public class ModifiedItem
{
    public string GameObjectPath;
    public string ComponentType;
    public int ComponentOrder;
    public string PropertyPath;
    public string Before;
    public string After;
}

public static class DiffEngine
{
    public static DiffResult Diff(SceneSnapshot baseline, SceneSnapshot current)
    {
        var result = new DiffResult();

        var baseGOs = baseline.GameObjects.ToDictionary(g => g.Path);
        var curGOs = current.GameObjects.ToDictionary(g => g.Path);

        foreach (var path in curGOs.Keys.Except(baseGOs.Keys))
            result.Added.Add(new ChangeItem { GameObjectPath = path, ComponentType = "(GameObject)", ComponentOrder = -1, PropertyPath = null });

        foreach (var path in baseGOs.Keys.Except(curGOs.Keys))
            result.Removed.Add(new ChangeItem { GameObjectPath = path, ComponentType = "(GameObject)", ComponentOrder = -1, PropertyPath = null });

        foreach (var path in baseGOs.Keys.Intersect(curGOs.Keys))
        {
            var b = baseGOs[path];
            var c = curGOs[path];

            var bComps = b.Components.ToDictionary(k => CompKey(k));
            var cComps = c.Components.ToDictionary(k => CompKey(k));

            foreach (var k in cComps.Keys.Except(bComps.Keys))
                result.Added.Add(new ChangeItem { GameObjectPath = path, ComponentType = cComps[k].Type, ComponentOrder = cComps[k].Order, PropertyPath = null });

            foreach (var k in bComps.Keys.Except(cComps.Keys))
                result.Removed.Add(new ChangeItem { GameObjectPath = path, ComponentType = bComps[k].Type, ComponentOrder = bComps[k].Order, PropertyPath = null });

            foreach (var k in bComps.Keys.Intersect(cComps.Keys))
            {
                var bc = bComps[k];
                var cc = cComps[k];

                var bProps = bc.Properties.ToDictionary(p => p.PropertyPath, p => p.Value);
                var cProps = cc.Properties.ToDictionary(p => p.PropertyPath, p => p.Value);

                foreach (var prop in cProps.Keys.Except(bProps.Keys))
                    result.Added.Add(new ChangeItem { GameObjectPath = path, ComponentType = cc.Type, ComponentOrder = cc.Order, PropertyPath = prop });

                foreach (var prop in bProps.Keys.Except(cProps.Keys))
                    result.Removed.Add(new ChangeItem { GameObjectPath = path, ComponentType = bc.Type, ComponentOrder = bc.Order, PropertyPath = prop });

                foreach (var prop in bProps.Keys.Intersect(cProps.Keys))
                {
                    var bv = bProps[prop];
                    var cv = cProps[prop];
                    if (!string.Equals(bv, cv, StringComparison.Ordinal))
                    {
                        result.Modified.Add(new ModifiedItem
                        {
                            GameObjectPath = path,
                            ComponentType = bc.Type,
                            ComponentOrder = bc.Order,
                            PropertyPath = prop,
                            Before = bv,
                            After = cv
                        });
                    }
                }
            }
        }

        result.Added = result.Added.OrderBy(k => k.GameObjectPath).ThenBy(k => k.ComponentType).ThenBy(k => k.ComponentOrder).ThenBy(k => k.PropertyPath).ToList();
        result.Removed = result.Removed.OrderBy(k => k.GameObjectPath).ThenBy(k => k.ComponentType).ThenBy(k => k.ComponentOrder).ThenBy(k => k.PropertyPath).ToList();
        result.Modified = result.Modified.OrderBy(k => k.GameObjectPath).ThenBy(k => k.ComponentType).ThenBy(k => k.ComponentOrder).ThenBy(k => k.PropertyPath).ToList();

        return result;
    }

    private static string CompKey(ComponentSnapshot c) => $"{c.Type}#{c.Order}";
}

#endregion
#endif

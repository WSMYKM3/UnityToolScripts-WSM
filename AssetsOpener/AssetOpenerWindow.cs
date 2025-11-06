#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

public class AssetOpenerWindow : EditorWindow
{
    private const string kDataAssetPath = "Assets/Editor/AssetOpenerWindowData.asset"; // old single-data asset (for migration)
    private const string kProfilesAssetPath = "Assets/Editor/AssetOpenerProfilesData.asset"; // new multi-profile asset

    // --- Profile container ---
    private AssetOpenerProfilesData _profilesData;
    private int _selectedProfileIndex = 0;
    private string _collaboratorName = string.Empty;

    // --- Level Scenes (NEW list replacing "Single Scene") ---
    [SerializeField] private List<SceneAsset> _levelScenes = new List<SceneAsset>();
    private ReorderableList _levelScenesReorderable;

    // --- Scene List WITH TIPS (changed to SceneNote) ---
    [System.Serializable]
    public class SceneNote
    {
        public SceneAsset scene;
        public string note;
        public bool noteExpanded; // NEW: controls whether Tip field is visible
    }

    [SerializeField] private List<SceneNote> _sceneList = new List<SceneNote>();
    private ReorderableList _sceneReorderable;

    // --- Folder list (ENTER only) ---
    [SerializeField] private List<DefaultAsset> _folderList = new List<DefaultAsset>();
    private ReorderableList _folderReorderable;

    // --- Asset list (ANY PROJECT ASSET + NOTES) ---
    [System.Serializable]
    public class AssetNote
    {
        public Object asset;
        public string note;
    }
    [SerializeField] private List<AssetNote> _assetList = new List<AssetNote>();
    private ReorderableList _assetReorderable;

    // --- Scene GameObject list (sticky selection) ---
    [System.Serializable]
    public class SceneGameObject
    {
        public GameObject gameObject;
        public GlobalObjectId objectId;
    }

    [SerializeField] private List<SceneGameObject> _sceneGameObjectList = new List<SceneGameObject>();
    private ReorderableList _sceneGameObjectReorderable;

    [MenuItem("Tools/Asset Opener")]
    public static void ShowWindow()
    {
        var win = GetWindow<AssetOpenerWindow>();
        win.titleContent = new GUIContent("Asset Opener");
        win.minSize = new Vector2(640, 420);
        win.Show();
    }

    private void OnEnable()
    {
        // Load profiles (migrate if needed) and then load the selected profile into the window
        LoadProfilesAsset();

        // Ensure window lists populated before building reorderables
        BuildLevelScenesReorderableList();
        BuildSceneReorderableList();
        BuildFolderReorderableList();
        BuildAssetReorderableList();
        BuildSceneGameObjectReorderableList();

        EditorSceneManager.activeSceneChanged += OnActiveSceneChanged;
        EditorSceneManager.sceneOpened += OnSceneOpened;
    }

    private void OnDisable()
    {
        EditorSceneManager.activeSceneChanged -= OnActiveSceneChanged;
        EditorSceneManager.sceneOpened -= OnSceneOpened;

        SaveCurrentWindowToProfile();
        SaveProfilesAsset();
    }

    private void OnActiveSceneChanged(Scene oldScene, Scene newScene)
    {
        TryRestoreAllGameObjects();
        Repaint();
    }

    private void OnSceneOpened(Scene scene, OpenSceneMode mode)
    {
        TryRestoreAllGameObjects();
        Repaint();
    }

    private void OnHierarchyChange()
    {
        TryRestoreAllGameObjects();
        Repaint();
    }

    // ===================== GUI =====================

    private void OnGUI()
    {
        GUILayout.Space(6);

        DrawCollaboratorBlock();
        EditorGUILayout.Space(8);

        DrawLevelScenesBlock();
        EditorGUILayout.Space(10);

        DrawSceneListBlock();
        EditorGUILayout.Space(10);

        DrawFolderListBlock();
        EditorGUILayout.Space(10);

        DrawAssetListBlock();
        EditorGUILayout.Space(10);

        DrawSceneGameObjectBlock();
    }

    // --- Collaborator (profile) UI ---

    private void DrawCollaboratorBlock()
    {
        EditorGUILayout.LabelField("Collaborator", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            _collaboratorName = EditorGUILayout.TextField(_collaboratorName);

            if (GUILayout.Button("New", GUILayout.Width(60)))
            {
                CreateNewProfileFromName(_collaboratorName);
            }

            if (GUILayout.Button("Delete", GUILayout.Width(60)))
            {
                DeleteCurrentProfileWithConfirm();
            }
        }

        // Popup list (scrollable automatically when many)
        if (_profilesData != null && _profilesData.profiles.Count > 0)
        {
            var names = _profilesData.profiles.Select(p => p.name).ToArray();
            int newIndex = EditorGUILayout.Popup(_selectedProfileIndex, names);
            if (newIndex != _selectedProfileIndex)
            {
                SaveCurrentWindowToProfile();
                _selectedProfileIndex = newIndex;
                LoadProfileToWindow(_selectedProfileIndex);
            }

            // Rename button
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rename", GUILayout.Width(80)))
                {
                    RenameCurrentProfile(_collaboratorName);
                }
                if (GUILayout.Button("Save Now", GUILayout.Width(80)))
                {
                    SaveCurrentWindowToProfile();
                    SaveProfilesAsset();
                    EditorUtility.DisplayDialog("Saved", "Profile saved.", "OK");
                }
            }
        }
        else
        {
            EditorGUILayout.HelpBox("No profiles yet. Enter a name and press New to create a profile.", MessageType.Info);
        }
    }

    private void CreateNewProfileFromName(string name)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "Collaborator" : name.Trim();
        if (_profilesData == null) CreateEmptyProfilesAsset();

        
        // ensure unique
        string unique = safeName;
        int suffix = 1;
        while (_profilesData.profiles.Any(p => p.name == unique))
        {
            unique = $"{safeName} ({suffix++})";
        }

        var p = new AssetOpenerProfilesData.Profile();
        p.name = unique;
        // start empty profile
        _profilesData.profiles.Add(p);
        _selectedProfileIndex = _profilesData.profiles.Count - 1;
        _profilesData.lastSelectedProfileIndex = _selectedProfileIndex;
        EditorUtility.SetDirty(_profilesData);
        AssetDatabase.SaveAssets();

        LoadProfileToWindow(_selectedProfileIndex);
    }

    private void DeleteCurrentProfileWithConfirm()
    {
        if (_profilesData == null || _profilesData.profiles.Count == 0) return;

        var name = _profilesData.profiles[_selectedProfileIndex].name;
        if (!EditorUtility.DisplayDialog("Delete profile", $"Delete profile '{name}'?", "Delete", "Cancel")) return;

        _profilesData.profiles.RemoveAt(_selectedProfileIndex);
        _selectedProfileIndex = Mathf.Clamp(_selectedProfileIndex, 0, Mathf.Max(0, _profilesData.profiles.Count - 1));
        _profilesData.lastSelectedProfileIndex = _selectedProfileIndex;
        EditorUtility.SetDirty(_profilesData);
        AssetDatabase.SaveAssets();

        LoadProfileToWindow(_selectedProfileIndex);
    }

    private void RenameCurrentProfile(string newName)
    {
        if (_profilesData == null || _profilesData.profiles.Count == 0) return;
        
        if (string.IsNullOrWhiteSpace(newName)) { EditorUtility.DisplayDialog("Invalid name", "Name must not be empty.", "OK"); return; }

        var trimmed = newName.Trim();
        if (_profilesData.profiles.Any((p) => p.name == trimmed))
        {
            EditorUtility.DisplayDialog("Duplicate", "A profile with that name already exists.", "OK");
            return;
        }

        _profilesData.profiles[_selectedProfileIndex].name = trimmed;
        EditorUtility.SetDirty(_profilesData);
        AssetDatabase.SaveAssets();
    }

    // --- Level Scenes list ---

    private void BuildLevelScenesReorderableList()
    {
        if (_levelScenes == null) _levelScenes = new List<SceneAsset>();
        _levelScenesReorderable = new ReorderableList(_levelScenes, typeof(SceneAsset), true, true, true, true);

        _levelScenesReorderable.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Level Scenes (drag scenes here)");
        };

        _levelScenesReorderable.onAddCallback = list => _levelScenes.Add(null);

        _levelScenesReorderable.drawElementCallback = (rect, index, active, focused) =>
        {
            var r = new Rect(rect.x, rect.y + 2, rect.width, EditorGUIUtility.singleLineHeight);

            float btnW = 160f;
            var fieldRect = new Rect(r.x, r.y, r.width - btnW - 6f, r.height);
            var btnRect = new Rect(fieldRect.xMax + 6f, r.y, btnW, r.height);

            _levelScenes[index] = (SceneAsset)EditorGUI.ObjectField(fieldRect, _levelScenes[index], typeof(SceneAsset), false);

            using (new EditorGUI.DisabledScope(_levelScenes[index] == null))
            {
                var half = btnRect; half.width = (btnRect.width - 6f) * 0.5f;
                var right = new Rect(half.xMax + 6f, half.y, half.width, half.height);

                if (GUI.Button(half, "Open"))
                    OpenSceneSingle(_levelScenes[index]);

                if (GUI.Button(right, "Open +"))
                    OpenSceneAdditive(_levelScenes[index]);
            }
        };

        _levelScenesReorderable.onRemoveCallback = list =>
        {
            if (list.index >= 0 && list.index < _levelScenes.Count) _levelScenes.RemoveAt(list.index);
        };
    }

    private void DrawLevelScenesBlock()
    {
        EditorGUILayout.LabelField("Level Scenes", EditorStyles.boldLabel);
        _levelScenesReorderable.DoLayoutList();

        // Drag area
        HandleLevelScenesDropArea();
    }

    private void HandleLevelScenesDropArea()
    {
        var rect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "Drop scenes here to add to Level Scenes", EditorStyles.helpBox);

        var evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                    if (obj is SceneAsset sa && !_levelScenes.Contains(sa)) _levelScenes.Add(sa);
                Repaint();
            }
            evt.Use();
        }
    }
    
// csharp
    private void LoadProfilesAsset()
    {
        _profilesData = AssetDatabase.LoadAssetAtPath<AssetOpenerProfilesData>(kProfilesAssetPath);
    
        if (_profilesData == null)
        {
            // 创建 asset 文件（如果不存在）
            CreateEmptyProfilesAsset();
            // 重新加载
            _profilesData = AssetDatabase.LoadAssetAtPath<AssetOpenerProfilesData>(kProfilesAssetPath);
            if (_profilesData == null)
            {
                Debug.LogError("无法创建或加载 " + kProfilesAssetPath);
                return;
            }
    
            // 保证至少有一个默认 profile
            if (_profilesData.profiles == null)
                _profilesData.profiles = new List<AssetOpenerProfilesData.Profile>();
    
            if (_profilesData.profiles.Count == 0)
            {
                var p = new AssetOpenerProfilesData.Profile();
                p.name = "Default";
                _profilesData.profiles.Add(p);
                _profilesData.lastSelectedProfileIndex = 0;
                EditorUtility.SetDirty(_profilesData);
                AssetDatabase.SaveAssets();
            }
        }
    
        // 设置选中并加载到窗口
        _selectedProfileIndex = Mathf.Clamp(_profilesData.lastSelectedProfileIndex, 0, Mathf.Max(0, _profilesData.profiles.Count - 1));
        LoadProfileToWindow(_selectedProfileIndex);
    }
    
    private void CreateEmptyProfilesAsset()
    {
        var dir = Path.GetDirectoryName(kProfilesAssetPath) ?? "Assets";
        dir = dir.Replace("\\", "/");
    
        if (!AssetDatabase.IsValidFolder(dir))
        {
            var parts = dir.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }
                cur = next;
            }
        }
    
        // 如果已经存在，不再覆盖
        if (AssetDatabase.LoadAssetAtPath<AssetOpenerProfilesData>(kProfilesAssetPath) != null)
            return;
    
        var obj = ScriptableObject.CreateInstance<AssetOpenerProfilesData>();
        AssetDatabase.CreateAsset(obj, kProfilesAssetPath);
        AssetDatabase.SaveAssets();
        EditorUtility.SetDirty(obj);
    }
    
    private void SaveProfilesAsset()
    {
        if (_profilesData == null) return;
        _profilesData.lastSelectedProfileIndex = _selectedProfileIndex;
        EditorUtility.SetDirty(_profilesData);
        AssetDatabase.SaveAssets();
    }
    

    // --- Scene List WITH tip per item ---

    private void BuildSceneReorderableList()
    {
        if (_sceneList == null) _sceneList = new List<SceneNote>();
        _sceneReorderable = new ReorderableList(_sceneList, typeof(SceneNote), true, true, true, true);

        _sceneReorderable.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Scene List (optional tips per scene)");
        };

        _sceneReorderable.onAddCallback = list => _sceneList.Add(new SceneNote());

        // Each item can optionally expand for its tip
        _sceneReorderable.elementHeightCallback = index =>
        {
            var item = _sceneList[index];
            if (item == null) return EditorGUIUtility.singleLineHeight + 6f;
            float line = EditorGUIUtility.singleLineHeight + 4f;
            return item.noteExpanded ? line * 2f + 4f : line;
        };

        _sceneReorderable.drawElementCallback = (rect, index, active, focused) =>
        {
            if (_sceneList[index] == null) _sceneList[index] = new SceneNote();
            var item = _sceneList[index];

            var line = EditorGUIUtility.singleLineHeight;
            var y = rect.y + 2;

            // Row 1: scene field + buttons + foldout arrow
            float btnW = 140f;
            float foldW = 16f;

            var foldRect = new Rect(rect.x, y, foldW, line);
            var fieldRect = new Rect(foldRect.xMax + 2f, y, rect.width - foldW - btnW - 10f, line);
            var btnRect = new Rect(fieldRect.xMax + 6f, y, btnW, line);

            item.noteExpanded = EditorGUI.Foldout(foldRect, item.noteExpanded, GUIContent.none, true);
            item.scene = (SceneAsset)EditorGUI.ObjectField(fieldRect, item.scene, typeof(SceneAsset), false);

            using (new EditorGUI.DisabledScope(item.scene == null))
            {
                var half = btnRect; half.width = (btnRect.width - 6f) * 0.5f;
                var right = new Rect(half.xMax + 6f, half.y, half.width, half.height);

                if (GUI.Button(half, "Open"))
                    OpenSceneSingle(item.scene);

                if (GUI.Button(right, "Open +"))
                    OpenSceneAdditive(item.scene);
            }

            // Row 2 (optional): Tip field if expanded
            if (item.noteExpanded)
            {
                y += line + 4f;
                var tipLabelW = 36f;
                var tipLabelRect = new Rect(rect.x + 16f, y, tipLabelW, line);
                var noteRect = new Rect(tipLabelRect.xMax + 4f, y, rect.width - tipLabelW - 24f, line);

                EditorGUI.LabelField(tipLabelRect, "Tip:");
                item.note = EditorGUI.TextField(noteRect, item.note ?? string.Empty);
            }
        };

        _sceneReorderable.onRemoveCallback = list =>
        {
            if (list.index >= 0 && list.index < _sceneList.Count) _sceneList.RemoveAt(list.index);
        };
    }

    private void DrawSceneListBlock()
    {
        EditorGUILayout.LabelField("Scene List", EditorStyles.boldLabel);
        _sceneReorderable.DoLayoutList();
        HandleSceneListDropArea();
    }

    private void HandleSceneListDropArea()
    {
        var rect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "Drop Scene assets here to add to the Scene List (with tips)", EditorStyles.helpBox);

        var evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is SceneAsset sa)
                        _sceneList.Add(new SceneNote { scene = sa, note = "" });
                }
                Repaint();
            }
            evt.Use();
        }
    }

    // --- Folder List (ENTER only) ---

    private void BuildFolderReorderableList()
    {
        if (_folderList == null) _folderList = new List<DefaultAsset>();
        _folderReorderable = new ReorderableList(_folderList, typeof(DefaultAsset), true, true, true, true);

        _folderReorderable.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Folder List (drag folders here)");
        };

        _folderReorderable.onAddCallback = list => _folderList.Add(null);

        _folderReorderable.drawElementCallback = (rect, index, active, focused) =>
        {
            var r = new Rect(rect.x, rect.y + 2, rect.width, EditorGUIUtility.singleLineHeight);

            float btnW = 120f;
            var fieldRect = new Rect(r.x, r.y, r.width - btnW - 6f, r.height);
            var btnRect = new Rect(fieldRect.xMax + 6f, r.y, btnW, r.height);

            _folderList[index] = (DefaultAsset)EditorGUI.ObjectField(fieldRect, _folderList[index], typeof(DefaultAsset), false);

            using (new EditorGUI.DisabledScope(_folderList[index] == null))
            {
                if (GUI.Button(btnRect, "Open (Enter)"))
                {
                    var path = AssetDatabase.GetAssetPath(_folderList[index]);
                    if (AssetDatabase.IsValidFolder(path)) EnterFolderInProject(path); else ShowNonFolderWarning(path);
                }
            }
        };

        _folderReorderable.onRemoveCallback = list =>
        {
            if (list.index >= 0 && list.index < _folderList.Count) _folderList.RemoveAt(list.index);
        };
    }

    private void DrawFolderListBlock()
    {
        EditorGUILayout.LabelField("Folder List", EditorStyles.boldLabel);
        _folderReorderable.DoLayoutList();
        HandleFolderListDropArea();
    }

    private void HandleFolderListDropArea()
    {
        var rect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "Drop Folders here to add to the Folder List", EditorStyles.helpBox);

        var evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    var path = AssetDatabase.GetAssetPath(obj);
                    if (AssetDatabase.IsValidFolder(path))
                    {
                        var asDefault = AssetDatabase.LoadAssetAtPath<DefaultAsset>(path);
                        if (asDefault != null && !_folderList.Contains(asDefault))
                            _folderList.Add(asDefault);
                    }
                }
                Repaint();
            }
            evt.Use();
        }
    }

    // --- Asset List (ANY PROJECT ASSET + NOTES) ---

    private void BuildAssetReorderableList()
    {
        if (_assetList == null) _assetList = new List<AssetNote>();
        _assetReorderable = new ReorderableList(_assetList, typeof(AssetNote), true, true, true, true);

        _assetReorderable.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Asset List (drag assets from Project — any type)");
        };

        _assetReorderable.onAddCallback = list => _assetList.Add(new AssetNote());

        _assetReorderable.elementHeightCallback = index =>
        {
            return EditorGUIUtility.singleLineHeight * 2f + 8f;
        };

        _assetReorderable.drawElementCallback = (rect, index, active, focused) =>
        {
            var item = _assetList[index];
            if (item == null) { _assetList[index] = item = new AssetNote(); }

            var line = EditorGUIUtility.singleLineHeight;
            var y = rect.y + 2;

            // Row 1: Object field + Ping
            float btnW = 70f;
            var objRect = new Rect(rect.x, y, rect.width - btnW - 6f, line);
            var pingRect = new Rect(objRect.xMax + 6f, y, btnW, line);

            EditorGUI.BeginChangeCheck();
            var newObj = EditorGUI.ObjectField(objRect, item.asset, typeof(Object), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (newObj == null || (EditorUtility.IsPersistent(newObj) && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(newObj))))
                    item.asset = newObj;
            }

            using (new EditorGUI.DisabledScope(item.asset == null))
            {
                if (GUI.Button(pingRect, "Ping"))
                {
                    EditorUtility.FocusProjectWindow();
                    EditorGUIUtility.PingObject(item.asset);
                }
            }

            // Row 2: Tip
            y += line + 4f;
            var tipLabelW = 36f;
            var tipLabelRect = new Rect(rect.x, y, tipLabelW, line);
            var noteRect = new Rect(tipLabelRect.xMax + 4f, y, rect.width - tipLabelW - 4f, line);

            EditorGUI.LabelField(tipLabelRect, "Tip:");
            item.note = EditorGUI.TextField(noteRect, item.note ?? string.Empty);
        };

        _assetReorderable.onRemoveCallback = list =>
        {
            if (list.index >= 0 && list.index < _assetList.Count) _assetList.RemoveAt(list.index);
        };
    }

    private void DrawAssetListBlock()
    {
        EditorGUILayout.LabelField("Asset List (with notes)", EditorStyles.boldLabel);
        _assetReorderable.DoLayoutList();
        EditorGUILayout.HelpBox("Tip: drag any Project asset here (prefabs, materials, textures, scripts, etc.). Scene objects are ignored. Use the Ping button to highlight the asset in the Project window.", MessageType.None);
        HandleAssetListDropArea();
    }

    private void HandleAssetListDropArea()
    {
        var rect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "Drop assets here to add to Asset List", EditorStyles.helpBox);

        var evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj == null) continue;
                    if (!EditorUtility.IsPersistent(obj)) continue;
                    var path = AssetDatabase.GetAssetPath(obj);
                    if (string.IsNullOrEmpty(path)) continue;

                    _assetList.Add(new AssetNote { asset = obj, note = "" });
                }
                Repaint();
            }
            evt.Use();
        }
    }

    private static void HandleDragAnywhereHint()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Drag & drop supported", EditorStyles.miniLabel);
        }
    }

    // ===================== Scene open helpers =====================

    private static void OpenSceneSingle(SceneAsset sceneAsset)
    {
        if (sceneAsset == null) return;
        var path = AssetDatabase.GetAssetPath(sceneAsset);
        if (string.IsNullOrEmpty(path))
        {
            EditorUtility.DisplayDialog("Open Scene", "Could not resolve scene path.", "OK");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        if (!scene.IsValid())
            EditorUtility.DisplayDialog("Open Scene", "Failed to open scene.", "OK");
    }

    private static void OpenSceneAdditive(SceneAsset sceneAsset)
    {
        if (sceneAsset == null) return;
        var path = AssetDatabase.GetAssetPath(sceneAsset);
        if (string.IsNullOrEmpty(path))
        {
            EditorUtility.DisplayDialog("Open Scene", "Could not resolve scene path.", "OK");
            return;
        }

        var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        if (!scene.IsValid())
            EditorUtility.DisplayDialog("Open Scene (Additive)", "Failed to open scene additively.", "OK");
    }

    // ===================== Folder navigation helpers =====================

    private static void EnterFolderInProject(string path)
    {
        if (!AssetDatabase.IsValidFolder(path)) { ShowNonFolderWarning(path); return; }

        EditorUtility.FocusProjectWindow();

        var browser = GetProjectBrowserWindow();
        if (browser == null) return;

        var t = browser.GetType();

        var setFolderSelection = t.GetMethod("SetFolderSelection", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string[]), typeof(bool) }, null);
        if (setFolderSelection != null)
        {
            try { setFolderSelection.Invoke(browser, new object[] { new[] { path }, true }); }
            catch { /* ignore */ }
        }

        var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
        if (obj != null)
        {
            var showFolderContents = t.GetMethod("ShowFolderContents", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
            if (showFolderContents != null)
            {
                try { showFolderContents.Invoke(browser, new object[] { obj.GetInstanceID(), true }); }
                catch { /* ignore */ }
            }
        }

        browser.Focus();
    }

    private static EditorWindow GetProjectBrowserWindow()
    {
        var type = typeof(Editor).Assembly.GetType("UnityEditor.ProjectBrowser");
        if (type == null) return null;
        var windows = Resources.FindObjectsOfTypeAll(type);
        if (windows != null && windows.Length > 0)
            return windows[0] as EditorWindow;
        return null;
    }

    // ===================== Selection helpers (sticky) =====================

    private void UpdateGameObjectId(SceneGameObject item)
    {
        if (item == null) return;
        if (item.gameObject != null)
        {
            item.objectId = GlobalObjectId.GetGlobalObjectIdSlow(item.gameObject);
        }
        else
        {
            item.objectId = default;
        }
    }

    private bool IsStickyIdValid(GlobalObjectId id)
    {
        return !id.Equals(default(GlobalObjectId));
    }

    private void TryRestoreAllGameObjects()
    {
        if (_sceneGameObjectList == null) return;

        foreach (var item in _sceneGameObjectList)
        {
            if (item == null) continue;
            
            if (IsStickyIdValid(item.objectId))
            {
                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(item.objectId);
                var go = obj as GameObject;
                if (go != null)
                {
                    item.gameObject = go;
                }
                else
                {
                    item.gameObject = null;
                }
            }
        }
    }

    private static void SelectInHierarchy(GameObject go, bool ping)
    {
        if (go == null) return;

        Selection.activeObject = go;
        if (ping) EditorGUIUtility.PingObject(go);

        var type = typeof(Editor).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        if (type != null)
        {
            var hierarchy = GetWindow(type);
            hierarchy?.Focus();
        }
    }

    private static void ShowNonFolderWarning(string path)
    {
        EditorUtility.DisplayDialog("Not a Folder",
            string.IsNullOrEmpty(path)
                ? "That reference does not have a valid path."
                : $"The selected asset is not a folder:\n{path}",
            "OK");
    }

    // ===================== Scene GameObject List UI =====================

    private void BuildSceneGameObjectReorderableList()
    {
        if (_sceneGameObjectList == null) _sceneGameObjectList = new List<SceneGameObject>();
        _sceneGameObjectReorderable = new ReorderableList(_sceneGameObjectList, typeof(SceneGameObject), true, true, true, true);

        _sceneGameObjectReorderable.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Scene GameObject List (Sticky Selection)");
        };

        _sceneGameObjectReorderable.onAddCallback = list => 
        {
            var newItem = new SceneGameObject();
            _sceneGameObjectList.Add(newItem);
        };

        _sceneGameObjectReorderable.drawElementCallback = (rect, index, active, focused) =>
        {
            if (_sceneGameObjectList[index] == null) _sceneGameObjectList[index] = new SceneGameObject();
            var item = _sceneGameObjectList[index];

            var r = new Rect(rect.x, rect.y + 2, rect.width, EditorGUIUtility.singleLineHeight);

            float btnW = 100f;
            var fieldRect = new Rect(r.x, r.y, r.width - btnW - 6f, r.height);
            var btnRect = new Rect(fieldRect.xMax + 6f, r.y, btnW, r.height);

            EditorGUI.BeginChangeCheck();
            var newGO = (GameObject)EditorGUI.ObjectField(fieldRect, item.gameObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                item.gameObject = newGO;
                UpdateGameObjectId(item);
            }

            using (new EditorGUI.DisabledScope(item.gameObject == null))
            {
                if (GUI.Button(btnRect, "Select"))
                {
                    SelectInHierarchy(item.gameObject, ping: true);
                }
            }

            // Show warning if GameObject is in a different scene
            if (item.gameObject != null && item.gameObject.scene.IsValid())
            {
                var activeScene = SceneManager.GetActiveScene();
                if (item.gameObject.scene != activeScene)
                {
                    var warningRect = new Rect(r.x, r.y + r.height + 2, r.width, EditorGUIUtility.singleLineHeight);
                    EditorGUI.HelpBox(warningRect, 
                        $"In scene '{item.gameObject.scene.name}' (active: '{activeScene.name}')", 
                        MessageType.Warning);
                }
            }
        };

        _sceneGameObjectReorderable.elementHeightCallback = index =>
        {
            if (_sceneGameObjectList == null || index < 0 || index >= _sceneGameObjectList.Count)
                return EditorGUIUtility.singleLineHeight + 6f;

            var item = _sceneGameObjectList[index];
            if (item == null) return EditorGUIUtility.singleLineHeight + 6f;

            float baseHeight = EditorGUIUtility.singleLineHeight + 4f;
            // Add extra height if warning is shown
            if (item.gameObject != null && item.gameObject.scene.IsValid())
            {
                var activeScene = SceneManager.GetActiveScene();
                if (item.gameObject.scene != activeScene)
                {
                    baseHeight += EditorGUIUtility.singleLineHeight + 4f;
                }
            }
            return baseHeight;
        };

        _sceneGameObjectReorderable.onRemoveCallback = list =>
        {
            if (list.index >= 0 && list.index < _sceneGameObjectList.Count) 
                _sceneGameObjectList.RemoveAt(list.index);
        };
    }

    private void DrawSceneGameObjectBlock()
    {
        EditorGUILayout.LabelField("Scene GameObject List", EditorStyles.boldLabel);
        _sceneGameObjectReorderable.DoLayoutList();
    }

    // ===================== Persistence helpers (profiles) ====================

    private void LoadProfileToWindow(int index)
    {
        if (_profilesData == null || _profilesData.profiles.Count == 0)
        {
            // ensure not null lists
            if (_levelScenes == null) _levelScenes = new List<SceneAsset>();
            if (_sceneList == null) _sceneList = new List<SceneNote>();
            if (_folderList == null) _folderList = new List<DefaultAsset>();
            if (_assetList == null) _assetList = new List<AssetNote>();
            if (_sceneGameObjectList == null) _sceneGameObjectList = new List<SceneGameObject>();
            TryRestoreAllGameObjects();
            return;
        }

        index = Mathf.Clamp(index, 0, _profilesData.profiles.Count - 1);
        var p = _profilesData.profiles[index];

        _levelScenes = new List<SceneAsset>(p.levelScenes ?? new List<SceneAsset>());
        _sceneList = new List<SceneNote>(p.sceneNotes ?? new List<SceneNote>());
        _folderList = new List<DefaultAsset>(p.folderList ?? new List<DefaultAsset>());
        _assetList = new List<AssetNote>(p.assetNotes ?? new List<AssetNote>());
        _sceneGameObjectList = new List<SceneGameObject>(p.sceneGameObjects ?? new List<SceneGameObject>());
        TryRestoreAllGameObjects();

        _collaboratorName = p.name;
        RebuildAllReorderables();
    }

    private void SaveCurrentWindowToProfile()
    {
        if (_profilesData == null || _profilesData.profiles.Count == 0) return;

        var index = Mathf.Clamp(_selectedProfileIndex, 0, _profilesData.profiles.Count - 1);
        var p = _profilesData.profiles[index];

        // ensure lists not null
        if (_levelScenes == null) _levelScenes = new List<SceneAsset>();
        if (_sceneList == null) _sceneList = new List<SceneNote>();
        if (_folderList == null) _folderList = new List<DefaultAsset>();
        if (_assetList == null) _assetList = new List<AssetNote>();
        if (_sceneGameObjectList == null) _sceneGameObjectList = new List<SceneGameObject>();

        // Update object IDs before saving
        foreach (var item in _sceneGameObjectList)
        {
            if (item != null)
            {
                UpdateGameObjectId(item);
            }
        }

        p.levelScenes = new List<SceneAsset>(_levelScenes);
        p.sceneNotes = new List<SceneNote>(_sceneList);
        p.folderList = new List<DefaultAsset>(_folderList);
        p.assetNotes = new List<AssetNote>(_assetList);
        p.sceneGameObjects = new List<SceneGameObject>(_sceneGameObjectList);
        p.name = _collaboratorName ?? p.name;

        EditorUtility.SetDirty(_profilesData);
        AssetDatabase.SaveAssets();
    }

    private void RebuildAllReorderables()
    {
        // Recreate reorderable lists to pick up updated lists
        BuildLevelScenesReorderableList();
        BuildSceneReorderableList();
        BuildFolderReorderableList();
        BuildAssetReorderableList();
        BuildSceneGameObjectReorderableList();
        Repaint();
    }

    // ===================== Remaining helper methods (unchanged) =====================

    // ... (Open scene helpers, folder helpers, selection helpers already implemented above)

    // NOTE: The rest of methods (OpenSceneSingle/OpenSceneAdditive/EnterFolderInProject/GetProjectBrowserWindow/Selection helpers)
    // are kept earlier in this file and used as-is.
}
#endif

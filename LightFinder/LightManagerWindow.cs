// File: Assets/Editor/LightManagerWindow.cs
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class LightManagerWindow : EditorWindow
{
    private Vector2 _scroll;
    private List<Light> _allLights = new List<Light>();
    private string _nameFilter = "";
    private int _typeFilterIndex = 0; // 0 = All, else enum order + 1
    private int _enabledFilterIndex = 0; // 0 = All, 1 = Enabled, 2 = Disabled
    private double _highlightSeconds = 2.0;

    // simple highlight tracker
    private readonly Dictionary<int, double> _highlightUntilById = new Dictionary<int, double>();

    private static readonly string[] EnabledFilterOptions = { "All", "Enabled", "Disabled" };
    private static string[] _typeOptions;

    [MenuItem("Tools/Lighting/Light Manager")]
    public static void Open()
    {
        GetWindow<LightManagerWindow>("Light Manager");
    }

    private void OnEnable()
    {
        BuildTypeOptions();
        RefreshLightList();
        EditorApplication.hierarchyChanged += RefreshLightList;
        SceneView.duringSceneGui += OnSceneGUIHighlight;
    }

    private void OnDisable()
    {
        EditorApplication.hierarchyChanged -= RefreshLightList;
        SceneView.duringSceneGui -= OnSceneGUIHighlight;
        _highlightUntilById.Clear();
    }

    private static void BuildTypeOptions()
    {
        // First entry is "All", then the LightType names
        var types = Enum.GetNames(typeof(LightType));
        _typeOptions = new string[types.Length + 1];
        _typeOptions[0] = "All";
        for (int i = 0; i < types.Length; i++) _typeOptions[i + 1] = types[i];
    }

    private void RefreshLightList()
    {
        // include disabled/hidden objects in the scene
        _allLights = UnityEngine.Object.FindObjectsOfType<Light>(true)
            .Where(l => l != null && l.gameObject.scene.IsValid())
            .OrderBy(l => l.name)
            .ToList();

        Repaint();
    }

    private IEnumerable<Light> GetFiltered()
    {
        IEnumerable<Light> q = _allLights;

        // Name filter
        if (!string.IsNullOrWhiteSpace(_nameFilter))
        {
            var needle = _nameFilter.Trim();
            q = q.Where(l => l.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Type filter
        if (_typeFilterIndex > 0)
        {
            var wanted = (LightType)(_typeFilterIndex - 1);
            q = q.Where(l => l.type == wanted);
        }

        // Enabled filter
        if (_enabledFilterIndex == 1) q = q.Where(l => l.enabled);
        else if (_enabledFilterIndex == 2) q = q.Where(l => !l.enabled);

        return q;
    }

    private void OnGUI()
    {
        DrawHeader();
        EditorGUILayout.Space(6);
        DrawToolbar();
        EditorGUILayout.Space(6);
        DrawList();
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Light Manager", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                RefreshLightList();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            _nameFilter = EditorGUILayout.TextField(new GUIContent("Name Filter"), _nameFilter);

            _typeFilterIndex = EditorGUILayout.Popup(new GUIContent("Type"), _typeFilterIndex, _typeOptions, GUILayout.MaxWidth(240));

            _enabledFilterIndex = EditorGUILayout.Popup(new GUIContent("State"), _enabledFilterIndex, EnabledFilterOptions, GUILayout.MaxWidth(200));
        }

        var count = GetFiltered().Count();
        EditorGUILayout.HelpBox($"Showing {count} / {_allLights.Count} lights", MessageType.Info);
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Select All (Filtered)", GUILayout.Height(24)))
            {
                var objs = GetFiltered().Select(l => l.gameObject).ToArray();
                Selection.objects = objs;
                if (objs.Length > 0) EditorGUIUtility.PingObject(objs[0]);
            }

            if (GUILayout.Button("Toggle All (Filtered)", GUILayout.Height(24)))
            {
                foreach (var l in GetFiltered())
                {
                    Undo.RecordObject(l, "Toggle Light");
                    l.enabled = !l.enabled;
                    EditorUtility.SetDirty(l);
                }
            }

            if (GUILayout.Button("Highlight All (Filtered)", GUILayout.Height(24)))
            {
                var now = EditorApplication.timeSinceStartup;
                foreach (var l in GetFiltered())
                {
                    _highlightUntilById[l.GetInstanceID()] = now + _highlightSeconds;
                }
                SceneView.RepaintAll();
            }

            GUILayout.FlexibleSpace();

            _highlightSeconds = EditorGUILayout.DoubleField(new GUIContent("Highlight (s)"), _highlightSeconds, GUILayout.Width(160));
        }
    }

    private void DrawList()
    {
        using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
        {
            _scroll = scroll.scrollPosition;

            // header row
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Name", EditorStyles.boldLabel, GUILayout.Width(220));
                GUILayout.Label("Type", EditorStyles.boldLabel, GUILayout.Width(90));
                GUILayout.Label("Enabled", EditorStyles.boldLabel, GUILayout.Width(70));
                GUILayout.Label("Intensity", EditorStyles.boldLabel, GUILayout.Width(70));
                GUILayout.FlexibleSpace();
                GUILayout.Label("Actions", EditorStyles.boldLabel, GUILayout.Width(240));
            }

            foreach (var light in GetFiltered())
            {
                if (light == null) continue;

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.ObjectField(light, typeof(Light), true, GUILayout.Width(220));
                    GUILayout.Label(light.type.ToString(), GUILayout.Width(90));

                    // toggle checkbox
                    bool newEnabled = EditorGUILayout.Toggle(light.enabled, GUILayout.Width(70));
                    if (newEnabled != light.enabled)
                    {
                        Undo.RecordObject(light, "Toggle Light");
                        light.enabled = newEnabled;
                        EditorUtility.SetDirty(light);
                    }

                    // intensity field
                    float newIntensity = EditorGUILayout.FloatField(light.intensity, GUILayout.Width(70));
                    if (!Mathf.Approximately(newIntensity, light.intensity))
                    {
                        Undo.RecordObject(light, "Change Light Intensity");
                        light.intensity = Mathf.Max(0f, newIntensity);
                        EditorUtility.SetDirty(light);
                    }

                    GUILayout.FlexibleSpace();

                    // actions
                    using (new EditorGUILayout.HorizontalScope(GUILayout.Width(240)))
                    {
                        if (GUILayout.Button("Select", GUILayout.Width(70)))
                        {
                            Selection.activeObject = light.gameObject;
                            EditorGUIUtility.PingObject(light.gameObject);
                        }

                        if (GUILayout.Button(light.enabled ? "Disable" : "Enable", GUILayout.Width(70)))
                        {
                            Undo.RecordObject(light, "Toggle Light");
                            light.enabled = !light.enabled;
                            EditorUtility.SetDirty(light);
                        }

                        if (GUILayout.Button("Highlight", GUILayout.Width(80)))
                        {
                            double until = EditorApplication.timeSinceStartup + _highlightSeconds;
                            _highlightUntilById[light.GetInstanceID()] = until;
                            Selection.activeObject = light.gameObject; // helps framing
                            EditorGUIUtility.PingObject(light.gameObject);
                            SceneView.lastActiveSceneView?.FrameSelected();
                            SceneView.RepaintAll();
                        }
                    }
                }
            }
        }
    }

    // Draw simple temporary outlines / gizmos for highlighted lights
    private void OnSceneGUIHighlight(SceneView sceneView)
    {
        if (_highlightUntilById.Count == 0) return;

        double now = EditorApplication.timeSinceStartup;
        var expired = new List<int>();

        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

        foreach (var kvp in _highlightUntilById)
        {
            int id = kvp.Key;
            double until = kvp.Value;
            if (now > until)
            {
                expired.Add(id);
                continue;
            }

            var light = EditorUtility.InstanceIDToObject(id) as Light;
            if (light == null) { expired.Add(id); continue; }

            // a simple pulsing alpha
            float t = (float)(until - now);
            float alpha = Mathf.InverseLerp(0f, (float)_highlightSeconds, t);
            var c = new Color(1f, 0.85f, 0.2f, Mathf.Lerp(0.15f, 0.6f, alpha)); // warm yellow-ish

            using (new Handles.DrawingScope(c))
            {
                var pos = light.transform.position;

                switch (light.type)
                {
                    case LightType.Directional:
                        // draw an arrow indicating direction
                        Vector3 dir = -light.transform.forward;
                        Handles.ArrowHandleCap(0, pos, Quaternion.LookRotation(dir), 2.5f, EventType.Repaint);
                        Handles.DrawWireDisc(pos, dir, 1.5f);
                        break;

                    case LightType.Point:
                        Handles.SphereHandleCap(0, pos, Quaternion.identity, 1.0f, EventType.Repaint);
                        Handles.DrawWireDisc(pos, Vector3.up, 1.0f);
                        break;

                    case LightType.Spot:
                        // cone-ish visualization
                        float range = light.range;
                        float angle = light.spotAngle;
                        Vector3 forward = light.transform.forward;
                        Quaternion rot = light.transform.rotation;
                        float radius = Mathf.Tan(angle * 0.5f * Mathf.Deg2Rad) * range;
                        Handles.DrawWireDisc(pos + forward * range, forward, radius);
                        Handles.DrawLine(pos, pos + (rot * Quaternion.Euler(angle * 0.5f, 0f, 0f)) * Vector3.forward * range);
                        Handles.DrawLine(pos, pos + (rot * Quaternion.Euler(-angle * 0.5f, 0f, 0f)) * Vector3.forward * range);
                        Handles.DrawLine(pos, pos + (rot * Quaternion.Euler(0f, angle * 0.5f, 0f)) * Vector3.forward * range);
                        Handles.DrawLine(pos, pos + (rot * Quaternion.Euler(0f, -angle * 0.5f, 0f)) * Vector3.forward * range);
                        break;

                    case LightType.Rectangle:
#if UNITY_2021_2_OR_NEWER
                        var rect = light.areaSize;
#else
                        var rect = new Vector2(1f, 1f);
#endif
                        var right = light.transform.right * rect.x * 0.5f;
                        var up = light.transform.up * rect.y * 0.5f;
                        Handles.DrawAAPolyLine(3f, new[]
                        {
                            pos - right - up, pos + right - up,
                            pos + right + up, pos - right + up, pos - right - up
                        });
                        break;
                }
            }
        }

        // cleanup expired
        foreach (var id in expired) _highlightUntilById.Remove(id);

        // keep repainting while anything is highlighted
        if (_highlightUntilById.Count > 0)
            sceneView.Repaint();
    }
}

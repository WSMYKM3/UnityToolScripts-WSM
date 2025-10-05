// URPMaterialFolderConverter.cs
// Convert all Built-in "Standard" materials in a folder to "Universal Render Pipeline/Lit".
// Place this script inside an `Editor` folder.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class URPMaterialFolderConverter : EditorWindow
{
    [SerializeField] private DefaultAsset _targetFolder; // drag a Project folder
    [SerializeField] private bool _includeSubfolders = true; // FindAssets already searches recursively for that folder
    [SerializeField] private bool _logDetails = true;

    private Vector2 _scroll;
    private readonly List<string> _logLines = new();

    [MenuItem("Tools/Rendering/Convert Folder Materials To URP Lit")]
    public static void ShowWindow()
    {
        GetWindow<URPMaterialFolderConverter>("Folder → URP/Lit");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Convert Built-in 'Standard' Materials → URP/Lit", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Drag a Project folder. The tool finds all Materials inside and, if their shader is 'Standard' or 'Standard (Specular setup)', converts them to 'Universal Render Pipeline/Lit'.",
            MessageType.Info);

        _targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("Folder", _targetFolder, typeof(DefaultAsset), false);
        _includeSubfolders = EditorGUILayout.ToggleLeft("Include subfolders (recursive)", _includeSubfolders);
        _logDetails = EditorGUILayout.ToggleLeft("Verbose log", _logDetails);

        using (new EditorGUI.DisabledScope(_targetFolder == null))
        {
            if (GUILayout.Button("Convert Materials", GUILayout.Height(28)))
                ConvertFolder();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var line in _logLines)
            EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    private void ConvertFolder()
    {
        _logLines.Clear();

        if (_targetFolder == null)
            return;

        string folderPath = AssetDatabase.GetAssetPath(_targetFolder);
        if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
        {
            _logLines.Add("Please drop a valid Project folder.");
            return;
        }

        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
        {
            _logLines.Add("ERROR: URP/Lit shader not found. Ensure the Universal RP package is installed and URP is set up.");
            return;
        }

        string[] searchFolders = new[] { folderPath };
        string[] guids = AssetDatabase.FindAssets("t:Material", searchFolders);

        int converted = 0, skipped = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { skipped++; continue; }

            string shaderName = mat.shader != null ? mat.shader.name : "(null)";
            bool isStandard = shaderName == "Standard" || shaderName == "Standard (Specular setup)";

            if (!isStandard)
            {
                skipped++;
                if (_logDetails) _logLines.Add($"Skip: {mat.name} — shader = {shaderName}");
                continue;
            }

            Undo.RecordObject(mat, "Convert to URP/Lit");

            // Cache standard properties before switching shader
            var cache = new StandardCache(mat, shaderName);

            // Switch shader
            mat.shader = urpLit;

            // Transfer properties to URP/Lit
            ApplyToURPLit(mat, cache);

            EditorUtility.SetDirty(mat);
            converted++;
            _logLines.Add($"Converted: {mat.name}  ({path})");
        }

        AssetDatabase.SaveAssets();
        _logLines.Add($"--- Done. Converted {converted} material(s); skipped {skipped}.");
        Repaint();

        // Console summary (single block)
        Debug.Log($"[URP Convert] Folder: {folderPath}\n" +
                  string.Join("\n", _logLines.Select(l => l)));
    }

    // ---------------------------------------------------------------------
    // Property ID cache (use int-based API to avoid string-based lookups)
    // ---------------------------------------------------------------------
    private static class PropId
    {
        // URP Lit
        public static readonly int BaseMap           = Shader.PropertyToID("_BaseMap");
        public static readonly int BaseColor         = Shader.PropertyToID("_BaseColor");
        public static readonly int Metallic          = Shader.PropertyToID("_Metallic");
        public static readonly int MetallicGlossMap  = Shader.PropertyToID("_MetallicGlossMap");
        public static readonly int Smoothness        = Shader.PropertyToID("_Smoothness");
        public static readonly int BumpMap           = Shader.PropertyToID("_BumpMap");
        public static readonly int BumpScale         = Shader.PropertyToID("_BumpScale");
        public static readonly int OcclusionMap      = Shader.PropertyToID("_OcclusionMap");
        public static readonly int OcclusionStrength = Shader.PropertyToID("_OcclusionStrength");
        public static readonly int EmissionMap       = Shader.PropertyToID("_EmissionMap");
        public static readonly int EmissionColor     = Shader.PropertyToID("_EmissionColor");
        public static readonly int AlphaClip         = Shader.PropertyToID("_AlphaClip");
        public static readonly int Cutoff            = Shader.PropertyToID("_Cutoff");

        // Built-in Standard
        public static readonly int Color             = Shader.PropertyToID("_Color");
        public static readonly int MainTex           = Shader.PropertyToID("_MainTex");
        public static readonly int MetallicStd       = Shader.PropertyToID("_Metallic");
        public static readonly int MetallicGlossStd  = Shader.PropertyToID("_MetallicGlossMap");
        public static readonly int GlossinessStd     = Shader.PropertyToID("_Glossiness");
        public static readonly int GlossMapScaleStd  = Shader.PropertyToID("_GlossMapScale");
        public static readonly int BumpMapStd        = Shader.PropertyToID("_BumpMap");
        public static readonly int BumpScaleStd      = Shader.PropertyToID("_BumpScale");
        public static readonly int OcclusionMapStd   = Shader.PropertyToID("_OcclusionMap");
        public static readonly int OcclusionStrStd   = Shader.PropertyToID("_OcclusionStrength");
        public static readonly int EmissionMapStd    = Shader.PropertyToID("_EmissionMap");
        public static readonly int EmissionColorStd  = Shader.PropertyToID("_EmissionColor");
    }

    // ---------------------------------------------------------------------
    // Cache for Standard properties (read before switching shader)
    // ---------------------------------------------------------------------
    private sealed class StandardCache
    {
        public string Workflow { get; }   // "Metallic" or "Specular" (for reference)
        public Color Color { get; }
        public Texture MainTex { get; }
        public Vector2 MainTexScale { get; }
        public Vector2 MainTexOffset { get; }

        public float Metallic { get; }
        public Texture MetallicGlossMap { get; }
        public float Glossiness { get; }
        public float GlossMapScale { get; }

        public Texture NormalMap { get; }
        public float BumpScale { get; }

        public Texture OcclusionMap { get; }
        public float OcclusionStrength { get; }

        public Texture EmissionMap { get; }
        public Color EmissionColor { get; }
        public bool EmissionEnabled { get; }

        public bool AlphaClip { get; }
        public float Cutoff { get; }

        public StandardCache(Material m, string shaderName)
        {
            Workflow = shaderName.Contains("Specular") ? "Specular" : "Metallic";

            if (m.HasProperty(PropId.Color))            Color          = m.GetColor(PropId.Color);
            if (m.HasProperty(PropId.MainTex))
            {
                MainTex      = m.GetTexture(PropId.MainTex);
                MainTexScale = m.GetTextureScale(PropId.MainTex);
                MainTexOffset= m.GetTextureOffset(PropId.MainTex);
            }

            if (m.HasProperty(PropId.MetallicStd))      Metallic       = m.GetFloat(PropId.MetallicStd);
            if (m.HasProperty(PropId.MetallicGlossStd)) MetallicGlossMap = m.GetTexture(PropId.MetallicGlossStd);
            if (m.HasProperty(PropId.GlossinessStd))    Glossiness     = m.GetFloat(PropId.GlossinessStd);
            if (m.HasProperty(PropId.GlossMapScaleStd)) GlossMapScale  = m.GetFloat(PropId.GlossMapScaleStd);

            if (m.HasProperty(PropId.BumpMapStd))       NormalMap      = m.GetTexture(PropId.BumpMapStd);
            if (m.HasProperty(PropId.BumpScaleStd))     BumpScale      = m.GetFloat(PropId.BumpScaleStd);

            if (m.HasProperty(PropId.OcclusionMapStd))  OcclusionMap   = m.GetTexture(PropId.OcclusionMapStd);
            if (m.HasProperty(PropId.OcclusionStrStd))  OcclusionStrength = m.GetFloat(PropId.OcclusionStrStd);

            if (m.HasProperty(PropId.EmissionMapStd))   EmissionMap    = m.GetTexture(PropId.EmissionMapStd);
            if (m.HasProperty(PropId.EmissionColorStd)) EmissionColor  = m.GetColor(PropId.EmissionColorStd);

#if UNITY_2021_2_OR_NEWER
            EmissionEnabled = m.IsKeywordEnabled("_EMISSION");
#else
            EmissionEnabled = (m.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0;
#endif

            if (m.HasProperty(PropId.Cutoff))
            {
                Cutoff   = m.GetFloat(PropId.Cutoff);
                AlphaClip= Cutoff > 0f;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Apply captured values onto the new URP/Lit material
    // ---------------------------------------------------------------------
    private static void ApplyToURPLit(Material mat, StandardCache c)
    {
        // Base map & color
        if (mat.HasProperty(PropId.BaseMap) && c.MainTex != null)
        {
            mat.SetTexture(PropId.BaseMap, c.MainTex);
            mat.SetTextureScale(PropId.BaseMap, c.MainTexScale);
            mat.SetTextureOffset(PropId.BaseMap, c.MainTexOffset);
        }
        if (mat.HasProperty(PropId.BaseColor))
        {
            var color = c.Color == default ? Color.white : c.Color;
            mat.SetColor(PropId.BaseColor, color);
        }

        // Metallic & smoothness
        if (mat.HasProperty(PropId.Metallic))
            mat.SetFloat(PropId.Metallic, c.Metallic);

        if (mat.HasProperty(PropId.MetallicGlossMap) && c.MetallicGlossMap != null)
            mat.SetTexture(PropId.MetallicGlossMap, c.MetallicGlossMap);

        if (mat.HasProperty(PropId.Smoothness))
            mat.SetFloat(PropId.Smoothness, c.Glossiness > 0 ? c.Glossiness : c.GlossMapScale);

        // Normal map
        if (c.NormalMap != null && mat.HasProperty(PropId.BumpMap))
        {
            mat.SetTexture(PropId.BumpMap, c.NormalMap);
            if (mat.HasProperty(PropId.BumpScale))
                mat.SetFloat(PropId.BumpScale, c.BumpScale == 0 ? 1f : c.BumpScale);
            mat.EnableKeyword("_NORMALMAP");
        }

        // Occlusion
        if (c.OcclusionMap != null && mat.HasProperty(PropId.OcclusionMap))
        {
            mat.SetTexture(PropId.OcclusionMap, c.OcclusionMap);
            if (mat.HasProperty(PropId.OcclusionStrength))
                mat.SetFloat(PropId.OcclusionStrength, c.OcclusionStrength);
        }

        // Emission
        if (mat.HasProperty(PropId.EmissionColor))
        {
            mat.SetColor(PropId.EmissionColor, c.EmissionColor);
            if (c.EmissionEnabled) mat.EnableKeyword("_EMISSION"); else mat.DisableKeyword("_EMISSION");
        }
        if (c.EmissionMap != null && mat.HasProperty(PropId.EmissionMap))
            mat.SetTexture(PropId.EmissionMap, c.EmissionMap);

        // Alpha clip (cutout)
        if (mat.HasProperty(PropId.AlphaClip))
            mat.SetFloat(PropId.AlphaClip, c.AlphaClip ? 1f : 0f);
        if (c.AlphaClip && mat.HasProperty(PropId.Cutoff))
            mat.SetFloat(PropId.Cutoff, Mathf.Clamp01(c.Cutoff));
    }
}

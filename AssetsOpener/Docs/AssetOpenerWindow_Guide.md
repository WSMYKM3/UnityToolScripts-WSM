# AssetOpenerWindow Guide
> This document defines what the AssetOpenerWindow editor script does and how it should behave.

AssetOpenerWindow Guide (Stage 3 — Unity 6 IMGUI version)
Purpose

This Unity EditorWindow improves scene and asset workflow by letting you:

Quickly open scenes or levels directly from the editor.

Jump into folders in the Project window.

Maintain a sticky GameObject selection across scene changes.

Organize assets, folders, and scenes with optional notes and tidy lists.

✨ Key Features
1. Level Scenes List

A ReorderableList of SceneAsset items.

Each entry has two buttons:

Open → opens scene in Single mode.

Open + → opens scene Additively.

You can drag and reorder entries or add new ones via + / - buttons.

2. Scene List (With Optional Tips)

A ReorderableList of SceneNote items:

[System.Serializable]
class SceneNote {
public SceneAsset scene;
public string note;
public bool noteExpanded;
}


Each entry includes:

Scene field

Open and Open + buttons

A foldout arrow to toggle visibility of a “Tip:” text field

Compact when folded, expands when you want to annotate a scene.

3. Folder List (Project Navigation)

A ReorderableList of DefaultAsset (folders only).

Each entry has a single Open (Enter) button:

Navigates inside the folder in Unity’s Project window (no OS browser).

Uses internal UnityEditor.ProjectBrowser reflection calls:

SetFolderSelection(string[], bool)

ShowFolderContents(int, bool)

4. Asset List (With Notes)

A ReorderableList of custom AssetNote objects:

[System.Serializable]
class AssetNote {
public Object asset;
public string note;
}


Works with any Project asset (prefabs, materials, scripts, textures, etc.).

Each entry includes:

Asset field

Ping button (highlights it in Project window)

Optional “Tip:” note field

Scene objects are ignored (only persistent project assets allowed).

5. Sticky Scene GameObject Selection

Lets you keep a selected GameObject even when switching scenes.

Uses:

GlobalObjectId.GetGlobalObjectIdSlow(go)

GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id)

The ID is serialized, so Unity restores it between domain reloads.

Includes:

Select Now button → selects and pings the stored object.

Error warning if the selected GameObject is not in the current scene.

Hides any “OK” messages (clean interface).

🧠 Behavior Summary
Section	Type	Actions	Notes
Level Scenes	SceneAsset list	Open / Open Additive	For main scenes or game levels
Scene List	SceneNote list	Open / Open Additive / Tip	Foldout toggle for tip field
Folder List	Folder list	Open (Enter)	Navigates inside Project folder
Asset List	AssetNote list	Ping	Any Project asset + optional note
GameObject (Sticky)	Scene object	Select Now	Keeps ref across scene loads
🧩 Design Notes

Uses IMGUI (EditorGUILayout, ReorderableList) — not UI Toolkit.

All “drop zones” and drag hints have been removed for cleaner layout.

ReorderableList entries are spaced and styled for Unity 6 dark theme.

Collapsible “Tip” fields reduce clutter in the Scene List.

Window title: “Asset Opener”

Menu: Tools > Asset Opener

Minimum window size: 620 × 420

🔧 Internal Helpers Used

EditorSceneManager.OpenScene(path, OpenSceneMode)

AssetDatabase.GetAssetPath()

GlobalObjectId for sticky selection

Reflection on UnityEditor.ProjectBrowser

ReorderableList (from UnityEditorInternal)

🚦 Known Behavior / Limitations

Folder navigation depends on internal APIs — may change in future Unity versions.

“Sticky” GameObject ID only valid if object still exists in scene.

Scene open functions prompt user to save unsaved scenes.

List state (assets, folders, etc.) persists in the editor window’s serialized data.

📌 Instructions

When updating this tool:

Keep the architecture IMGUI-based with ReorderableList.

Do not reintroduce the drop zones unless explicitly requested.

Maintain separate data models:

Level Scenes → simple list of SceneAsset

Scene List → list of SceneNote with optional tip foldout

Folder List → DefaultAsset only

Asset List → AssetNote (Object + note)

Preserve sticky GameObject behavior and warning system.

New features should respect Unity 6 editor conventions.

Follow the established layout style and spacing (10 px between blocks).
##  Instructions
- When I ask to modify `AssetOpenerWindow.cs`, follow the behavior and feature definitions in this document.
- Keep existing architecture and naming consistent.
- Add or change code only in ways that respect these rules and Unity 6 APIs.
- Use IMGUI, not UI Toolkit.
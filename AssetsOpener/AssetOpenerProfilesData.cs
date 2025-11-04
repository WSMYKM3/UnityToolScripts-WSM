// File: `Assets/Editor/AssetOpenerProfilesData.cs`
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR
// Container ScriptableObject that holds multiple collaborator profiles.
// Stored at: `Assets/Editor/AssetOpenerProfilesData.asset`
public class AssetOpenerProfilesData : ScriptableObject
{
    [System.Serializable]
    public class Profile
    {
        public string name = "Default";
        public List<SceneAsset> levelScenes = new List<SceneAsset>();
        public List<AssetOpenerWindow.SceneNote> sceneNotes = new List<AssetOpenerWindow.SceneNote>();
        public List<DefaultAsset> folderList = new List<DefaultAsset>();
        public List<AssetOpenerWindow.AssetNote> assetNotes = new List<AssetOpenerWindow.AssetNote>();
        public GlobalObjectId sceneObjectId;
    }

    public List<Profile> profiles = new List<Profile>();
    public int lastSelectedProfileIndex = 0;
}
#endif
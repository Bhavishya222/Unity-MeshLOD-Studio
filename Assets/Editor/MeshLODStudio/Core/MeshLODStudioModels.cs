#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    public enum WorkspaceTab
    {
        Dashboard = 0,
        PrefabStudio = 1,
        MeshLODConverter = 2,
        LODGroupEditor = 3,
        BatchOptimizer = 4,
        SceneAutoSetup = 5,
        PresetManager = 6,
        Settings = 7
    }

    public enum NodeCompatibilityStatus
    {
        Compatible,
        AlreadyConvertedByStudio,
        HasExistingManualLODGroup,
        MeshLODNotEnabledOnImporter,
        TooFewTrianglesForMeshLOD,
        NonTriangleTopology,
        MissingMeshReference,
        SkinnedMeshUnsupported,
        NestedPrefabProtected,
        ReadOnlyAssetUnsupported
    }

    public enum ExtractionStrategy
    {
        StandaloneCompactedMeshAssets = 0,
        SharedMeshForceLodProperty = 1
    }

    public enum ThresholdInputMode
    {
        ScreenRelativePercentage = 0,
        WorldSpaceDistanceMeters = 1
    }

    public enum PresetCategory
    {
        Trees,
        Buildings,
        Rocks,
        Props,
        Vegetation,
        Architecture,
        Custom
    }

    [Serializable]
    public class LodLevelMetadata
    {
        public int lodIndex;
        public int totalIndexCount;
        public int totalTriangleCount;
        public int estimatedUniqueVertices;
        public float reductionPercentFromLod0;
    }

    [Serializable]
    public class PrefabNodeScanResult
    {
        public string hierarchyPath;
        public string gameObjectName;
        public int depth;
        public bool isRoot;
        public bool selectedForConversion = true;

        public bool hasMeshFilter;
        public bool hasMeshRenderer;
        public bool hasSkinnedMeshRenderer;
        public bool hasExistingLODGroup;
        public bool isPartOfAncestorLODGroup;
        public bool isNestedPrefabInstance;
        public string nestedPrefabAssetPath;

        public Mesh sharedMesh;
        public string meshAssetPath;
        public string meshAssetGuid;
        public int subMeshCount;
        public int vertexCount;
        public int baseTriangleCount;
        public int detectedMeshLodCount;
        public Bounds localBounds;
        public Bounds worldBoundsInPrefab;
        public Material[] sharedMaterials = Array.Empty<Material>();
        public string[] otherComponentTypes = Array.Empty<string>();

        public bool importerSupportsMeshLod;
        public bool importerGenerateMeshLodsEnabled;
        public int importerMaxMeshLod;

        public NodeCompatibilityStatus status;
        public string statusMessage;
        public List<LodLevelMetadata> lodLevels = new List<LodLevelMetadata>();

        // Per-node custom overrides (optional; falls back to active preset)
        public int maxLodLevelsToExtract = 4;
        public int lodLevelStride = 1; // 1 = every level, 2 = skip odd levels if desired
    }

    [Serializable]
    public class PrefabScanReport
    {
        public GameObject prefabAsset;
        public string assetPath;
        public string assetGuid;
        public string prefabName;
        public PrefabAssetType prefabAssetType;
        public bool isVariant;
        public bool isImmutablePackageAsset;
        public bool hasBackupRecord;
        public string scanTimestamp;

        public int totalGameObjects;
        public int totalMeshRenderers;
        public int compatibleNodeCount;
        public int existingLODGroupCount;
        public int unsupportedNodeCount;
        public int maxDetectedLodLevels;

        public List<PrefabNodeScanResult> allNodes = new List<PrefabNodeScanResult>();
        public List<PrefabNodeScanResult> meshNodes = new List<PrefabNodeScanResult>();
        public List<string> warnings = new List<string>();
        public string selectedPresetName = "Balanced";
    }

    [Serializable]
    public class LodTierConfig
    {
        public int tierIndex;
        public int sourceMeshLodIndex;
        [Range(0.001f, 1f)] public float screenRelativeTransitionHeight = 0.5f;
        public float targetDistanceMeters = 25f;
        [Range(0f, 1f)] public float crossFadeTransitionWidth = 0.1f;
        public bool castShadows = true;
        public bool receiveShadows = true;
    }

    [Serializable]
    public class MeshLODPreset
    {
        public string presetId = Guid.NewGuid().ToString("N");
        public string presetName = "Balanced";
        public PresetCategory category = PresetCategory.Props;
        public string description = "Moderate LOD transitions and balanced culling threshold.";
        public bool isBuiltIn;

        public ThresholdInputMode inputMode = ThresholdInputMode.ScreenRelativePercentage;
        public ExtractionStrategy extractionStrategy = ExtractionStrategy.StandaloneCompactedMeshAssets;
        public LODFadeMode fadeMode = LODFadeMode.None;
        public bool animateCrossFading;
        [Range(0f, 1f)] public float defaultCrossFadeWidth = 0.1f;
        [Range(0.001f, 0.25f)] public float cullScreenRelativeHeight = 0.02f;
        public float cullDistanceMeters = 120f;
        public int maxLodTiers = 4;
        public int sourceLodStride = 1;
        public bool disableShadowsOnLowestLOD = false;
        public bool recalculateBounds = true;

        // Camera reference parameters for Distance-to-ScreenHeight math
        public bool useOrthographicCamera;
        public float referenceCameraFov = 60f;
        public float referenceOrthoSize = 10f;

        // Default screen-relative transition heights for LOD0, LOD1, LOD2, LOD3, LOD4
        public List<float> tierScreenHeights = new List<float> { 0.45f, 0.20f, 0.08f, 0.03f };
        public List<float> tierDistancesMeters = new List<float> { 20f, 45f, 80f, 120f };

        public MeshLODPreset Clone(string newName)
        {
            var copy = new MeshLODPreset
            {
                presetId = Guid.NewGuid().ToString("N"),
                presetName = newName,
                category = this.category,
                description = this.description,
                isBuiltIn = false,
                inputMode = this.inputMode,
                extractionStrategy = this.extractionStrategy,
                fadeMode = this.fadeMode,
                animateCrossFading = this.animateCrossFading,
                defaultCrossFadeWidth = this.defaultCrossFadeWidth,
                cullScreenRelativeHeight = this.cullScreenRelativeHeight,
                cullDistanceMeters = this.cullDistanceMeters,
                maxLodTiers = this.maxLodTiers,
                sourceLodStride = this.sourceLodStride,
                disableShadowsOnLowestLOD = this.disableShadowsOnLowestLOD,
                recalculateBounds = this.recalculateBounds,
                useOrthographicCamera = this.useOrthographicCamera,
                referenceCameraFov = this.referenceCameraFov,
                referenceOrthoSize = this.referenceOrthoSize,
                tierScreenHeights = new List<float>(this.tierScreenHeights),
                tierDistancesMeters = new List<float>(this.tierDistancesMeters)
            };
            return copy;
        }
    }

    [Serializable]
    public class NodeBackupState
    {
        public string hierarchyPath;
        public string originalMeshAssetGuid;
        public string originalMeshName;
        public int originalForceMeshLod = -1;
        public float originalMeshLodSelectionBias = 0f;
        public bool hadLODGroupBefore;
        public List<string> createdChildRelativePaths = new List<string>();
        public List<string> generatedMeshAssetPaths = new List<string>();
    }

    [Serializable]
    public class PrefabBackupRecord
    {
        public string prefabAssetGuid;
        public string prefabAssetPath;
        public string prefabName;
        public string conversionTimestamp;
        public string presetUsed;
        public ExtractionStrategy strategyUsed;
        public List<NodeBackupState> convertedNodes = new List<NodeBackupState>();
    }

    [Serializable]
    public class OperationLogEntry
    {
        public string timestamp;
        public string operationType;
        public string targetName;
        public bool succeeded;
        public string summary;
    }

    [Serializable]
    public class ScenePrefabGroupEntry
    {
        public string sourcePrefabGuid;
        public string sourcePrefabPath;
        public GameObject sourcePrefabAsset;
        public string prefabName;
        public bool selectedForConversion = true;
        public string assignedPresetName = "Balanced";

        public int instanceCountInScene;
        public int variantInstanceCount;
        public int overriddenInstanceCount;
        public int compatibleMeshNodesInPrefab;
        public int maxMeshLodLevels;
        public bool alreadyConverted;
        public bool hasConflicts;
        public string statusSummary;
        public List<GameObject> sceneInstances = new List<GameObject>();
    }

    [Serializable]
    public class StandaloneSceneObjectEntry
    {
        public GameObject sceneObject;
        public string hierarchyPath;
        public Mesh sharedMesh;
        public int detectedMeshLodCount;
        public bool selectedForConversion = false; // Explicitly false by default for safety
        public string statusSummary;
    }

    public class MeshLODStudioSettings : ScriptableObject
    {
        public string generatedAssetsRootFolder = "Assets/MeshLODStudioGenerated";
        public ExtractionStrategy defaultExtractionStrategy = ExtractionStrategy.StandaloneCompactedMeshAssets;
        public ThresholdInputMode defaultThresholdMode = ThresholdInputMode.ScreenRelativePercentage;
        public float defaultReferenceCameraFov = 60f;
        public bool allowSkinnedMeshRendererConversion = false;
        public bool createBackupBeforeConversion = true;
        public bool verifyPrefabOverridesBeforeSave = true;
        public List<MeshLODPreset> presets = new List<MeshLODPreset>();
        public List<PrefabBackupRecord> backupRecords = new List<PrefabBackupRecord>();
        public List<OperationLogEntry> recentOperations = new List<OperationLogEntry>();

        private const string DefaultSettingsPath = "Assets/Editor/MeshLODStudio/MeshLODStudioSettings.asset";

        public static MeshLODStudioSettings GetOrCreate()
        {
            var settings = AssetDatabase.LoadAssetAtPath<MeshLODStudioSettings>(DefaultSettingsPath);
            if (settings == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:MeshLODStudioSettings");
                if (guids.Length > 0)
                {
                    settings = AssetDatabase.LoadAssetAtPath<MeshLODStudioSettings>(AssetDatabase.GUIDToAssetPath(guids[0]));
                }
            }

            if (settings == null)
            {
                EnsureFolder("Assets/Editor/MeshLODStudio");
                settings = CreateInstance<MeshLODStudioSettings>();
                settings.InitializeDefaultPresets();
                AssetDatabase.CreateAsset(settings, DefaultSettingsPath);
                AssetDatabase.SaveAssets();
            }
            else if (settings.presets == null || settings.presets.Count == 0)
            {
                settings.InitializeDefaultPresets();
                EditorUtility.SetDirty(settings);
            }

            return settings;
        }

        public void InitializeDefaultPresets()
        {
            presets.Clear();

            presets.Add(new MeshLODPreset
            {
                presetName = "High Quality",
                category = PresetCategory.Architecture,
                description = "Preserves maximum geometric detail to far distances; gentle culling at 1%.",
                isBuiltIn = true,
                inputMode = ThresholdInputMode.ScreenRelativePercentage,
                extractionStrategy = ExtractionStrategy.StandaloneCompactedMeshAssets,
                fadeMode = LODFadeMode.CrossFade,
                animateCrossFading = true,
                defaultCrossFadeWidth = 0.15f,
                cullScreenRelativeHeight = 0.01f,
                cullDistanceMeters = 180f,
                maxLodTiers = 4,
                sourceLodStride = 1,
                disableShadowsOnLowestLOD = false,
                tierScreenHeights = new List<float> { 0.35f, 0.15f, 0.05f, 0.01f },
                tierDistancesMeters = new List<float> { 30f, 65f, 115f, 180f }
            });

            presets.Add(new MeshLODPreset
            {
                presetName = "Balanced",
                category = PresetCategory.Props,
                description = "Standard production transitions suited for trees, rocks, and mid-ground props.",
                isBuiltIn = true,
                inputMode = ThresholdInputMode.ScreenRelativePercentage,
                extractionStrategy = ExtractionStrategy.StandaloneCompactedMeshAssets,
                fadeMode = LODFadeMode.None,
                animateCrossFading = false,
                defaultCrossFadeWidth = 0.10f,
                cullScreenRelativeHeight = 0.02f,
                cullDistanceMeters = 120f,
                maxLodTiers = 4,
                sourceLodStride = 1,
                disableShadowsOnLowestLOD = false,
                tierScreenHeights = new List<float> { 0.45f, 0.20f, 0.08f, 0.02f },
                tierDistancesMeters = new List<float> { 20f, 45f, 80f, 120f }
            });

            presets.Add(new MeshLODPreset
            {
                presetName = "Performance",
                category = PresetCategory.Vegetation,
                description = "Aggressively transitions to simplified Mesh LOD levels and culls small details early.",
                isBuiltIn = true,
                inputMode = ThresholdInputMode.ScreenRelativePercentage,
                extractionStrategy = ExtractionStrategy.StandaloneCompactedMeshAssets,
                fadeMode = LODFadeMode.None,
                animateCrossFading = false,
                defaultCrossFadeWidth = 0.05f,
                cullScreenRelativeHeight = 0.04f,
                cullDistanceMeters = 75f,
                maxLodTiers = 3,
                sourceLodStride = 2,
                disableShadowsOnLowestLOD = true,
                tierScreenHeights = new List<float> { 0.55f, 0.25f, 0.04f, 0.02f },
                tierDistancesMeters = new List<float> { 12f, 30f, 75f, 100f }
            });
        }

        public MeshLODPreset GetPresetByName(string name)
        {
            if (presets == null || presets.Count == 0) InitializeDefaultPresets();
            foreach (var p in presets)
            {
                if (string.Equals(p.presetName, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return presets[0];
        }

        public void RecordOperation(string type, string target, bool success, string summary)
        {
            recentOperations.Insert(0, new OperationLogEntry
            {
                timestamp = DateTime.Now.ToString("HH:mm:ss"),
                operationType = type,
                targetName = target,
                succeeded = success,
                summary = summary
            });
            if (recentOperations.Count > 50)
                recentOperations.RemoveAt(recentOperations.Count - 1);
            EditorUtility.SetDirty(this);
        }

        public static void EnsureFolder(string folderPath)
        {
            string normalized = folderPath.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(normalized)) return;

            string[] parts = normalized.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
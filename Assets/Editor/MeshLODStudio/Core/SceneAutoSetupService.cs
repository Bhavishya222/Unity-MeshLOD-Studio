#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Scans the active scene to group hundreds of prefab instances into their unique source
    /// Prefab assets (e.g., 300 Oak Trees -> 1 Oak_Tree.prefab) and identifies standalone scene objects.
    /// Never modifies the scene automatically; requires explicit user confirmation.
    /// </summary>
    public static class SceneAutoSetupService
    {
        public class SceneScanSummary
        {
            public string sceneName;
            public int totalSceneRoots;
            public int totalPrefabInstances;
            public int uniquePrefabAssetCount;
            public int convertibleUniquePrefabs;
            public int standaloneMeshObjectsCount;
            public List<ScenePrefabGroupEntry> uniquePrefabGroups = new List<ScenePrefabGroupEntry>();
            public List<StandaloneSceneObjectEntry> standaloneObjects = new List<StandaloneSceneObjectEntry>();
        }

        public static SceneScanSummary ScanActiveScene(MeshLODStudioSettings settings = null)
        {
            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();
            Scene activeScene = SceneManager.GetActiveScene();

            var summary = new SceneScanSummary
            {
                sceneName = activeScene.IsValid() ? activeScene.name : "Untitled"
            };

            if (!activeScene.IsValid() || !activeScene.isLoaded)
                return summary;

            var prefabGroupMap = new Dictionary<string, ScenePrefabGroupEntry>();
            var visitedInstanceRoots = new HashSet<GameObject>();

            GameObject[] roots = activeScene.GetRootGameObjects();
            summary.totalSceneRoots = roots.Length;

            foreach (GameObject root in roots)
            {
                TraverseSceneHierarchy(
                    root.transform,
                    root.transform,
                    prefabGroupMap,
                    visitedInstanceRoots,
                    summary,
                    settings);
            }

            foreach (var kvp in prefabGroupMap)
            {
                summary.uniquePrefabGroups.Add(kvp.Value);
                if (kvp.Value.compatibleMeshNodesInPrefab > 0 && !kvp.Value.alreadyConverted)
                    summary.convertibleUniquePrefabs++;
            }

            summary.uniquePrefabAssetCount = summary.uniquePrefabGroups.Count;
            summary.standaloneMeshObjectsCount = summary.standaloneObjects.Count;
            return summary;
        }

        private static void TraverseSceneHierarchy(
            Transform current,
            Transform sceneRoot,
            Dictionary<string, ScenePrefabGroupEntry> prefabGroupMap,
            HashSet<GameObject> visitedInstanceRoots,
            SceneScanSummary summary,
            MeshLODStudioSettings settings)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(current.gameObject))
            {
                GameObject instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(current.gameObject);
                if (instanceRoot != null && !visitedInstanceRoots.Contains(instanceRoot))
                {
                    visitedInstanceRoots.Add(instanceRoot);
                    summary.totalPrefabInstances++;

                    string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instanceRoot);
                    string prefabGuid = string.IsNullOrEmpty(prefabPath) ? string.Empty : AssetDatabase.AssetPathToGUID(prefabPath);
                    GameObject prefabAsset = string.IsNullOrEmpty(prefabPath)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

                    if (prefabAsset != null && !string.IsNullOrEmpty(prefabGuid))
                    {
                        if (!prefabGroupMap.TryGetValue(prefabGuid, out ScenePrefabGroupEntry group))
                        {
                            PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefabAsset, settings);
                            bool converted = scan.existingLODGroupCount > 0 && scan.compatibleNodeCount == 0;

                            group = new ScenePrefabGroupEntry
                            {
                                sourcePrefabGuid = prefabGuid,
                                sourcePrefabPath = prefabPath,
                                sourcePrefabAsset = prefabAsset,
                                prefabName = prefabAsset.name,
                                compatibleMeshNodesInPrefab = scan.compatibleNodeCount,
                                maxMeshLodLevels = scan.maxDetectedLodLevels,
                                alreadyConverted = converted,
                                selectedForConversion = scan.compatibleNodeCount > 0 && !scan.isImmutablePackageAsset,
                                statusSummary = scan.compatibleNodeCount > 0
                                    ? $"{scan.compatibleNodeCount} convertible mesh node(s) ({scan.maxDetectedLodLevels} LODs)"
                                    : (converted ? "Already has LODGroup" : "No Mesh LODs (>1) detected")
                            };
                            prefabGroupMap[prefabGuid] = group;
                        }

                        group.instanceCountInScene++;
                        group.sceneInstances.Add(instanceRoot);

                        if (PrefabUtility.GetPrefabAssetType(instanceRoot) == PrefabAssetType.Variant)
                            group.variantInstanceCount++;

                        if (PrefabUtility.HasPrefabInstanceAnyOverrides(instanceRoot, false))
                            group.overriddenInstanceCount++;
                    }
                }
                return;
            }

            MeshFilter mf = current.GetComponent<MeshFilter>();
            MeshRenderer mr = current.GetComponent<MeshRenderer>();
            if (mf != null && mr != null && mf.sharedMesh != null && !current.name.Contains("_MeshLOD_Child_LOD"))
            {
                int lods = mf.sharedMesh.lodCount;
                if (lods > 1 && current.GetComponent<LODGroup>() == null)
                {
                    summary.standaloneObjects.Add(new StandaloneSceneObjectEntry
                    {
                        sceneObject = current.gameObject,
                        hierarchyPath = AnimationUtility.CalculateTransformPath(current, sceneRoot),
                        sharedMesh = mf.sharedMesh,
                        detectedMeshLodCount = lods,
                        selectedForConversion = false,
                        statusSummary = $"Standalone Scene Object ({lods} Mesh LODs)"
                    });
                }
            }

            for (int i = 0; i < current.childCount; i++)
            {
                TraverseSceneHierarchy(
                    current.GetChild(i),
                    sceneRoot,
                    prefabGroupMap,
                    visitedInstanceRoots,
                    summary,
                    settings);
            }
        }

        public static bool ConvertStandaloneSceneObject(
            StandaloneSceneObjectEntry entry,
            MeshLODPreset preset,
            MeshLODStudioSettings settings,
            out string message)
        {
            message = string.Empty;
            if (entry == null || entry.sceneObject == null)
            {
                message = "Scene object is null.";
                return false;
            }

            GameObject targetGo = entry.sceneObject;
            MeshFilter mf = targetGo.GetComponent<MeshFilter>();
            MeshRenderer mr = targetGo.GetComponent<MeshRenderer>();
            if (mf == null || mr == null || mf.sharedMesh == null || mf.sharedMesh.lodCount <= 1)
            {
                message = $"'{targetGo.name}' does not have valid Mesh LODs.";
                return false;
            }

            Mesh sourceMesh = mf.sharedMesh;
            int totalLods = Mathf.Min(preset.maxLodTiers, sourceMesh.lodCount);
            float[] heights = new float[totalLods];

            for (int i = 0; i < totalLods; i++)
            {
                heights[i] = i == totalLods - 1
                    ? preset.cullScreenRelativeHeight
                    : (i < preset.tierScreenHeights.Count ? preset.tierScreenHeights[i] : Mathf.Pow(0.45f, i + 1));
            }
            DistanceThresholdCalculator.EnforceStrictlyDescendingThresholds(heights);

            Undo.RecordObject(mr, "Convert Standalone Object to LODGroup");
            mr.forceMeshLod = 0;

            var lods = new LOD[totalLods];
            lods[0] = new LOD(heights[0], new Renderer[] { mr });

            for (int tier = 1; tier < totalLods; tier++)
            {
                var childGo = new GameObject($"{targetGo.name}_MeshLOD_Child_LOD{tier}");
                Undo.RegisterCreatedObjectUndo(childGo, "Create Standalone LOD Child");
                childGo.transform.SetParent(targetGo.transform, false);

                MeshFilter cMf = childGo.AddComponent<MeshFilter>();
                MeshRenderer cMr = childGo.AddComponent<MeshRenderer>();
                cMf.sharedMesh = sourceMesh;
                cMr.sharedMaterials = mr.sharedMaterials;
                cMr.forceMeshLod = (short)tier;

                lods[tier] = new LOD(heights[tier], new Renderer[] { cMr });
            }

            LODGroup group = Undo.AddComponent<LODGroup>(targetGo);
            group.fadeMode = preset.fadeMode;
            group.animateCrossFading = preset.animateCrossFading;
            group.SetLODs(lods);
            group.RecalculateBounds();

            EditorSceneManager.MarkSceneDirty(targetGo.scene);
            message = $"Converted standalone scene object '{targetGo.name}' ({totalLods} LODs).";
            return true;
        }
    }
}
#endif
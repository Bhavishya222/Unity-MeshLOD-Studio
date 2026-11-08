#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Converts Unity 6.3 generated Mesh LODs (plus optional synthesized far-distance LOD levels up to 8 tiers)
    /// into native Unity LODGroups on the source prefab asset.
    /// </summary>
    public static class PrefabConversionService
    {
        public static bool ConvertPrefabAsset(
            PrefabScanReport report,
            MeshLODPreset preset,
            MeshLODStudioSettings settings,
            out string resultMessage)
        {
            resultMessage = string.Empty;
            if (report == null || report.prefabAsset == null)
            {
                resultMessage = "Invalid prefab scan report.";
                return false;
            }

            if (report.isImmutablePackageAsset)
            {
                resultMessage = $"Cannot convert '{report.prefabName}': asset is inside a read-only package.";
                return false;
            }

            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();
            if (preset == null) preset = settings.GetPresetByName(report.selectedPresetName);

            var nodesToConvert = new List<PrefabNodeScanResult>();
            foreach (PrefabNodeScanResult node in report.meshNodes)
            {
                if (node.selectedForConversion && node.status == NodeCompatibilityStatus.Compatible)
                {
                    nodesToConvert.Add(node);
                }
            }

            if (nodesToConvert.Count == 0)
            {
                resultMessage = $"No compatible and selected nodes found in '{report.prefabName}'.";
                return false;
            }

            string prefabPath = report.assetPath;
            string prefabGuid = report.assetGuid;
            string generatedFolder = $"{settings.generatedAssetsRootFolder}/{report.prefabName}_{prefabGuid.Substring(0, Mathf.Min(8, prefabGuid.Length))}";

            bool requiresAssetFolder = preset.extractionStrategy == ExtractionStrategy.StandaloneCompactedMeshAssets || preset.synthesizeExtraFarLods;
            if (requiresAssetFolder)
            {
                MeshLODStudioSettings.EnsureFolder(generatedFolder);
            }

            var backupRecord = new PrefabBackupRecord
            {
                prefabAssetGuid = prefabGuid,
                prefabAssetPath = prefabPath,
                prefabName = report.prefabName,
                conversionTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                presetUsed = preset.presetName,
                strategyUsed = preset.extractionStrategy
            };

            GameObject prefabRoot = null;
            int convertedCount = 0;

            try
            {
                AssetDatabase.StartAssetEditing();
                prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);

                foreach (PrefabNodeScanResult nodeInfo in nodesToConvert)
                {
                    Transform targetTransform = PrefabScannerAndValidator.FindTransformByRelativePath(
                        prefabRoot.transform,
                        nodeInfo.hierarchyPath);

                    if (targetTransform == null) continue;

                    MeshFilter mf = targetTransform.GetComponent<MeshFilter>();
                    MeshRenderer mr = targetTransform.GetComponent<MeshRenderer>();
                    if (mf == null || mr == null || mf.sharedMesh == null) continue;

                    Mesh sourceMesh = mf.sharedMesh;
                    int nativeMeshLods = sourceMesh.lodCount;
                    if (nativeMeshLods <= 1) continue;

                    var nodeBackup = new NodeBackupState
                    {
                        hierarchyPath = nodeInfo.hierarchyPath,
                        originalMeshAssetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sourceMesh)),
                        originalMeshName = sourceMesh.name,
                        originalForceMeshLod = mr.forceMeshLod,
                        originalMeshLodSelectionBias = mr.meshLodSelectionBias,
                        hadLODGroupBefore = targetTransform.GetComponent<LODGroup>() != null
                    };

                    int maxReachableLods = preset.synthesizeExtraFarLods
                        ? Mathf.Max(nativeMeshLods, nodeInfo.maxAvailableLodCount)
                        : nativeMeshLods;

                    int requestedMaxTiers = Mathf.Clamp(
                        Mathf.Min(preset.maxLodTiers, nodeInfo.maxLodLevelsToExtract),
                        2,
                        8);

                    List<int> selectedSourceLodIndices = SelectLodIndicesForConversion(
                        maxReachableLods,
                        requestedMaxTiers,
                        Mathf.Max(1, preset.sourceLodStride));

                    int tierCount = selectedSourceLodIndices.Count;
                    float[] transitionHeights = ComputeTransitionHeightsForNode(
                        mr.bounds,
                        tierCount,
                        preset);

                    var lodsArray = new LOD[tierCount];

                    // Tier 0 (LOD0): Keep on the original MeshFilter + MeshRenderer
                    mr.forceMeshLod = 0;
                    lodsArray[0] = new LOD(transitionHeights[0], new Renderer[] { mr })
                    {
                        fadeTransitionWidth = preset.defaultCrossFadeWidth
                    };

                    // Tiers 1..N (up to LOD7)
                    for (int tier = 1; tier < tierCount; tier++)
                    {
                        int srcLodIndex = selectedSourceLodIndices[tier];
                        bool isSynthesizedFarTier = srcLodIndex >= nativeMeshLods;
                        string childName = $"{targetTransform.name}_MeshLOD_Child_LOD{tier}";

                        Transform existingChild = targetTransform.Find(childName);
                        if (existingChild != null)
                            UnityEngine.Object.DestroyImmediate(existingChild.gameObject);

                        var childGo = new GameObject(childName);
                        childGo.layer = targetTransform.gameObject.layer;
                        childGo.tag = targetTransform.gameObject.tag;
                        GameObjectUtility.SetStaticEditorFlags(
                            childGo,
                            GameObjectUtility.GetStaticEditorFlags(targetTransform.gameObject));

                        Transform childTr = childGo.transform;
                        childTr.SetParent(targetTransform, false);
                        childTr.localPosition = Vector3.zero;
                        childTr.localRotation = Quaternion.identity;
                        childTr.localScale = Vector3.one;

                        MeshFilter childMf = childGo.AddComponent<MeshFilter>();
                        MeshRenderer childMr = childGo.AddComponent<MeshRenderer>();

                        if (preset.extractionStrategy == ExtractionStrategy.StandaloneCompactedMeshAssets || isSynthesizedFarTier)
                        {
                            Mesh extractedMesh = MeshLODDetectorAndExtractor.ExtractOrSynthesizeLodMesh(
                                sourceMesh,
                                srcLodIndex,
                                preset.minTrianglesFloor,
                                true);

                            string safeNodeName = SanitizeFileName(targetTransform.name);
                            string tag = isSynthesizedFarTier ? $"FarLOD{srcLodIndex}" : $"Src{srcLodIndex}";
                            string meshAssetPath = $"{generatedFolder}/{safeNodeName}_{SanitizeFileName(sourceMesh.name)}_LOD{tier}_{tag}.asset";

                            if (AssetDatabase.LoadMainAssetAtPath(meshAssetPath) != null)
                                AssetDatabase.DeleteAsset(meshAssetPath);

                            AssetDatabase.CreateAsset(extractedMesh, meshAssetPath);
                            childMf.sharedMesh = extractedMesh;
                            childMr.forceMeshLod = -1;
                            nodeBackup.generatedMeshAssetPaths.Add(meshAssetPath);
                        }
                        else
                        {
                            childMf.sharedMesh = sourceMesh;
                            childMr.forceMeshLod = (short)srcLodIndex;
                        }

                        CopyRendererProperties(mr, childMr, tier == tierCount - 1 && preset.disableShadowsOnLowestLOD);

                        lodsArray[tier] = new LOD(transitionHeights[tier], new Renderer[] { childMr })
                        {
                            fadeTransitionWidth = preset.defaultCrossFadeWidth
                        };

                        string childRelPath = AnimationUtility.CalculateTransformPath(childTr, prefabRoot.transform);
                        nodeBackup.createdChildRelativePaths.Add(childRelPath);
                    }

                    LODGroup lodGroup = targetTransform.GetComponent<LODGroup>();
                    if (lodGroup == null)
                        lodGroup = targetTransform.gameObject.AddComponent<LODGroup>();

                    lodGroup.fadeMode = preset.fadeMode;
                    lodGroup.animateCrossFading = preset.animateCrossFading;
                    lodGroup.SetLODs(lodsArray);
                    if (preset.recalculateBounds)
                        lodGroup.RecalculateBounds();

                    backupRecord.convertedNodes.Add(nodeBackup);
                    convertedCount++;
                }
            }
            catch (Exception ex)
            {
                resultMessage = $"Conversion failed on '{report.prefabName}': {ex.Message}";
                Debug.LogException(ex);
                return false;
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            try
            {
                if (convertedCount > 0 && prefabRoot != null)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath, out bool savedSuccessfully);
                    if (!savedSuccessfully)
                    {
                        resultMessage = $"Failed to save modified prefab asset at '{prefabPath}'.";
                        return false;
                    }
                }
            }
            finally
            {
                if (prefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            if (settings.createBackupBeforeConversion && backupRecord.convertedNodes.Count > 0)
            {
                BackupAndRollbackManager.SaveOrUpdateBackupRecord(backupRecord, settings);
            }

            settings.RecordOperation(
                "Convert Prefab",
                report.prefabName,
                true,
                $"Converted {convertedCount} object(s) using '{preset.presetName}' preset (up to {preset.maxLodTiers} tiers).");

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            resultMessage = $"Successfully converted {convertedCount} object(s) in '{report.prefabName}' (up to {preset.maxLodTiers} LOD tiers).";
            return true;
        }

        private static List<int> SelectLodIndicesForConversion(int availableLodCount, int maxTiers, int stride)
        {
            var indices = new List<int> { 0 };
            int currentLod = stride;

            while (indices.Count < maxTiers && currentLod < availableLodCount)
            {
                indices.Add(currentLod);
                currentLod += stride;
            }

            if (indices.Count == 1 && availableLodCount > 1)
            {
                indices.Add(availableLodCount - 1);
            }

            return indices;
        }

        private static float[] ComputeTransitionHeightsForNode(Bounds worldBounds, int tierCount, MeshLODPreset preset)
        {
            float[] heights = new float[tierCount];
            float objSize = DistanceThresholdCalculator.GetObjectWorldSize(worldBounds);

            for (int i = 0; i < tierCount; i++)
            {
                if (i == tierCount - 1)
                {
                    heights[i] = preset.inputMode == ThresholdInputMode.WorldSpaceDistanceMeters
                        ? DistanceThresholdCalculator.DistanceToScreenRelativeHeight(
                            preset.cullDistanceMeters,
                            objSize,
                            preset.referenceCameraFov,
                            preset.useOrthographicCamera,
                            preset.referenceOrthoSize)
                        : preset.cullScreenRelativeHeight;
                }
                else
                {
                    if (preset.inputMode == ThresholdInputMode.WorldSpaceDistanceMeters)
                    {
                        float dist = i < preset.tierDistancesMeters.Count
                            ? preset.tierDistancesMeters[i]
                            : (i + 1) * 25f;
                        heights[i] = DistanceThresholdCalculator.DistanceToScreenRelativeHeight(
                            dist,
                            objSize,
                            preset.referenceCameraFov,
                            preset.useOrthographicCamera,
                            preset.referenceOrthoSize);
                    }
                    else
                    {
                        heights[i] = i < preset.tierScreenHeights.Count
                            ? preset.tierScreenHeights[i]
                            : Mathf.Pow(0.55f, i + 1);
                    }
                }
            }

            DistanceThresholdCalculator.EnforceStrictlyDescendingThresholds(heights);
            return heights;
        }

        private static void CopyRendererProperties(MeshRenderer source, MeshRenderer destination, bool disableShadows)
        {
            destination.sharedMaterials = source.sharedMaterials;
            destination.shadowCastingMode = disableShadows ? ShadowCastingMode.Off : source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.probeAnchor = source.probeAnchor;
            destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.renderingLayerMask = source.renderingLayerMask;
            destination.rendererPriority = source.rendererPriority;
            destination.staticShadowCaster = source.staticShadowCaster;
        }

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Mesh";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw.Replace(' ', '_');
        }
    }
}
#endif

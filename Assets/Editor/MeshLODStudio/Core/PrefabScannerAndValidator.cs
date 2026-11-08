#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    public static class PrefabScannerAndValidator
    {
        public static PrefabScanReport ScanPrefabAsset(GameObject prefabAsset, MeshLODStudioSettings settings = null)
        {
            if (prefabAsset == null) return null;
            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();

            string assetPath = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(assetPath))
            {
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset);
                if (source != null)
                {
                    prefabAsset = source;
                    assetPath = AssetDatabase.GetAssetPath(prefabAsset);
                }
            }

            string assetGuid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(prefabAsset);

            var report = new PrefabScanReport
            {
                prefabAsset = prefabAsset,
                assetPath = assetPath,
                assetGuid = assetGuid,
                prefabName = prefabAsset.name,
                prefabAssetType = prefabType,
                isVariant = prefabType == PrefabAssetType.Variant,
                isImmutablePackageAsset = !string.IsNullOrEmpty(assetPath) && !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase),
                hasBackupRecord = BackupAndRollbackManager.HasBackup(assetGuid, settings),
                scanTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            if (report.isImmutablePackageAsset)
            {
                report.warnings.Add($"Prefab '{assetPath}' resides outside 'Assets/' (read-only package) and cannot be modified in place.");
            }

            var renderersInExistingLodGroups = new HashSet<Renderer>();
            LODGroup[] existingGroups = prefabAsset.GetComponentsInChildren<LODGroup>(true);
            report.existingLODGroupCount = existingGroups.Length;

            foreach (LODGroup group in existingGroups)
            {
                LOD[] lods = group.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    if (lods[i].renderers == null) continue;
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r != null) renderersInExistingLodGroups.Add(r);
                    }
                }
            }

            ScanTransformRecursive(
                prefabAsset.transform,
                prefabAsset.transform,
                0,
                report,
                renderersInExistingLodGroups,
                settings);

            return report;
        }

        private static void ScanTransformRecursive(
            Transform current,
            Transform root,
            int depth,
            PrefabScanReport report,
            HashSet<Renderer> renderersInExistingLodGroups,
            MeshLODStudioSettings settings)
        {
            if (current.name.Contains("_MeshLOD_Child_LOD"))
                return;

            report.totalGameObjects++;
            string relPath = AnimationUtility.CalculateTransformPath(current, root);

            MeshFilter mf = current.GetComponent<MeshFilter>();
            MeshRenderer mr = current.GetComponent<MeshRenderer>();
            SkinnedMeshRenderer smr = current.GetComponent<SkinnedMeshRenderer>();
            LODGroup lodGroup = current.GetComponent<LODGroup>();

            if (mr != null) report.totalMeshRenderers++;

            bool isNestedPrefab = current != root && PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject);
            string nestedPath = isNestedPrefab ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current.gameObject) : string.Empty;

            Component[] allComps = current.GetComponents<Component>();
            var otherComps = new List<string>();
            foreach (Component c in allComps)
            {
                if (c == null)
                {
                    report.warnings.Add($"Missing script detected on '{relPath}'.");
                    continue;
                }
                if (c is Transform || c is MeshFilter || c is MeshRenderer || c is SkinnedMeshRenderer || c is LODGroup)
                    continue;
                otherComps.Add(c.GetType().Name);
            }

            var node = new PrefabNodeScanResult
            {
                hierarchyPath = string.IsNullOrEmpty(relPath) ? "<Root>" : relPath,
                gameObjectName = current.name,
                depth = depth,
                isRoot = current == root,
                hasMeshFilter = mf != null,
                hasMeshRenderer = mr != null,
                hasSkinnedMeshRenderer = smr != null,
                hasExistingLODGroup = lodGroup != null,
                isPartOfAncestorLODGroup = mr != null && renderersInExistingLodGroups.Contains(mr) && lodGroup == null,
                isNestedPrefabInstance = isNestedPrefab,
                nestedPrefabAssetPath = nestedPath,
                otherComponentTypes = otherComps.ToArray()
            };

            report.allNodes.Add(node);

            if (mf != null || mr != null || smr != null)
            {
                EvaluateMeshNode(node, mf, mr, smr, report, settings);
                report.meshNodes.Add(node);
            }

            for (int i = 0; i < current.childCount; i++)
            {
                ScanTransformRecursive(
                    current.GetChild(i),
                    root,
                    depth + 1,
                    report,
                    renderersInExistingLodGroups,
                    settings);
            }
        }

        private static void EvaluateMeshNode(
            PrefabNodeScanResult node,
            MeshFilter mf,
            MeshRenderer mr,
            SkinnedMeshRenderer smr,
            PrefabScanReport report,
            MeshLODStudioSettings settings)
        {
            if (report.isImmutablePackageAsset)
            {
                node.status = NodeCompatibilityStatus.ReadOnlyAssetUnsupported;
                node.statusMessage = "Read-only package prefab.";
                node.selectedForConversion = false;
                report.unsupportedNodeCount++;
                return;
            }

            if (smr != null && !settings.allowSkinnedMeshRendererConversion)
            {
                node.sharedMesh = smr.sharedMesh;
                node.status = NodeCompatibilityStatus.SkinnedMeshUnsupported;
                node.statusMessage = "SkinnedMeshRenderer: Unity 6.3 Mesh LOD ignores skin weights and deforms LOD0 at runtime.";
                node.selectedForConversion = false;
                report.unsupportedNodeCount++;
                return;
            }

            if (mf == null || mr == null || mf.sharedMesh == null)
            {
                node.status = NodeCompatibilityStatus.MissingMeshReference;
                node.statusMessage = "Missing MeshFilter, MeshRenderer, or sharedMesh reference.";
                node.selectedForConversion = false;
                report.unsupportedNodeCount++;
                report.warnings.Add($"Node '{node.hierarchyPath}' has a missing Mesh or Renderer reference.");
                return;
            }

            Mesh mesh = mf.sharedMesh;
            node.sharedMesh = mesh;
            node.meshAssetPath = AssetDatabase.GetAssetPath(mesh);
            node.meshAssetGuid = string.IsNullOrEmpty(node.meshAssetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(node.meshAssetPath);
            node.subMeshCount = mesh.subMeshCount;
            node.vertexCount = mesh.vertexCount;
            node.baseTriangleCount = MeshLODDetectorAndExtractor.GetBaseTriangleCount(mesh);
            node.detectedMeshLodCount = mesh.lodCount;
            node.localBounds = mesh.bounds;
            node.worldBoundsInPrefab = mr.bounds;
            node.sharedMaterials = mr.sharedMaterials;

            if (!string.IsNullOrEmpty(node.meshAssetPath))
            {
                ModelImporter importer = AssetImporter.GetAtPath(node.meshAssetPath) as ModelImporter;
                if (importer != null)
                {
                    node.importerSupportsMeshLod = true;
                    node.importerGenerateMeshLodsEnabled = importer.generateMeshLods;
                    node.importerMaxMeshLod = importer.maximumMeshLod;
                }
            }

            node.lodLevels = MeshLODDetectorAndExtractor.AnalyzeMeshLodLevels(mesh, 8, true, 6);
            node.maxAvailableLodCount = node.lodLevels.Count;

            if (node.detectedMeshLodCount > report.maxDetectedLodLevels)
                report.maxDetectedLodLevels = node.detectedMeshLodCount;

            if (node.hasExistingLODGroup)
            {
                bool studioConverted = BackupAndRollbackManager.IsNodeTrackedInBackup(report.assetGuid, node.hierarchyPath, settings);
                node.status = studioConverted
                    ? NodeCompatibilityStatus.AlreadyConvertedByStudio
                    : NodeCompatibilityStatus.HasExistingManualLODGroup;
                node.statusMessage = studioConverted
                    ? $"Converted by MeshLOD Studio ({node.detectedMeshLodCount} native Mesh LODs)."
                    : "Already has a manual LODGroup component.";
                node.selectedForConversion = false;
                return;
            }

            if (node.isPartOfAncestorLODGroup)
            {
                node.status = NodeCompatibilityStatus.HasExistingManualLODGroup;
                node.statusMessage = "Already managed by an ancestor LODGroup.";
                node.selectedForConversion = false;
                return;
            }

            if (MeshLODDetectorAndExtractor.HasNonTriangleTopology(mesh))
            {
                node.status = NodeCompatibilityStatus.NonTriangleTopology;
                node.statusMessage = "Non-triangle topology detected. Mesh LOD requires triangle meshes.";
                node.selectedForConversion = false;
                report.unsupportedNodeCount++;
                return;
            }

            if (node.detectedMeshLodCount <= 1)
            {
                if (node.baseTriangleCount < 256)
                {
                    node.status = NodeCompatibilityStatus.TooFewTrianglesForMeshLOD;
                    node.statusMessage = $"Only {node.baseTriangleCount} triangles (< 256 minimum required by Unity Mesh LOD generator).";
                }
                else
                {
                    node.status = NodeCompatibilityStatus.MeshLODNotEnabledOnImporter;
                    node.statusMessage = "Mesh has 1 native LOD level. Enable 'Generate Mesh LODs' on ModelImporter.";
                }
                node.selectedForConversion = false;
                report.unsupportedNodeCount++;
                return;
            }

            node.status = NodeCompatibilityStatus.Compatible;
            node.statusMessage = $"Ready ({node.detectedMeshLodCount} native Mesh LODs, extendable up to {node.maxAvailableLodCount} LODs with far decimation).";
            node.selectedForConversion = true;
            node.maxLodLevelsToExtract = 8;
            report.compatibleNodeCount++;
        }

        public static Transform FindTransformByRelativePath(Transform root, string relativePath)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(relativePath) || relativePath == "<Root>")
                return root;
            return root.Find(relativePath);
        }
    }
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Records pre-conversion prefab structure, original mesh GUIDs, Renderer.forceMeshLod values,
    /// and generated child/mesh assets so any converted prefab can be cleanly rolled back
    /// while preserving the prefab's GUID and scene instance overrides.
    /// </summary>
    public static class BackupAndRollbackManager
    {
        public static bool HasBackup(string prefabGuid, MeshLODStudioSettings settings = null)
        {
            if (string.IsNullOrEmpty(prefabGuid)) return false;
            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();
            return GetBackupRecord(prefabGuid, settings) != null;
        }

        public static PrefabBackupRecord GetBackupRecord(string prefabGuid, MeshLODStudioSettings settings = null)
        {
            if (string.IsNullOrEmpty(prefabGuid)) return null;
            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();

            foreach (PrefabBackupRecord rec in settings.backupRecords)
            {
                if (rec.prefabAssetGuid == prefabGuid)
                    return rec;
            }
            return null;
        }

        public static bool IsNodeTrackedInBackup(string prefabGuid, string hierarchyPath, MeshLODStudioSettings settings = null)
        {
            PrefabBackupRecord rec = GetBackupRecord(prefabGuid, settings);
            if (rec == null) return false;
            foreach (NodeBackupState n in rec.convertedNodes)
            {
                if (n.hierarchyPath == hierarchyPath) return true;
            }
            return false;
        }

        public static void SaveOrUpdateBackupRecord(PrefabBackupRecord newRecord, MeshLODStudioSettings settings = null)
        {
            if (newRecord == null || string.IsNullOrEmpty(newRecord.prefabAssetGuid)) return;
            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();

            Undo.RecordObject(settings, "Save MeshLOD Backup Record");
            for (int i = settings.backupRecords.Count - 1; i >= 0; i--)
            {
                if (settings.backupRecords[i].prefabAssetGuid == newRecord.prefabAssetGuid)
                    settings.backupRecords.RemoveAt(i);
            }

            settings.backupRecords.Add(newRecord);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        public static bool RollbackPrefab(GameObject prefabAsset, out string statusMessage, MeshLODStudioSettings settings = null)
        {
            statusMessage = string.Empty;
            if (prefabAsset == null)
            {
                statusMessage = "No prefab selected for rollback.";
                return false;
            }

            if (settings == null) settings = MeshLODStudioSettings.GetOrCreate();
            string assetPath = AssetDatabase.GetAssetPath(prefabAsset);
            string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

            PrefabBackupRecord record = GetBackupRecord(assetGuid, settings);
            if (record == null)
            {
                statusMessage = $"No MeshLOD Studio backup record found for '{prefabAsset.name}'.";
                return false;
            }

            GameObject root = null;
            var assetsToDelete = new List<string>();

            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);

                foreach (NodeBackupState nodeState in record.convertedNodes)
                {
                    Transform target = PrefabScannerAndValidator.FindTransformByRelativePath(root.transform, nodeState.hierarchyPath);
                    if (target == null) continue;

                    foreach (string childRelPath in nodeState.createdChildRelativePaths)
                    {
                        Transform child = root.transform.Find(childRelPath);
                        if (child == null && target != null)
                        {
                            child = target.Find(childRelPath);
                        }
                        if (child != null)
                        {
                            UnityEngine.Object.DestroyImmediate(child.gameObject);
                        }
                    }

                    for (int c = target.childCount - 1; c >= 0; c--)
                    {
                        Transform ch = target.GetChild(c);
                        if (ch.name.Contains("_MeshLOD_Child_LOD"))
                        {
                            UnityEngine.Object.DestroyImmediate(ch.gameObject);
                        }
                    }

                    if (!nodeState.hadLODGroupBefore)
                    {
                        LODGroup lodGroup = target.GetComponent<LODGroup>();
                        if (lodGroup != null)
                        {
                            UnityEngine.Object.DestroyImmediate(lodGroup);
                        }
                    }

                    MeshFilter mf = target.GetComponent<MeshFilter>();
                    MeshRenderer mr = target.GetComponent<MeshRenderer>();

                    if (mf != null && !string.IsNullOrEmpty(nodeState.originalMeshAssetGuid))
                    {
                        string origMeshPath = AssetDatabase.GUIDToAssetPath(nodeState.originalMeshAssetGuid);
                        if (!string.IsNullOrEmpty(origMeshPath))
                        {
                            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(origMeshPath);
                            foreach (UnityEngine.Object obj in allSubAssets)
                            {
                                if (obj is Mesh m && (string.IsNullOrEmpty(nodeState.originalMeshName) || m.name == nodeState.originalMeshName))
                                {
                                    mf.sharedMesh = m;
                                    break;
                                }
                            }
                        }
                    }

                    if (mr != null)
                    {
                        mr.forceMeshLod = (short)nodeState.originalForceMeshLod;
                        mr.meshLodSelectionBias = nodeState.originalMeshLodSelectionBias;
                    }

                    foreach (string genAsset in nodeState.generatedMeshAssetPaths)
                    {
                        if (!string.IsNullOrEmpty(genAsset))
                            assetsToDelete.Add(genAsset);
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(root, assetPath, out bool saved);
                if (!saved)
                {
                    statusMessage = $"Failed to save rolled-back prefab at '{assetPath}'.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"Rollback error: {ex.Message}";
                Debug.LogException(ex);
                return false;
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }

            foreach (string genPath in assetsToDelete)
            {
                if (genPath.StartsWith(settings.generatedAssetsRootFolder, StringComparison.OrdinalIgnoreCase) &&
                    AssetDatabase.LoadMainAssetAtPath(genPath) != null)
                {
                    AssetDatabase.DeleteAsset(genPath);
                }
            }

            Undo.RecordObject(settings, "Remove MeshLOD Backup Record");
            settings.backupRecords.Remove(record);
            settings.RecordOperation("Rollback", prefabAsset.name, true, $"Restored original state ({record.convertedNodes.Count} nodes).");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            statusMessage = $"Successfully restored '{prefabAsset.name}' to its pre-conversion state.";
            return true;
        }
    }
}
#endif
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Automated 15-case Editor Validation Suite for MeshLOD Studio.
    /// Creates synthetic meshes (with real Unity 6.3 MeshLodUtility.GenerateMeshLods data)
    /// and temporary prefabs, verifies all 15 required test scenarios, and cleans up after execution.
    /// </summary>
    public static class MeshLODStudioTests
    {
        private const string TempTestFolder = "Assets/MeshLODStudio_TempTests";

        [MenuItem("Tools/MeshLOD Studio/Run 15-Case Validation Suite", false, 50)]
        public static void RunFromMenu()
        {
            string report = RunAllValidationTests();
            Debug.Log(report);
            EditorUtility.DisplayDialog("MeshLOD Studio — 15-Case Test Report", report, "OK");
        }

        public static string RunAllValidationTests()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== MeshLOD Studio: 15-Case Validation Report ===");
            int passed = 0;
            int total = 15;

            MeshLODStudioSettings.EnsureFolder(TempTestFolder);
            MeshLODStudioSettings settings = MeshLODStudioSettings.GetOrCreate();
            MeshLODPreset balanced = settings.GetPresetByName("Balanced");

            try
            {
                // Prepare a high-poly synthetic sphere mesh (>= 256 tris) with real Unity 6.3 Mesh LODs
                Mesh meshWithLods = CreateSyntheticSphereMeshWithLods("TestSphere_MeshLOD", 24, 24, true);
                Mesh meshWithoutLods = CreateSyntheticSphereMeshWithLods("TestLowPoly_NoLOD", 4, 4, false);

                // Test 1: Simple prefab with one MeshRenderer
                passed += RecordTest(sb, 1, "Simple prefab with one MeshRenderer", () =>
                {
                    GameObject prefab = CreateTempPrefab("T1_Simple", meshWithLods);
                    string origGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    bool ok = PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                    string afterGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
                    return ok && origGuid == afterGuid && prefab.GetComponent<LODGroup>() != null;
                });

                // Test 2: Prefab with multiple child MeshRenderers
                passed += RecordTest(sb, 2, "Prefab with multiple child MeshRenderers", () =>
                {
                    var root = new GameObject("T2_MultiChild");
                    AddMeshChild(root.transform, "PartA", meshWithLods);
                    AddMeshChild(root.transform, "PartB", meshWithLods);
                    GameObject prefab = SaveTempPrefab(root, "T2_MultiChild");
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    bool ok = PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                    return ok && prefab.GetComponentsInChildren<LODGroup>(true).Length == 2;
                });

                // Test 3: Selective multi-object prefab conversion (e.g., Castle: convert Tower, skip Fence)
                passed += RecordTest(sb, 3, "Prefab with multiple independent meshes (selective skip)", () =>
                {
                    var root = new GameObject("T3_Castle");
                    AddMeshChild(root.transform, "Tower", meshWithLods);
                    AddMeshChild(root.transform, "Fence", meshWithLods);
                    GameObject prefab = SaveTempPrefab(root, "T3_Castle");
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    scan.meshNodes[1].selectedForConversion = false; // Skip Fence
                    bool ok = PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                    return ok &&
                           prefab.transform.Find("Tower").GetComponent<LODGroup>() != null &&
                           prefab.transform.Find("Fence").GetComponent<LODGroup>() == null;
                });

                // Test 4: Prefab containing nested prefabs
                passed += RecordTest(sb, 4, "Prefab containing nested prefabs", () =>
                {
                    GameObject childPrefab = CreateTempPrefab("T4_NestedChild", meshWithLods);
                    var parentGo = new GameObject("T4_Parent");
                    GameObject inst = (GameObject)PrefabUtility.InstantiatePrefab(childPrefab, parentGo.transform);
                    GameObject parentPrefab = SaveTempPrefab(parentGo, "T4_Parent");
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(parentPrefab, settings);
                    return scan.meshNodes.Count == 1 && scan.meshNodes[0].isNestedPrefabInstance;
                });

                // Test 5: Prefab Variant
                passed += RecordTest(sb, 5, "Prefab Variant compatibility", () =>
                {
                    GameObject basePrefab = CreateTempPrefab("T5_Base", meshWithLods);
                    GameObject instanceRoot = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                    string variantPath = $"{TempTestFolder}/T5_Variant.prefab";
                    GameObject variantPrefab = PrefabUtility.SaveAsPrefabAsset(instanceRoot, variantPath);
                    UnityEngine.Object.DestroyImmediate(instanceRoot);
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(variantPrefab, settings);
                    return scan.isVariant && PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                });

                // Test 6: Prefab with existing LODGroups (protected from duplicate wrapping)
                passed += RecordTest(sb, 6, "Prefab with existing manual LODGroup skipped safely", () =>
                {
                    var go = new GameObject("T6_ExistingLOD");
                    AddMeshChild(go.transform, "MeshNode", meshWithLods);
                    go.transform.Find("MeshNode").gameObject.AddComponent<LODGroup>();
                    GameObject prefab = SaveTempPrefab(go, "T6_ExistingLOD");
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    return scan.compatibleNodeCount == 0 && scan.existingLODGroupCount == 1;
                });

                // Test 7: Prefab with missing mesh references
                passed += RecordTest(sb, 7, "Prefab with missing mesh reference flagged", () =>
                {
                    var go = new GameObject("T7_MissingMesh");
                    go.AddComponent<MeshFilter>(); // sharedMesh left null
                    go.AddComponent<MeshRenderer>();
                    GameObject prefab = SaveTempPrefab(go, "T7_MissingMesh");
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    return scan.meshNodes[0].status == NodeCompatibilityStatus.MissingMeshReference;
                });

                // Test 8: Prefab with unsupported Mesh LOD data (< 256 tris / 1 LOD)
                passed += RecordTest(sb, 8, "Prefab with unsupported (<256 tris) mesh flagged", () =>
                {
                    GameObject prefab = CreateTempPrefab("T8_Unsupported", meshWithoutLods);
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    return scan.meshNodes[0].status == NodeCompatibilityStatus.TooFewTrianglesForMeshLOD;
                });

                // Test 9: Prefab with custom component references preserved
                passed += RecordTest(sb, 9, "Custom components (BoxCollider) preserved after conversion", () =>
                {
                    GameObject prefab = CreateTempPrefab("T9_Components", meshWithLods);
                    string path = AssetDatabase.GetAssetPath(prefab);
                    GameObject root = PrefabUtility.LoadPrefabContents(path);
                    root.AddComponent<BoxCollider>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    PrefabUtility.UnloadPrefabContents(root);

                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                    return prefab.GetComponent<BoxCollider>() != null && prefab.GetComponent<LODGroup>() != null;
                });

                // Test 10: Existing scene instances inherit converted LODGroup automatically
                passed += RecordTest(sb, 10, "Existing scene instances inherit converted LODGroup", () =>
                {
                    GameObject prefab = CreateTempPrefab("T10_SceneInherit", meshWithLods);
                    GameObject sceneInst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    try
                    {
                        PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                        PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                        return sceneInst.GetComponent<LODGroup>() != null && sceneInst.transform.childCount > 0;
                    }
                    finally
                    {
                        if (sceneInst != null) UnityEngine.Object.DestroyImmediate(sceneInst);
                    }
                });

                // Test 11: Scene instance-specific material override preserved on LOD0
                passed += RecordTest(sb, 11, "Scene instance material override preserved on LOD0", () =>
                {
                    GameObject prefab = CreateTempPrefab("T11_Overrides", meshWithLods);
                    GameObject sceneInst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    var customMat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { name = "OverrideMat" };
                    try
                    {
                        sceneInst.GetComponent<MeshRenderer>().sharedMaterial = customMat;
                        PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                        PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                        return sceneInst.GetComponent<MeshRenderer>().sharedMaterial == customMat;
                    }
                    finally
                    {
                        if (sceneInst != null) UnityEngine.Object.DestroyImmediate(sceneInst);
                        UnityEngine.Object.DestroyImmediate(customMat);
                    }
                });

                // Test 12: Batch processing multiple prefabs
                passed += RecordTest(sb, 12, "Batch processing multiple prefab assets", () =>
                {
                    GameObject pA = CreateTempPrefab("T12_BatchA", meshWithLods);
                    GameObject pB = CreateTempPrefab("T12_BatchB", meshWithLods);
                    bool okA = PrefabConversionService.ConvertPrefabAsset(PrefabScannerAndValidator.ScanPrefabAsset(pA, settings), balanced, settings, out _);
                    bool okB = PrefabConversionService.ConvertPrefabAsset(PrefabScannerAndValidator.ScanPrefabAsset(pB, settings), balanced, settings, out _);
                    return okA && okB;
                });

                // Test 13: Conversion Rollback restores original prefab state
                passed += RecordTest(sb, 13, "Conversion rollback restores original state cleanly", () =>
                {
                    GameObject prefab = CreateTempPrefab("T13_Rollback", meshWithLods);
                    PrefabScanReport scan = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    PrefabConversionService.ConvertPrefabAsset(scan, balanced, settings, out _);
                    bool rolledBack = BackupAndRollbackManager.RollbackPrefab(prefab, out _, settings);
                    return rolledBack && prefab.GetComponent<LODGroup>() == null && prefab.transform.childCount == 0;
                });

                // Test 14: Scene Auto Setup deduplicates multiple instances of same prefab
                passed += RecordTest(sb, 14, "Scene Auto Setup deduplicates multiple scene instances", () =>
                {
                    GameObject prefab = CreateTempPrefab("T14_SceneDedupe", meshWithLods);
                    GameObject inst1 = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    GameObject inst2 = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    try
                    {
                        var summary = SceneAutoSetupService.ScanActiveScene(settings);
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
                        foreach (var g in summary.uniquePrefabGroups)
                        {
                            if (g.sourcePrefabGuid == guid)
                                return g.instanceCountInScene == 2;
                        }
                        return false;
                    }
                    finally
                    {
                        if (inst1 != null) UnityEngine.Object.DestroyImmediate(inst1);
                        if (inst2 != null) UnityEngine.Object.DestroyImmediate(inst2);
                    }
                });

                // Test 15: Repeated conversion attempt prevented on already-converted prefab
                passed += RecordTest(sb, 15, "Repeated conversion attempt prevented on converted prefab", () =>
                {
                    GameObject prefab = CreateTempPrefab("T15_Idempotent", meshWithLods);
                    PrefabScanReport scan1 = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    PrefabConversionService.ConvertPrefabAsset(scan1, balanced, settings, out _);
                    PrefabScanReport scan2 = PrefabScannerAndValidator.ScanPrefabAsset(prefab, settings);
                    return scan2.compatibleNodeCount == 0 && scan2.meshNodes[0].status == NodeCompatibilityStatus.AlreadyConvertedByStudio;
                });
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempTestFolder);
                AssetDatabase.Refresh();
            }

            sb.AppendLine($"=== Result: {passed}/{total} Tests Passed ===");
            return sb.ToString();
        }

        private static int RecordTest(StringBuilder sb, int id, string description, Func<bool> testFunc)
        {
            try
            {
                bool ok = testFunc();
                sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] Test {id:00}: {description}");
                return ok ? 1 : 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[ERROR] Test {id:00}: {description} ({ex.Message})");
                return 0;
            }
        }

        private static Mesh CreateSyntheticSphereMeshWithLods(string name, int lon, int lat, bool generateLods)
        {
            var mesh = new Mesh { name = name };
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int y = 0; y <= lat; y++)
            {
                float v = (float)y / lat;
                float theta = v * Mathf.PI;
                for (int x = 0; x <= lon; x++)
                {
                    float u = (float)x / lon;
                    float phi = u * Mathf.PI * 2f;
                    Vector3 dir = new Vector3(
                        Mathf.Sin(theta) * Mathf.Cos(phi),
                        Mathf.Cos(theta),
                        Mathf.Sin(theta) * Mathf.Sin(phi));
                    verts.Add(dir);
                    norms.Add(dir);
                    uvs.Add(new Vector2(u, v));
                }
            }

            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int i0 = y * (lon + 1) + x;
                    int i1 = i0 + 1;
                    int i2 = (y + 1) * (lon + 1) + x;
                    int i3 = i2 + 1;
                    tris.Add(i0); tris.Add(i2); tris.Add(i1);
                    tris.Add(i1); tris.Add(i2); tris.Add(i3);
                }
            }

            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            if (generateLods)
            {
                MeshLodUtility.GenerateMeshLods(mesh, -1);
            }

            string path = $"{TempTestFolder}/{name}.asset";
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static void AddMeshChild(Transform parent, string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");
        }

        private static GameObject CreateTempPrefab(string name, Mesh mesh)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");
            return SaveTempPrefab(go, name);
        }

        private static GameObject SaveTempPrefab(GameObject root, string name)
        {
            string path = $"{TempTestFolder}/{name}.prefab";
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }
    }
}
#endif
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Handles Unity 6.3 LTS Mesh LOD inspection (Mesh.lodCount, Mesh.GetLod, Mesh.GetIndices)
    /// and lossless reconstruction of standalone, vertex-compacted Mesh assets per LOD level.
    /// Uses only documented public Unity 6.3 APIs.
    /// </summary>
    public static class MeshLODDetectorAndExtractor
    {
        public static bool HasGeneratedMeshLods(Mesh mesh, out int lodCount)
        {
            lodCount = 0;
            if (mesh == null) return false;

            lodCount = mesh.lodCount;
            return lodCount > 1;
        }

        public static bool HasNonTriangleTopology(Mesh mesh)
        {
            if (mesh == null) return false;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int s = 0; s < subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    return true;
            }
            return false;
        }

        public static int GetBaseTriangleCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            int totalIndices = 0;
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);

            for (int s = 0; s < subMeshCount; s++)
            {
                if (mesh.lodCount > 1)
                {
                    MeshLodRange range0 = mesh.GetLod(s, 0);
                    totalIndices += (int)range0.indexCount;
                }
                else
                {
                    SubMeshDescriptor desc = mesh.GetSubMesh(s);
                    totalIndices += desc.indexCount;
                }
            }
            return totalIndices / 3;
        }

        public static List<LodLevelMetadata> AnalyzeMeshLodLevels(Mesh mesh)
        {
            var results = new List<LodLevelMetadata>();
            if (mesh == null) return results;

            int lodCount = Mathf.Max(1, mesh.lodCount);
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            int lod0Triangles = 0;

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData meshData = dataArray[0];

                for (int level = 0; level < lodCount; level++)
                {
                    int levelIndices = 0;
                    var uniqueVertices = new HashSet<int>();

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        if (meshData.lodCount > 1 && level < meshData.lodCount)
                        {
                            MeshLodRange lodRange = meshData.GetLod(s, level);
                            int count = (int)lodRange.indexCount;
                            levelIndices += count;

                            if (count > 0)
                            {
                                using (var tempIndices = new NativeArray<int>(count, Allocator.Temp))
                                {
                                    meshData.GetIndices(tempIndices, s, level, true);
                                    for (int i = 0; i < count; i++)
                                        uniqueVertices.Add(tempIndices[i]);
                                }
                            }
                        }
                        else
                        {
                            SubMeshDescriptor subDesc = meshData.GetSubMesh(s);
                            int count = subDesc.indexCount;
                            levelIndices += count;
                            if (count > 0)
                            {
                                using (var tempIndices = new NativeArray<int>(count, Allocator.Temp))
                                {
                                    meshData.GetIndices(tempIndices, s, true);
                                    for (int i = 0; i < count; i++)
                                        uniqueVertices.Add(tempIndices[i]);
                                }
                            }
                        }
                    }

                    int tris = levelIndices / 3;
                    if (level == 0) lod0Triangles = Mathf.Max(1, tris);

                    float reduction = level == 0
                        ? 0f
                        : Mathf.Clamp01(1f - ((float)tris / lod0Triangles)) * 100f;

                    results.Add(new LodLevelMetadata
                    {
                        lodIndex = level,
                        totalIndexCount = levelIndices,
                        totalTriangleCount = tris,
                        estimatedUniqueVertices = uniqueVertices.Count,
                        reductionPercentFromLod0 = reduction
                    });
                }
            }

            return results;
        }

        /// <summary>
        /// Reconstructs a standalone, vertex-compacted Unity Mesh for a specific Mesh LOD level.
        /// Works even when the source Mesh has Read/Write Disabled in ModelImporter by reading
        /// via Mesh.AcquireReadOnlyMeshData.
        /// Preserves Positions, Normals, Tangents, Color32, UV0..UV7, SubMeshes, and Bounds.
        /// </summary>
        public static Mesh ExtractStandaloneLodMesh(Mesh sourceMesh, int targetLodLevel, bool compactVertexBuffer = true)
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));

            int availableLods = Mathf.Max(1, sourceMesh.lodCount);
            int clampedLod = Mathf.Clamp(targetLodLevel, 0, availableLods - 1);
            int subMeshCount = Mathf.Max(1, sourceMesh.subMeshCount);

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(sourceMesh))
            {
                Mesh.MeshData srcData = dataArray[0];
                int srcVertexCount = srcData.vertexCount;

                // 1. Read per-submesh triangle indices for the requested Mesh LOD level
                var subMeshIndices = new List<int[]>(subMeshCount);
                for (int s = 0; s < subMeshCount; s++)
                {
                    if (srcData.lodCount > 1 && clampedLod < srcData.lodCount)
                    {
                        MeshLodRange range = srcData.GetLod(s, clampedLod);
                        int count = (int)range.indexCount;
                        int[] indices = new int[count];
                        if (count > 0)
                        {
                            using (var nativeIndices = new NativeArray<int>(count, Allocator.Temp))
                            {
                                srcData.GetIndices(nativeIndices, s, clampedLod, true);
                                nativeIndices.CopyTo(indices);
                            }
                        }
                        subMeshIndices.Add(indices);
                    }
                    else
                    {
                        SubMeshDescriptor desc = srcData.GetSubMesh(s);
                        int count = desc.indexCount;
                        int[] indices = new int[count];
                        if (count > 0)
                        {
                            using (var nativeIndices = new NativeArray<int>(count, Allocator.Temp))
                            {
                                srcData.GetIndices(nativeIndices, s, true);
                                nativeIndices.CopyTo(indices);
                            }
                        }
                        subMeshIndices.Add(indices);
                    }
                }

                // 2. Read vertex attributes from MeshData
                var srcPositions = new NativeArray<Vector3>(srcVertexCount, Allocator.Temp);
                srcData.GetVertices(srcPositions);

                bool hasNormals = srcData.HasVertexAttribute(VertexAttribute.Normal);
                var srcNormals = new NativeArray<Vector3>(hasNormals ? srcVertexCount : 0, Allocator.Temp);
                if (hasNormals) srcData.GetNormals(srcNormals);

                bool hasTangents = srcData.HasVertexAttribute(VertexAttribute.Tangent);
                var srcTangents = new NativeArray<Vector4>(hasTangents ? srcVertexCount : 0, Allocator.Temp);
                if (hasTangents) srcData.GetTangents(srcTangents);

                bool hasColors = srcData.HasVertexAttribute(VertexAttribute.Color);
                var srcColors = new NativeArray<Color32>(hasColors ? srcVertexCount : 0, Allocator.Temp);
                if (hasColors) srcData.GetColors(srcColors);

                var srcUVs = new NativeArray<Vector4>[8];
                var hasUV = new bool[8];
                for (int uvCh = 0; uvCh < 8; uvCh++)
                {
                    VertexAttribute attr = (VertexAttribute)((int)VertexAttribute.TexCoord0 + uvCh);
                    hasUV[uvCh] = srcData.HasVertexAttribute(attr);
                    if (hasUV[uvCh])
                    {
                        srcUVs[uvCh] = new NativeArray<Vector4>(srcVertexCount, Allocator.Temp);
                        srcData.GetUVs(uvCh, srcUVs[uvCh]);
                    }
                }

                try
                {
                    // 3. Build compacted vertex tables so lower LODs also shrink vertex buffer memory
                    int[] oldToNewMap = new int[srcVertexCount];
                    for (int i = 0; i < srcVertexCount; i++) oldToNewMap[i] = -1;

                    var outPositions = new List<Vector3>();
                    var outNormals = hasNormals ? new List<Vector3>() : null;
                    var outTangents = hasTangents ? new List<Vector4>() : null;
                    var outColors = hasColors ? new List<Color32>() : null;
                    var outUVs = new List<Vector4>[8];
                    for (int uvCh = 0; uvCh < 8; uvCh++)
                    {
                        if (hasUV[uvCh]) outUVs[uvCh] = new List<Vector4>();
                    }

                    if (!compactVertexBuffer)
                    {
                        for (int v = 0; v < srcVertexCount; v++)
                        {
                            oldToNewMap[v] = v;
                            outPositions.Add(srcPositions[v]);
                            if (hasNormals) outNormals.Add(srcNormals[v]);
                            if (hasTangents) outTangents.Add(srcTangents[v]);
                            if (hasColors) outColors.Add(srcColors[v]);
                            for (int uvCh = 0; uvCh < 8; uvCh++)
                            {
                                if (hasUV[uvCh]) outUVs[uvCh].Add(srcUVs[uvCh][v]);
                            }
                        }
                    }

                    var remappedSubMeshes = new List<int[]>(subMeshCount);
                    for (int s = 0; s < subMeshCount; s++)
                    {
                        int[] rawIndices = subMeshIndices[s];
                        int[] remapped = new int[rawIndices.Length];

                        for (int i = 0; i < rawIndices.Length; i++)
                        {
                            int oldIdx = Mathf.Clamp(rawIndices[i], 0, srcVertexCount - 1);
                            int newIdx = oldToNewMap[oldIdx];
                            if (newIdx == -1)
                            {
                                newIdx = outPositions.Count;
                                oldToNewMap[oldIdx] = newIdx;

                                outPositions.Add(srcPositions[oldIdx]);
                                if (hasNormals) outNormals.Add(srcNormals[oldIdx]);
                                if (hasTangents) outTangents.Add(srcTangents[oldIdx]);
                                if (hasColors) outColors.Add(srcColors[oldIdx]);
                                for (int uvCh = 0; uvCh < 8; uvCh++)
                                {
                                    if (hasUV[uvCh]) outUVs[uvCh].Add(srcUVs[uvCh][oldIdx]);
                                }
                            }
                            remapped[i] = newIdx;
                        }
                        remappedSubMeshes.Add(remapped);
                    }

                    // 4. Construct the standalone Unity Mesh (lodCount == 1)
                    var extractedMesh = new Mesh
                    {
                        name = $"{sourceMesh.name}_LOD{clampedLod}",
                        indexFormat = outPositions.Count > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16
                    };

                    extractedMesh.SetVertices(outPositions);
                    if (hasNormals && outNormals.Count == outPositions.Count)
                        extractedMesh.SetNormals(outNormals);
                    if (hasTangents && outTangents.Count == outPositions.Count)
                        extractedMesh.SetTangents(outTangents);
                    if (hasColors && outColors.Count == outPositions.Count)
                        extractedMesh.SetColors(outColors);

                    for (int uvCh = 0; uvCh < 8; uvCh++)
                    {
                        if (hasUV[uvCh] && outUVs[uvCh].Count == outPositions.Count)
                            extractedMesh.SetUVs(uvCh, outUVs[uvCh]);
                    }

                    extractedMesh.subMeshCount = subMeshCount;
                    for (int s = 0; s < subMeshCount; s++)
                    {
                        extractedMesh.SetTriangles(remappedSubMeshes[s], s, false);
                    }

                    extractedMesh.bounds = sourceMesh.bounds;
                    extractedMesh.UploadMeshData(false);
                    return extractedMesh;
                }
                finally
                {
                    if (srcPositions.IsCreated) srcPositions.Dispose();
                    if (srcNormals.IsCreated) srcNormals.Dispose();
                    if (srcTangents.IsCreated) srcTangents.Dispose();
                    if (srcColors.IsCreated) srcColors.Dispose();
                    for (int uvCh = 0; uvCh < 8; uvCh++)
                    {
                        if (srcUVs[uvCh].IsCreated) srcUVs[uvCh].Dispose();
                    }
                }
            }
        }

        public static bool EnableMeshLodOnModelImporter(Mesh mesh, out string message)
        {
            if (mesh == null)
            {
                message = "Mesh reference is null.";
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath))
            {
                message = "Mesh is not a persistent asset on disk.";
                return false;
            }

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                message = $"Asset at '{assetPath}' is not imported via ModelImporter.";
                return false;
            }

            if (GetBaseTriangleCount(mesh) < 256)
            {
                message = $"Mesh '{mesh.name}' has fewer than 256 triangles ({GetBaseTriangleCount(mesh)} tris). Unity's Mesh LOD generator requires at least 256 triangles.";
                return false;
            }

            Undo.RecordObject(importer, "Enable Generate Mesh LODs");
            importer.generateMeshLods = true;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();

            message = $"Enabled 'Generate Mesh LODs' on '{assetPath}' and reimported.";
            return true;
        }
    }
}
#endif
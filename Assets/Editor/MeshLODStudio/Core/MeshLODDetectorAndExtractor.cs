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
    /// Handles Unity 6.3 LTS Mesh LOD inspection (Mesh.lodCount, Mesh.GetLod, Mesh.GetIndices),
    /// lossless standalone mesh extraction, and far-distance procedural edge-collapse decimation
    /// so models that stop at 3 native Mesh LODs can still generate up to 8 LOD levels (LOD0..LOD7).
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

        public static List<LodLevelMetadata> AnalyzeMeshLodLevels(
            Mesh mesh,
            int maxPreviewTiers = 8,
            bool includeSynthesizedFarLods = true,
            int minTrianglesFloor = 6)
        {
            var results = new List<LodLevelMetadata>();
            if (mesh == null) return results;

            int nativeLodCount = Mathf.Max(1, mesh.lodCount);
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            int lod0Triangles = 0;
            int lastNativeTriangles = 0;
            int lastNativeVertices = 0;

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData meshData = dataArray[0];

                for (int level = 0; level < nativeLodCount; level++)
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
                    lastNativeTriangles = tris;
                    lastNativeVertices = uniqueVertices.Count;

                    float reduction = level == 0
                        ? 0f
                        : Mathf.Clamp01(1f - ((float)tris / lod0Triangles)) * 100f;

                    results.Add(new LodLevelMetadata
                    {
                        lodIndex = level,
                        isSynthesizedFarLod = false,
                        totalIndexCount = levelIndices,
                        totalTriangleCount = tris,
                        estimatedUniqueVertices = uniqueVertices.Count,
                        reductionPercentFromLod0 = reduction
                    });
                }
            }

            // Append projected synthesized far-distance LOD levels up to 8 tiers
            if (includeSynthesizedFarLods && lastNativeTriangles > minTrianglesFloor)
            {
                int currentTris = lastNativeTriangles;
                int currentVerts = lastNativeVertices;

                for (int extraLevel = nativeLodCount; extraLevel < maxPreviewTiers; extraLevel++)
                {
                    int nextTris = Mathf.Max(minTrianglesFloor, Mathf.RoundToInt(currentTris * 0.5f));
                    if (nextTris >= currentTris && currentTris <= minTrianglesFloor)
                        break;

                    currentTris = nextTris;
                    currentVerts = Mathf.Max(4, Mathf.RoundToInt(currentVerts * 0.55f));
                    float reduction = Mathf.Clamp01(1f - ((float)currentTris / Mathf.Max(1, lod0Triangles))) * 100f;

                    results.Add(new LodLevelMetadata
                    {
                        lodIndex = extraLevel,
                        isSynthesizedFarLod = true,
                        totalIndexCount = currentTris * 3,
                        totalTriangleCount = currentTris,
                        estimatedUniqueVertices = currentVerts,
                        reductionPercentFromLod0 = reduction
                    });

                    if (currentTris <= minTrianglesFloor)
                        break;
                }
            }

            return results;
        }

        /// <summary>
        /// Extracts a native Unity 6.3 Mesh LOD level if targetLodLevel is within sourceMesh.lodCount,
        /// or synthesizes an ultra-low-poly far-away LOD level via edge-collapse decimation if
        /// targetLodLevel >= sourceMesh.lodCount.
        /// </summary>
        public static Mesh ExtractOrSynthesizeLodMesh(
            Mesh sourceMesh,
            int targetLodLevel,
            int minTrianglesFloor = 6,
            bool compactVertexBuffer = true)
        {
            if (sourceMesh == null)
                throw new ArgumentNullException(nameof(sourceMesh));

            int nativeLods = Mathf.Max(1, sourceMesh.lodCount);
            if (targetLodLevel < nativeLods)
            {
                return ExtractStandaloneLodMesh(sourceMesh, targetLodLevel, compactVertexBuffer);
            }

            // Extract the lowest native LOD mesh first, then decimate it further for far distance
            int highestNativeIndex = nativeLods - 1;
            Mesh baseLowMesh = ExtractStandaloneLodMesh(sourceMesh, highestNativeIndex, true);
            int extraSteps = (targetLodLevel - highestNativeIndex);
            float reductionRatio = Mathf.Pow(0.5f, extraSteps);

            Mesh decimated = DecimateMeshForFarLod(
                baseLowMesh,
                reductionRatio,
                minTrianglesFloor,
                $"{sourceMesh.name}_LOD{targetLodLevel}_FarDecimated");

            UnityEngine.Object.DestroyImmediate(baseLowMesh);
            return decimated;
        }

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

        /// <summary>
        /// Position-Welded Shortest-Edge Collapse Decimator for far-distance LOD levels (LOD3..LOD7).
        /// Welds UV/normal seam splits by 3D position so edges can collapse below Unity's 64-triangle limit
        /// while preserving submeshes, UVs, normals, and silhouette bounds.
        /// </summary>
        public static Mesh DecimateMeshForFarLod(
            Mesh inputMesh,
            float targetRatio,
            int minTrianglesFloor,
            string newMeshName)
        {
            Vector3[] positions = inputMesh.vertices;
            Vector3[] normals = inputMesh.normals;
            Vector4[] tangents = inputMesh.tangents;
            Color32[] colors = inputMesh.colors32;
            Vector2[] uv0 = inputMesh.uv;
            Vector2[] uv1 = inputMesh.uv2;

            int vertexCount = positions.Length;
            int subMeshCount = Mathf.Max(1, inputMesh.subMeshCount);

            var subTris = new List<int>[subMeshCount];
            int totalInputTris = 0;
            for (int s = 0; s < subMeshCount; s++)
            {
                int[] tris = inputMesh.GetTriangles(s);
                subTris[s] = new List<int>(tris);
                totalInputTris += tris.Length / 3;
            }

            int targetTris = Mathf.Max(minTrianglesFloor, Mathf.RoundToInt(totalInputTris * Mathf.Clamp(targetRatio, 0.02f, 0.9f)));

            // 1. Weld co-located 3D positions so UV seams don't block far-distance simplification
            int[] vertexClusterRoot = new int[vertexCount];
            var spatialMap = new Dictionary<Vector3Int, int>(vertexCount);
            float invCell = 10000f;

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 p = positions[v];
                var key = new Vector3Int(
                    Mathf.RoundToInt(p.x * invCell),
                    Mathf.RoundToInt(p.y * invCell),
                    Mathf.RoundToInt(p.z * invCell));

                if (!spatialMap.TryGetValue(key, out int rootIdx))
                {
                    rootIdx = v;
                    spatialMap[key] = v;
                }
                vertexClusterRoot[v] = rootIdx;
            }

            int FindCluster(int v)
            {
                int curr = vertexClusterRoot[v];
                while (curr != vertexClusterRoot[curr])
                {
                    vertexClusterRoot[curr] = vertexClusterRoot[vertexClusterRoot[curr]];
                    curr = vertexClusterRoot[curr];
                }
                vertexClusterRoot[v] = curr;
                return curr;
            }

            // 2. Iteratively collapse shortest edges until target triangle count is reached
            int currentTris = totalInputTris;
            int maxIterations = Mathf.Max(1, totalInputTris - targetTris + 32);

            for (int iter = 0; iter < maxIterations && currentTris > targetTris; iter++)
            {
                float shortestSq = float.MaxValue;
                int bestClusterA = -1;
                int bestClusterB = -1;

                // Find shortest remaining triangle edge across all submeshes
                for (int s = 0; s < subMeshCount; s++)
                {
                    List<int> tris = subTris[s];
                    for (int i = 0; i < tris.Count; i += 3)
                    {
                        int c0 = FindCluster(tris[i]);
                        int c1 = FindCluster(tris[i + 1]);
                        int c2 = FindCluster(tris[i + 2]);

                        if (c0 == c1 || c1 == c2 || c2 == c0) continue;

                        float d01 = (positions[c0] - positions[c1]).sqrMagnitude;
                        if (d01 < shortestSq) { shortestSq = d01; bestClusterA = c0; bestClusterB = c1; }

                        float d12 = (positions[c1] - positions[c2]).sqrMagnitude;
                        if (d12 < shortestSq) { shortestSq = d12; bestClusterA = c1; bestClusterB = c2; }

                        float d20 = (positions[c2] - positions[c0]).sqrMagnitude;
                        if (d20 < shortestSq) { shortestSq = d20; bestClusterA = c2; bestClusterB = c0; }
                    }
                }

                if (bestClusterA == -1 || bestClusterB == -1)
                    break;

                // Collapse bestClusterB into bestClusterA at their midpoint
                Vector3 midPoint = (positions[bestClusterA] + positions[bestClusterB]) * 0.5f;
                positions[bestClusterA] = midPoint;
                vertexClusterRoot[bestClusterB] = bestClusterA;

                // Prune degenerate triangles created by the collapse
                int remainingTris = 0;
                for (int s = 0; s < subMeshCount; s++)
                {
                    List<int> tris = subTris[s];
                    int writeIdx = 0;
                    for (int i = 0; i < tris.Count; i += 3)
                    {
                        int v0 = tris[i];
                        int v1 = tris[i + 1];
                        int v2 = tris[i + 2];

                        int c0 = FindCluster(v0);
                        int c1 = FindCluster(v1);
                        int c2 = FindCluster(v2);

                        if (c0 == c1 || c1 == c2 || c2 == c0)
                            continue;

                        positions[v0] = positions[c0];
                        positions[v1] = positions[c1];
                        positions[v2] = positions[c2];

                        tris[writeIdx++] = v0;
                        tris[writeIdx++] = v1;
                        tris[writeIdx++] = v2;
                        remainingTris++;
                    }
                    if (writeIdx < tris.Count)
                        tris.RemoveRange(writeIdx, tris.Count - writeIdx);
                }

                currentTris = remainingTris;
            }

            // 3. Compact the surviving vertices into a clean standalone Mesh
            int[] remap = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) remap[i] = -1;

            var outPos = new List<Vector3>();
            var outNorm = (normals != null && normals.Length == vertexCount) ? new List<Vector3>() : null;
            var outTan = (tangents != null && tangents.Length == vertexCount) ? new List<Vector4>() : null;
            var outCol = (colors != null && colors.Length == vertexCount) ? new List<Color32>() : null;
            var outUv0 = (uv0 != null && uv0.Length == vertexCount) ? new List<Vector2>() : null;
            var outUv1 = (uv1 != null && uv1.Length == vertexCount) ? new List<Vector2>() : null;

            for (int s = 0; s < subMeshCount; s++)
            {
                List<int> tris = subTris[s];
                for (int i = 0; i < tris.Count; i++)
                {
                    int oldV = tris[i];
                    int newV = remap[oldV];
                    if (newV == -1)
                    {
                        newV = outPos.Count;
                        remap[oldV] = newV;
                        outPos.Add(positions[FindCluster(oldV)]);
                        if (outNorm != null) outNorm.Add(normals[oldV]);
                        if (outTan != null) outTan.Add(tangents[oldV]);
                        if (outCol != null) outCol.Add(colors[oldV]);
                        if (outUv0 != null) outUv0.Add(uv0[oldV]);
                        if (outUv1 != null) outUv1.Add(uv1[oldV]);
                    }
                    tris[i] = newV;
                }
            }

            var result = new Mesh
            {
                name = newMeshName,
                indexFormat = outPos.Count > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };

            result.SetVertices(outPos);
            if (outNorm != null) result.SetNormals(outNorm);
            if (outTan != null) result.SetTangents(outTan);
            if (outCol != null) result.SetColors(outCol);
            if (outUv0 != null) result.SetUVs(0, outUv0);
            if (outUv1 != null) result.SetUVs(1, outUv1);

            result.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++)
            {
                result.SetTriangles(subTris[s], s, false);
            }

            if (outNorm == null)
                result.RecalculateNormals();

            result.bounds = inputMesh.bounds;
            result.UploadMeshData(false);
            return result;
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

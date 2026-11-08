#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Non-destructive interactive 3D LOD Preview Controller using Unity's PreviewRenderUtility.
    /// Caches temporary extracted meshes for each LOD level in memory so the user can inspect
    /// LOD0, LOD1, LOD2, wireframe overlay, and simulated camera distances without modifying assets.
    /// </summary>
    public sealed class MeshLODPreviewController : IDisposable
    {
        private PreviewRenderUtility previewUtility;
        private Mesh currentSourceMesh;
        private Material[] currentMaterials;
        private readonly Dictionary<int, Mesh> cachedLodMeshes = new Dictionary<int, Mesh>();

        public Vector2 orbitAngles = new Vector2(25f, -35f);
        public float simulatedDistanceMeters = 15f;
        public float cameraFov = 60f;
        public int forcedPreviewLod = -1; // -1 = Auto by distance, >= 0 = Locked to specific LOD
        public bool showWireframe = false;
        public int activeRenderedLodIndex { get; private set; }
        public int activeRenderedTriangles { get; private set; }
        public float activeScreenRelativeHeight { get; private set; }

        public void SetTarget(Mesh mesh, Material[] materials)
        {
            if (currentSourceMesh == mesh) return;

            ClearCache();
            currentSourceMesh = mesh;
            currentMaterials = materials;

            if (currentSourceMesh != null)
            {
                float radius = Mathf.Max(0.5f, currentSourceMesh.bounds.extents.magnitude);
                simulatedDistanceMeters = radius * 3.0f;
            }
        }

        public Mesh GetOrBuildPreviewLodMesh(int lodIndex)
        {
            if (currentSourceMesh == null) return null;
            int clamped = Mathf.Clamp(lodIndex, 0, Mathf.Max(0, currentSourceMesh.lodCount - 1));

            if (!cachedLodMeshes.TryGetValue(clamped, out Mesh extracted) || extracted == null)
            {
                extracted = MeshLODDetectorAndExtractor.ExtractStandaloneLodMesh(currentSourceMesh, clamped, true);
                extracted.hideFlags = HideFlags.HideAndDontSave;
                cachedLodMeshes[clamped] = extracted;
            }
            return extracted;
        }

        public Texture RenderPreview(Rect rect, MeshLODPreset activePreset)
        {
            if (currentSourceMesh == null || rect.width < 16f || rect.height < 16f)
                return null;

            EnsureInitialized();

            float objSize = DistanceThresholdCalculator.GetObjectWorldSize(currentSourceMesh.bounds);
            activeScreenRelativeHeight = DistanceThresholdCalculator.DistanceToScreenRelativeHeight(
                simulatedDistanceMeters,
                objSize,
                cameraFov);

            int lodToRender = forcedPreviewLod;
            if (lodToRender < 0)
            {
                lodToRender = EvaluateAutoLodForHeight(activeScreenRelativeHeight, currentSourceMesh.lodCount, activePreset);
            }

            activeRenderedLodIndex = lodToRender;
            if (lodToRender >= currentSourceMesh.lodCount)
            {
                // Culled state
                activeRenderedTriangles = 0;
                previewUtility.BeginPreview(rect, GUIStyle.none);
                previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
                previewUtility.camera.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);
                previewUtility.camera.Render();
                return previewUtility.EndPreview();
            }

            Mesh previewMesh = GetOrBuildPreviewLodMesh(lodToRender);
            activeRenderedTriangles = previewMesh != null ? (int)(previewMesh.GetIndexCount(0) / 3) : 0;
            if (previewMesh != null && previewMesh.subMeshCount > 1)
            {
                int totalIdx = 0;
                for (int s = 0; s < previewMesh.subMeshCount; s++)
                    totalIdx += (int)previewMesh.GetIndexCount(s);
                activeRenderedTriangles = totalIdx / 3;
            }

            previewUtility.BeginPreview(rect, GUIStyle.none);

            Camera cam = previewUtility.camera;
            cam.fieldOfView = cameraFov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);

            Bounds b = currentSourceMesh.bounds;
            float visualDist = Mathf.Clamp(simulatedDistanceMeters, b.extents.magnitude * 1.1f, b.extents.magnitude * 40f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(500f, visualDist * 4f);

            Quaternion rot = Quaternion.Euler(orbitAngles.x, orbitAngles.y, 0f);
            cam.transform.position = b.center - (rot * Vector3.forward * visualDist);
            cam.transform.LookAt(b.center);

            if (previewUtility.lights != null && previewUtility.lights.Length > 0)
            {
                previewUtility.lights[0].intensity = 1.25f;
                previewUtility.lights[0].transform.rotation = Quaternion.Euler(40f, -45f, 0f);
            }

            if (previewMesh != null)
            {
                int subCount = previewMesh.subMeshCount;
                for (int s = 0; s < subCount; s++)
                {
                    Material mat = (currentMaterials != null && s < currentMaterials.Length && currentMaterials[s] != null)
                        ? currentMaterials[s]
                        : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");

                    previewUtility.DrawMesh(previewMesh, Matrix4x4.identity, mat, s);
                }
            }

            bool prevWireframe = GL.wireframe;
            if (showWireframe) GL.wireframe = true;
            cam.Render();
            if (showWireframe) GL.wireframe = prevWireframe;

            return previewUtility.EndPreview();
        }

        private int EvaluateAutoLodForHeight(float screenHeight, int maxLodCount, MeshLODPreset preset)
        {
            if (preset == null) return 0;
            if (screenHeight < preset.cullScreenRelativeHeight)
                return maxLodCount; // Culled

            int tiers = Mathf.Min(maxLodCount, preset.tierScreenHeights.Count);
            for (int i = 0; i < tiers; i++)
            {
                if (screenHeight >= preset.tierScreenHeights[i])
                    return i;
            }
            return Mathf.Max(0, maxLodCount - 1);
        }

        private void EnsureInitialized()
        {
            if (previewUtility == null)
            {
                previewUtility = new PreviewRenderUtility();
            }
        }

        private void ClearCache()
        {
            foreach (var kvp in cachedLodMeshes)
            {
                if (kvp.Value != null)
                    UnityEngine.Object.DestroyImmediate(kvp.Value);
            }
            cachedLodMeshes.Clear();
        }

        public void Dispose()
        {
            ClearCache();
            if (previewUtility != null)
            {
                previewUtility.Cleanup();
                previewUtility = null;
            }
        }
    }
}
#endif
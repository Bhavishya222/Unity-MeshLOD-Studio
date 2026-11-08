#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshLODStudio.Editor
{
    /// <summary>
    /// Interactive 3D LOD Preview Controller supporting up to 8 LOD levels (LOD0..LOD7),
    /// including both native Unity 6.3 Mesh LODs and synthesized far-distance decimated meshes.
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
        public int forcedPreviewLod = -1;
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

        public Mesh GetOrBuildPreviewLodMesh(int lodIndex, int minTrianglesFloor = 6)
        {
            if (currentSourceMesh == null) return null;
            int clamped = Mathf.Clamp(lodIndex, 0, 7);

            if (!cachedLodMeshes.TryGetValue(clamped, out Mesh extracted) || extracted == null)
            {
                extracted = MeshLODDetectorAndExtractor.ExtractOrSynthesizeLodMesh(
                    currentSourceMesh,
                    clamped,
                    minTrianglesFloor,
                    true);
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

            int maxPreviewTiers = activePreset != null
                ? (activePreset.synthesizeExtraFarLods ? activePreset.maxLodTiers : Mathf.Min(activePreset.maxLodTiers, currentSourceMesh.lodCount))
                : currentSourceMesh.lodCount;

            maxPreviewTiers = Mathf.Clamp(maxPreviewTiers, 1, 8);
            int minFloor = activePreset != null ? activePreset.minTrianglesFloor : 6;

            float objSize = DistanceThresholdCalculator.GetObjectWorldSize(currentSourceMesh.bounds);
            activeScreenRelativeHeight = DistanceThresholdCalculator.DistanceToScreenRelativeHeight(
                simulatedDistanceMeters,
                objSize,
                cameraFov);

            int lodToRender = forcedPreviewLod;
            if (lodToRender < 0)
            {
                lodToRender = EvaluateAutoLodForHeight(activeScreenRelativeHeight, maxPreviewTiers, activePreset);
            }

            activeRenderedLodIndex = lodToRender;
            if (lodToRender >= maxPreviewTiers)
            {
                activeRenderedTriangles = 0;
                previewUtility.BeginPreview(rect, GUIStyle.none);
                previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
                previewUtility.camera.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);
                previewUtility.camera.Render();
                return previewUtility.EndPreview();
            }

            Mesh previewMesh = GetOrBuildPreviewLodMesh(lodToRender, minFloor);
            activeRenderedTriangles = 0;
            if (previewMesh != null)
            {
                for (int s = 0; s < previewMesh.subMeshCount; s++)
                    activeRenderedTriangles += (int)previewMesh.GetIndexCount(s) / 3;
            }

            previewUtility.BeginPreview(rect, GUIStyle.none);

            Camera cam = previewUtility.camera;
            cam.fieldOfView = cameraFov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.11f, 0.12f, 0.14f, 1f);

            Bounds b = currentSourceMesh.bounds;
            float visualDist = Mathf.Clamp(simulatedDistanceMeters, b.extents.magnitude * 1.1f, b.extents.magnitude * 60f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(1000f, visualDist * 4f);

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
                return maxLodCount;

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

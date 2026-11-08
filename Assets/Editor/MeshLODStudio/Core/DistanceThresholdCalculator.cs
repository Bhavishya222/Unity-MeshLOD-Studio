#if UNITY_EDITOR
using UnityEngine;

namespace MeshLODStudio.Editor
{
    public static class DistanceThresholdCalculator
    {
        public static float GetObjectWorldSize(Bounds worldBounds)
        {
            Vector3 size = worldBounds.size;
            float maxAxis = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            return Mathf.Max(0.05f, maxAxis);
        }

        public static float DistanceToScreenRelativeHeight(
            float distanceMeters,
            float objectWorldSize,
            float verticalFovDegrees,
            bool isOrthographic = false,
            float orthoSize = 10f,
            float lodBias = 1f)
        {
            float safeSize = Mathf.Max(0.05f, objectWorldSize);
            float safeBias = Mathf.Max(0.01f, lodBias);

            if (isOrthographic)
            {
                float denom = 2f * Mathf.Max(0.01f, orthoSize);
                return Mathf.Clamp((safeSize * safeBias) / denom, 0.0005f, 0.999f);
            }

            float safeDistance = Mathf.Max(0.1f, distanceMeters);
            float halfFovRad = Mathf.Deg2Rad * Mathf.Clamp(verticalFovDegrees, 5f, 170f) * 0.5f;
            float tanHalfFov = Mathf.Tan(halfFovRad);
            float height = (safeSize * safeBias) / (2f * safeDistance * tanHalfFov);
            return Mathf.Clamp(height, 0.0005f, 0.999f);
        }

        public static float ScreenRelativeHeightToDistance(
            float screenRelativeHeight,
            float objectWorldSize,
            float verticalFovDegrees,
            float lodBias = 1f)
        {
            float safeSize = Mathf.Max(0.05f, objectWorldSize);
            float safeHeight = Mathf.Clamp(screenRelativeHeight, 0.0005f, 0.999f);
            float safeBias = Mathf.Max(0.01f, lodBias);
            float halfFovRad = Mathf.Deg2Rad * Mathf.Clamp(verticalFovDegrees, 5f, 170f) * 0.5f;
            float tanHalfFov = Mathf.Tan(halfFovRad);

            float distance = (safeSize * safeBias) / (2f * safeHeight * tanHalfFov);
            return Mathf.Max(0.1f, distance);
        }

        public static void EnforceStrictlyDescendingThresholds(float[] thresholds)
        {
            if (thresholds == null || thresholds.Length == 0) return;

            thresholds[0] = Mathf.Clamp(thresholds[0], 0.02f, 0.99f);
            for (int i = 1; i < thresholds.Length; i++)
            {
                float maxAllowed = Mathf.Max(0.0005f, thresholds[i - 1] - 0.005f);
                thresholds[i] = Mathf.Clamp(thresholds[i], 0.0005f, maxAllowed);
            }
        }
    }
}
#endif

// Copyright (c) 2011-2026 PlayCanvas Ltd.
// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Port of GSplatOctreeInstance.evaluateNodeDistances and GSplatOctree._computeBoundsExcess,
// https://github.com/playcanvas/engine/blob/v2.23.2/src/scene/gsplat-unified/

using System;
using UnityEngine;

namespace Gsplat
{
    internal static class LodDistances
    {
        static readonly double k_refTanHalfFov = Math.Tan(22.5 * Math.PI / 180.0);

        public static float[] BoundsExcess(float[] boundsFlat)
        {
            int nodeCount = boundsFlat.Length / 6;
            var excess = new float[nodeCount * 3];
            if (nodeCount == 0) return excess;
            var half = new float[nodeCount];
            var sorted = new float[nodeCount];
            for (int axis = 0; axis < 3; axis++)
            {
                for (int i = 0; i < nodeCount; i++)
                    half[i] = (boundsFlat[i * 6 + 3 + axis] - boundsFlat[i * 6 + axis]) * 0.5f;
                Array.Copy(half, sorted, nodeCount);
                Array.Sort(sorted);
                float typical = sorted[nodeCount >> 1];
                for (int i = 0; i < nodeCount; i++)
                    excess[i * 3 + axis] = Math.Max(0f, half[i] - typical);
            }
            return excess;
        }

        public static double FovScale(float verticalFovDeg, float aspect)
        {
            double a = aspect > 0 && !float.IsInfinity(aspect) ? aspect : 1;
            double tanHalfV = Math.Tan(verticalFovDeg * 0.5 * Math.PI / 180.0);
            double tanHalfH = tanHalfV * a;
            return Math.Min(tanHalfV, tanHalfH) / k_refTanHalfFov;
        }

        public static void CameraToLocal(Matrix4x4 localToWorld, Vector3 cameraWorld, Vector3 forwardWorld,
            out Vector3 cameraLocal, out Vector3 forwardLocal, out float uniformScale)
        {
            var inv = localToWorld.inverse;
            cameraLocal = inv.MultiplyPoint3x4(cameraWorld);
            forwardLocal = inv.MultiplyVector(forwardWorld).normalized;
            uniformScale = ((Vector3)localToWorld.GetColumn(0)).magnitude;
        }

        public static void Evaluate(float[] boundsFlat, float[] excess, float shrink, Vector3 cameraLocal,
            Vector3 forwardLocal, double distanceScale, float behindPenalty, double[] outDistanceSq)
        {
            double distanceScaleSq = distanceScale * distanceScale;
            double penalty = behindPenalty > 1 ? behindPenalty - 1 : 0;
            double px = cameraLocal.x, py = cameraLocal.y, pz = cameraLocal.z;
            double fx = forwardLocal.x, fy = forwardLocal.y, fz = forwardLocal.z;

            for (int n = 0; n < outDistanceSq.Length; n++)
            {
                int b = n * 6;
                double minX = boundsFlat[b], maxX = boundsFlat[b + 3];
                double minY = boundsFlat[b + 1], maxY = boundsFlat[b + 4];
                double minZ = boundsFlat[b + 2], maxZ = boundsFlat[b + 5];
                if (shrink > 0)
                {
                    int e = n * 3;
                    double sx = excess[e] * (double)shrink, sy = excess[e + 1] * (double)shrink, sz = excess[e + 2] * (double)shrink;
                    minX += sx; maxX -= sx;
                    minY += sy; maxY -= sy;
                    minZ += sz; maxZ -= sz;
                }

                double qx = px < minX ? minX : (px > maxX ? maxX : px);
                double qy = py < minY ? minY : (py > maxY ? maxY : py);
                double qz = pz < minZ ? minZ : (pz > maxZ ? maxZ : pz);
                double dx = qx - px, dy = qy - py, dz = qz - pz;
                double distanceSq = dx * dx + dy * dy + dz * dz;

                double penaltyFactor = 1;
                if (penalty > 0 && distanceSq > 0.0001)
                {
                    double dot = fx * dx + fy * dy + fz * dz;
                    if (dot < 0)
                        penaltyFactor = 1 + (-dot / Math.Sqrt(distanceSq)) * penalty;
                }

                outDistanceSq[n] = distanceSq * penaltyFactor * penaltyFactor * distanceScaleSq;
            }
        }
    }
}

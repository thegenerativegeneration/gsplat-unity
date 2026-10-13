// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    internal static class StreamedKernels
    {
        const int k_groupSize = 1024;
        static readonly int k_activeCount = Shader.PropertyToID("_ActiveCount");
        static readonly int k_rangeCount = Shader.PropertyToID("_RangeCount");
        static readonly int k_ranges = Shader.PropertyToID("_Ranges");
        static readonly int k_activeIds = Shader.PropertyToID("_ActiveIds");
        static readonly int k_matrixV = Shader.PropertyToID("_MatrixV");
        static readonly int k_orderBuffer = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_centers = Shader.PropertyToID("_Centers");
        static readonly int k_landmarkTransforms = Shader.PropertyToID("_LandmarkTransforms");
        static readonly int k_depthBuffer = Shader.PropertyToID("_DepthBuffer");

        public static void Expand(CommandBuffer cmd, ComputeShader cs, GraphicsBuffer ranges, int rangeCount,
            int activeCount, GraphicsBuffer order)
        {
            if (activeCount == 0 || rangeCount == 0) return;
            int k = cs.FindKernel("ExpandRanges");
            cmd.SetComputeIntParam(cs, k_activeCount, activeCount);
            cmd.SetComputeIntParam(cs, k_rangeCount, rangeCount);
            cmd.SetComputeBufferParam(cs, k, k_ranges, ranges);
            cmd.SetComputeBufferParam(cs, k, k_activeIds, order);
            cmd.DispatchCompute(cs, k, (activeCount + k_groupSize - 1) / k_groupSize, 1, 1);
        }

        public static void CalcDepth(CommandBuffer cmd, ComputeShader cs, Matrix4x4 view, GraphicsBuffer order,
            GraphicsBuffer centers, GraphicsBuffer landmarkTransforms, int activeCount, GraphicsBuffer depth)
        {
            if (activeCount == 0) return;
            int k = cs.FindKernel("CalcDepthStreamed");
            cmd.SetComputeIntParam(cs, k_activeCount, activeCount);
            cmd.SetComputeMatrixParam(cs, k_matrixV, view);
            cmd.SetComputeBufferParam(cs, k, k_orderBuffer, order);
            cmd.SetComputeBufferParam(cs, k, k_centers, centers);
            cmd.SetComputeBufferParam(cs, k, k_landmarkTransforms, landmarkTransforms);
            cmd.SetComputeBufferParam(cs, k, k_depthBuffer, depth);
            cmd.DispatchCompute(cs, k, (activeCount + k_groupSize - 1) / k_groupSize, 1, 1);
        }
    }
}

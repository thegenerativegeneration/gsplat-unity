// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
//
// Streamed LOD variant of GsplatSparkGlobal.hlsl: order entries are (landmark:8 | pool slot:24), _RendererOffsets is
// all zeros, and centres come from a float32 buffer instead of Spark's float16 words.

#ifndef GSPLAT_STREAMED_INCLUDED
#define GSPLAT_STREAMED_INCLUDED

#include "../GsplatSparkGlobal.hlsl"

StructuredBuffer<float4> _StreamedCenters;

bool InitStreamedSplatData(GlobalSplatSource source, out SplatCenter center, out SplatCorner corner, out float4 color)
{
    center = (SplatCenter)0;
    corner = (SplatCorner)0;
    color = float4(0, 0, 0, 0);

    float3 halfCenter, scale;
    float4 quat;
    UnpackSplat(_GlobalPackedBuffer[source.id], color, halfCenter, scale, quat);
    float3 modelCenter = _StreamedCenters[source.id].xyz;

    float4x4 modelView = mul(UNITY_MATRIX_V, _RendererTransforms[source.rendererId]);
    if (!InitCenter(modelView, modelCenter, center))
        return false;

    SplatSource tmp;
    tmp.order = source.order;
    tmp.id = source.id;
    tmp.cornerUV = source.cornerUV;
    SplatCovariance cov = CalcCovariance(quat, scale);
    return InitCorner(tmp, cov, center, corner);
}

#endif

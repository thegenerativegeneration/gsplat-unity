// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace Gsplat
{
    [Serializable]
    public sealed class GsplatStreamingParams
    {
        public const float MinBaseDistance = 0.001f;

        [Tooltip("Splats drawn across all streamed renderers. 0 or less means no budget: every landmark at its finest level.")]
        public int SplatBudget = 2_000_000;
        public bool LimitMode;
        [Min(MinBaseDistance)] public float LodBaseDistance = 5f;
        [Min(1.2f)] public float LodMultiplier = 3f;
        [Min(1f)] public float BehindPenalty = 1.5f;
        [Range(0f, 1f)] public float DistanceShrink = 0.75f;
        [Range(0, 3)] public int MaxShBands = 3;
        public int UploadSplatsPerFrame = 100_000;
        public int UnloadAfterFrames = 100;
        public int MaxDecodesInFlight = 2;

        // The inspector attributes do not bind values set from code or an old settings asset.
        public float EffectiveBaseDistance => LodBaseDistance >= MinBaseDistance ? LodBaseDistance : MinBaseDistance;

        // PlayCanvas treats a non-positive budget as no budget.
        public double EffectiveBudget => SplatBudget > 0 ? SplatBudget : double.PositiveInfinity;
    }
}

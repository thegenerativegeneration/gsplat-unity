// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;

namespace Gsplat
{
    [Serializable]
    public sealed class GsplatStreamingParams
    {
        public int SplatBudget = 2_000_000;
        public bool LimitMode;
        public float LodBaseDistance = 5f;
        public float LodMultiplier = 3f;
        public float BehindPenalty = 1.5f;
        public float DistanceShrink = 0.75f;
        public int MaxShBands = 3;
        public int UploadSplatsPerFrame = 100_000;
        public int UnloadAfterFrames = 100;
        public int MaxDecodesInFlight = 2;
    }
}

// Copyright (c) 2011-2026 PlayCanvas Ltd.
// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Port of https://github.com/playcanvas/engine/blob/v2.23.2/src/scene/gsplat-unified/gsplat-lod-table.js
//
// Per leaf and LOD band: the level drawn and its splat count. A leaf with no data at the coarse end gets an "empty
// level" (one past its coarsest data, drawing nothing); a gap uses the next finer level with data, else the next coarser.

namespace Gsplat
{
    internal sealed class LodTable
    {
        public readonly int RangeMin;
        public readonly int RangeMax;
        public readonly int Span;
        public readonly short[] BandLod;
        public readonly int[] BandCount;
        public readonly double TotalCoarsestCount;
        public readonly double TotalFinestCount;

        public LodTable(int[] counts, int leafCount, int levels, int rangeMin, int rangeMax)
        {
            RangeMin = rangeMin;
            RangeMax = rangeMax;
            Span = rangeMax - rangeMin + 1;
            BandLod = new short[leafCount * Span];
            BandCount = new int[leafCount * Span];

            for (int n = 0; n < leafCount; n++)
            {
                int row = n * Span;
                int coarsestData = -1;
                for (int lod = rangeMax; lod >= rangeMin; lod--)
                {
                    if (counts[n * levels + lod] > 0)
                    {
                        coarsestData = lod;
                        break;
                    }
                }

                if (coarsestData < 0)
                {
                    for (int b = 0; b < Span; b++) BandLod[row + b] = -1;
                    continue;
                }

                int emptyLod = coarsestData + 1;
                for (int b = 0; b < Span; b++)
                {
                    int band = rangeMin + b;
                    int lod = band;
                    if (band > coarsestData)
                    {
                        lod = emptyLod;
                    }
                    else if (counts[n * levels + band] <= 0)
                    {
                        lod = -1;
                        for (int l = band - 1; l >= rangeMin; l--)
                            if (counts[n * levels + l] > 0) { lod = l; break; }
                        if (lod < 0)
                            for (int l = band + 1; l <= coarsestData; l++)
                                if (counts[n * levels + l] > 0) { lod = l; break; }
                    }

                    BandLod[row + b] = (short)lod;
                    BandCount[row + b] = lod == emptyLod ? 0 : counts[n * levels + lod];
                }

                TotalFinestCount += BandCount[row];
                TotalCoarsestCount += BandCount[row + Span - 1];
            }
        }
    }
}

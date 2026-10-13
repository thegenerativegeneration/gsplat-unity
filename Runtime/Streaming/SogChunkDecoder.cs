// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;

namespace Gsplat
{
    internal sealed class DecodedChunk
    {
        public int Count;
        public byte ShBands;
        public Vector3[] Centers;
        public uint4[] Packed;
        public uint[] Sh1;
        public uint[] Sh2;
        public uint[] Sh3;
    }

    // Decodes one SOG unit into the streaming pool layout: float32 centres (no float16 stripes far from the origin)
    // plus Spark-packed colour, rotation, scale and SH. Runs on worker threads; touches no Unity objects.
    internal static class SogChunkDecoder
    {
        public static DecodedChunk Decode(SogData data, SourceCoordinates src, int maxShBands, CancellationToken ct)
        {
            int count = data.Count;
            byte bands = (byte)Math.Min(data.ShBands, Math.Min(maxShBands, 3));
            var chunk = new DecodedChunk
            {
                Count = count,
                ShBands = bands,
                Centers = new Vector3[count],
                Packed = new uint4[count],
                Sh1 = bands >= 1 ? new uint[count * 2] : null,
                Sh2 = bands >= 2 ? new uint[count * 4] : null,
                Sh3 = bands >= 3 ? new uint[count * 4] : null,
            };

            var (px, py, pz) = GsplatUtils.AxisSigns(src);
            float rx = py * pz, ry = px * pz, rz = px * py;
            var shBand = new float[7 * 3];

            for (int i = 0; i < count; i++)
            {
                if ((i & 0x3fff) == 0) ct.ThrowIfCancellationRequested();

                var raw = SogLoader.DecodePosition(data, i);
                chunk.Centers[i] = new Vector3(px * raw.x, py * raw.y, pz * raw.z);

                var color = SogLoader.DecodeColorLogitAlpha(data, i);
                var scale = SogLoader.DecodeScaleLog(data, i);
                var q = SogLoader.DecodeRotation(data, i);
                var rotation = new Quaternion(q.w, rx * q.x, ry * q.y, rz * q.z);
                chunk.Packed[i] = GsplatAssetSpark.PackSplat(color, Vector3.zero, scale, rotation);

                for (int band = 1; band <= bands; band++)
                {
                    SogLoader.DecodeShBand(data, i, band, shBand);
                    int bandSize = band * 2 + 1;
                    for (int k = 0; k < bandSize; k++)
                    {
                        float sign = GsplatUtils.ShSign(src, band, k);
                        shBand[k * 3 + 0] *= sign;
                        shBand[k * 3 + 1] *= sign;
                        shBand[k * 3 + 2] *= sign;
                    }
                    if (band == 1) GsplatAssetSpark.PackSH1(shBand, chunk.Sh1.AsSpan(i * 2, 2));
                    else if (band == 2) GsplatAssetSpark.PackSH2(shBand, chunk.Sh2.AsSpan(i * 4, 4));
                    else GsplatAssetSpark.PackSH3(shBand, chunk.Sh3.AsSpan(i * 4, 4));
                }
            }

            return chunk;
        }
    }
}

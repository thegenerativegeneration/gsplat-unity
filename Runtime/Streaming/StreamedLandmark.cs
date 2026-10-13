// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    // Per streamed renderer: its LOD tree, LOD table and the latest distance/selection results.
    internal sealed class StreamedLandmark
    {
        public readonly int Id;
        public readonly GsplatStreamedRenderer Renderer;
        public readonly SogLodMeta Meta;
        public readonly LodTable Table;
        public readonly float[] Excess;
        public readonly LodInstance Lod;
        public readonly byte ShBands;
        public Matrix4x4 LocalToWorld;

        public StreamedLandmark(int id, GsplatStreamedRenderer renderer, SogLodMeta meta, byte shBands)
        {
            Id = id;
            Renderer = renderer;
            Meta = meta;
            ShBands = shBands;
            var counts = new int[meta.LeafCount * meta.LodLevels];
            for (int leaf = 0; leaf < meta.LeafCount; leaf++)
                for (int lod = 0; lod < meta.LodLevels; lod++)
                    counts[leaf * meta.LodLevels + lod] = meta.Count(leaf, lod);
            Table = new LodTable(counts, meta.LeafCount, meta.LodLevels, 0, meta.LodLevels - 1);
            Excess = LodDistances.BoundsExcess(meta.Bounds);
            Lod = new LodInstance
            {
                Table = Table,
                DistanceSq = new double[meta.LeafCount],
                OptimalLod = new int[meta.LeafCount],
            };
            LocalToWorld = renderer.transform.localToWorldMatrix;
        }

        public void EvaluateDistances(Camera cam, GsplatStreamingParams p)
        {
            LodDistances.CameraToLocal(LocalToWorld, cam.transform.position, cam.transform.forward,
                out var camLocal, out var fwdLocal, out float scale);
            double distanceScale = LodDistances.FovScale(cam.fieldOfView, cam.aspect) * scale;
            LodDistances.Evaluate(Meta.Bounds, Excess, p.DistanceShrink, camLocal, fwdLocal, distanceScale,
                p.BehindPenalty, Lod.DistanceSq);
            Lod.BaseDistance = p.LodBaseDistance;
            Lod.Multiplier = Mathf.Max(1.2f, p.LodMultiplier);
        }
    }
}

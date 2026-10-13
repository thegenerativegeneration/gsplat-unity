// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class SogChunkDecoderTests
    {
        // GsplatAssetSog (PR #40) packs the same SOG into Spark words; our chunk must match it bit for bit except the
        // float16 centre, which the pool replaces with float32 centres.
        [Test]
        public void MatchesSparkAssetExceptCentres()
        {
            const SourceCoordinates src = SourceCoordinates.RUB;
            var path = Fixtures.Path("synth-folder/meta.json");
            var asset = ScriptableObject.CreateInstance<GsplatAssetSog>();
            asset.LoadFromSog(path, src);
            var chunk = SogChunkDecoder.Decode(SogLoader.Load(path), src, 3, CancellationToken.None);

            Assert.AreEqual(asset.SplatCount, (uint)chunk.Count);
            Assert.AreEqual(3, chunk.ShBands);
            for (int i = 0; i < chunk.Count; i++)
            {
                var a = asset.PackedSplats[i];
                var b = chunk.Packed[i];
                Assert.AreEqual(a.x, b.x, $"colour {i}");
                Assert.AreEqual(a.w, b.w, $"scale/angle {i}");
                Assert.AreEqual(a.z & 0xffff0000u, b.z & 0xffff0000u, $"quat {i}");
                var halfCentre = new Vector3(Mathf.HalfToFloat((ushort)(a.y & 0xffff)),
                    Mathf.HalfToFloat((ushort)(a.y >> 16)), Mathf.HalfToFloat((ushort)(a.z & 0xffff)));
                Assert.That(Vector3.Distance(halfCentre, chunk.Centers[i]), Is.LessThan(0.02f), $"centre {i}");
            }
            CollectionAssert.AreEqual(asset.PackedSH1, chunk.Sh1);
            CollectionAssert.AreEqual(asset.PackedSH2, chunk.Sh2);
            CollectionAssert.AreEqual(asset.PackedSH3, chunk.Sh3);
            Object.DestroyImmediate(asset);
        }

        [Test]
        public void BandLimitDropsHigherBands()
        {
            var chunk = SogChunkDecoder.Decode(SogLoader.Load(Fixtures.Path("synth-folder/meta.json")),
                SourceCoordinates.RUB, 1, CancellationToken.None);
            Assert.AreEqual(1, chunk.ShBands);
            Assert.NotNull(chunk.Sh1);
            Assert.IsNull(chunk.Sh2);
            Assert.IsNull(chunk.Sh3);
        }

        [Test]
        public void CancelledDecodeThrows()
        {
            var data = SogLoader.Load(Fixtures.Path("synth-folder/meta.json"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<System.OperationCanceledException>(() =>
                SogChunkDecoder.Decode(data, SourceCoordinates.RUB, 3, cts.Token));
        }
    }
}

// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Gsplat.Tests
{
    public class StreamedKernelTests
    {
        static ComputeShader Cs => GsplatSettings.Instance.StreamingCompute;

        [Test]
        public void ExpandWritesLandmarkAndPoolIndex()
        {
            using var ranges = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 16);
            using var order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 8, 4);
            ranges.SetData(new[] { new uint4(1, 100, 3, 0), new uint4(2, 5, 2, 3) });
            using var cmd = new CommandBuffer();
            StreamedKernels.Expand(cmd, Cs, ranges, 2, 5, order);
            Graphics.ExecuteCommandBuffer(cmd);
            var ids = new uint[5];
            order.GetData(ids);
            CollectionAssert.AreEqual(new uint[] { (1u << 24) | 100, (1u << 24) | 101, (1u << 24) | 102, (2u << 24) | 5, (2u << 24) | 6 }, ids);
        }

        [Test]
        public void DepthUsesLandmarkTransformAndView()
        {
            var t0 = Matrix4x4.TRS(new Vector3(0, 0, 10), Quaternion.identity, Vector3.one);
            var t1 = Matrix4x4.TRS(new Vector3(5, 0, 0), Quaternion.Euler(0, 90, 0), Vector3.one * 2);
            var view = Matrix4x4.TRS(new Vector3(0, 1, -3), Quaternion.Euler(10, 20, 0), Vector3.one).inverse;
            var centers = new[] { new Vector4(1, 2, 3, 0), new Vector4(-1, 0, 4, 0) };
            using var centerBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 16);
            using var transforms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 64);
            using var order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);
            using var depth = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);
            centerBuf.SetData(centers);
            transforms.SetData(new[] { t0, t1 });
            order.SetData(new uint[] { (1u << 24) | 0, (0u << 24) | 1 });
            using var cmd = new CommandBuffer();
            StreamedKernels.CalcDepth(cmd, Cs, view, order, centerBuf, transforms, 2, depth);
            Graphics.ExecuteCommandBuffer(cmd);
            var got = new float[2];
            depth.GetData(got);
            float want0 = view.MultiplyPoint3x4(t1.MultiplyPoint3x4(centers[0])).z;
            float want1 = view.MultiplyPoint3x4(t0.MultiplyPoint3x4(centers[1])).z;
            Assert.AreEqual(want0, got[0], 1e-4f);
            Assert.AreEqual(want1, got[1], 1e-4f);
        }

        static DecodedChunk NumberedChunk(int count, uint first)
        {
            var chunk = new DecodedChunk
            {
                Count = count, ShBands = 1,
                Centers = new Vector3[count], Packed = new uint4[count], Sh1 = new uint[count * 2],
            };
            for (int i = 0; i < count; i++)
            {
                uint v = first + (uint)i;
                chunk.Centers[i] = new Vector3(v, v + 1, v + 2);
                chunk.Packed[i] = new uint4(v, v * 3, 7, 9);
                chunk.Sh1[i * 2] = v;
                chunk.Sh1[i * 2 + 1] = v + 5;
            }
            return chunk;
        }

        static void AssertPoolHolds(SplatPool pool, DecodedChunk chunk, int start, string what)
        {
            int n = chunk.Count;
            var centers = new Vector4[n];
            var packed = new uint4[n];
            var sh1 = new uint[n * 2];
            pool.Centers.GetData(centers, 0, start, n);
            pool.Packed.GetData(packed, 0, start, n);
            pool.Sh1.GetData(sh1, 0, start * 2, n * 2);
            int badCenters = 0, badPacked = 0, badSh1 = 0;
            for (int i = 0; i < n; i++)
            {
                var c = chunk.Centers[i];
                if (centers[i] != new Vector4(c.x, c.y, c.z, 0)) badCenters++;
                if (!packed[i].Equals(chunk.Packed[i])) badPacked++;
                if (sh1[i * 2] != chunk.Sh1[i * 2] || sh1[i * 2 + 1] != chunk.Sh1[i * 2 + 1]) badSh1++;
            }
            Assert.AreEqual((0, 0, 0), (badCenters, badPacked, badSh1), $"{what}: wrong (centres, packed, sh1) of {n}");
        }

        // The loader grows the pool and uploads the next unit in the same frame, before the GPU has run the growth
        // copy; the copied splats must survive those uploads.
        [Test]
        public void PoolGrowthKeepsUploadedData([Values(3, 100_000)] int count)
        {
            var copy = GsplatSettings.Instance.GlobalMaterial.CopyBufferShader;
            using var pool = new SplatPool(count + 1, 1, copy);
            var first = NumberedChunk(count, 1);
            var second = NumberedChunk(count, 10_000_000);
            Assert.IsTrue(pool.TryAllocate(count, out int a));
            pool.Upload(first, 0, a, count);
            pool.Centers.GetData(new Vector4[1]); // the first unit was uploaded frames ago
            Assert.IsFalse(pool.TryAllocate(count, out _));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("pool grows"));
            Assert.IsTrue(pool.TryGrowFor(count));
            Assert.Greater(pool.Capacity, count + 1);
            Assert.IsTrue(pool.TryAllocate(count, out int b));
            Assert.AreEqual(count, b);
            pool.Upload(second, 0, b, count);
            AssertPoolHolds(pool, first, a, "splats uploaded before the growth");
            AssertPoolHolds(pool, second, b, "splats uploaded after the growth");
        }
    }
}

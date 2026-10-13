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

        [Test]
        public void PoolGrowthKeepsUploadedData()
        {
            var copy = GsplatSettings.Instance.GlobalMaterial.CopyBufferShader;
            using var pool = new SplatPool(4, 1, copy);
            var chunk = new DecodedChunk
            {
                Count = 3, ShBands = 1,
                Centers = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9) },
                Packed = new[] { new uint4(1, 2, 3, 4), new uint4(5, 6, 7, 8), new uint4(9, 10, 11, 12) },
                Sh1 = new uint[] { 1, 2, 3, 4, 5, 6 }
            };
            Assert.IsTrue(pool.TryAllocate(3, out int a));
            pool.Upload(chunk, 0, a, 3);
            Assert.IsFalse(pool.TryAllocate(3, out _));
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("pool grows"));
            Assert.IsTrue(pool.TryGrowFor(3));
            Assert.IsTrue(pool.TryAllocate(3, out int b));
            Assert.AreEqual(3, b);
            Assert.Greater(pool.Capacity, 4);
            var centers = new Vector4[3];
            pool.Centers.GetData(centers, 0, a, 3);
            Assert.AreEqual(new Vector4(4, 5, 6, 0), centers[1]);
            var sh = new uint[6];
            pool.Sh1.GetData(sh, 0, a * 2, 6);
            CollectionAssert.AreEqual(chunk.Sh1, sh);
        }
    }
}

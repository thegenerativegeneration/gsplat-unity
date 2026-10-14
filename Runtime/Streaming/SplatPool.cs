// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using Unity.Mathematics;
using UnityEngine;

namespace Gsplat
{
    // One set of GPU buffers for every streamed landmark; each loaded SOG unit owns a contiguous slot range.
    internal sealed class SplatPool : IDisposable
    {
        public const int MaxSlots = (1 << 24) - 1;
        static readonly int k_srcUint4 = Shader.PropertyToID("_SrcUint4");
        static readonly int k_dstUint4 = Shader.PropertyToID("_DstUint4");
        static readonly int k_srcUint2 = Shader.PropertyToID("_SrcUint2");
        static readonly int k_dstUint2 = Shader.PropertyToID("_DstUint2");
        static readonly int k_srcElementCount = Shader.PropertyToID("_SrcElementCount");
        static readonly int k_dstElementOffset = Shader.PropertyToID("_DstElementOffset");

        readonly ComputeShader m_copy;
        readonly BlockAllocator m_allocator;
        Vector4[] m_centerScratch = new Vector4[0];

        public int Capacity => m_allocator.Capacity;
        public int Used => m_allocator.Used;
        public int ShBands { get; }
        public GraphicsBuffer Centers { get; private set; }
        public GraphicsBuffer Packed { get; private set; }
        public GraphicsBuffer Sh1 { get; private set; }
        public GraphicsBuffer Sh2 { get; private set; }
        public GraphicsBuffer Sh3 { get; private set; }
        public event Action Grown;

        public SplatPool(int capacity, int shBands, ComputeShader copyShader)
        {
            ShBands = Math.Min(shBands, 3);
            m_copy = copyShader;
            m_allocator = new BlockAllocator(Math.Min(capacity, MaxSlots));
            Create(m_allocator.Capacity);
        }

        public bool TryAllocate(int count, out int start) => m_allocator.TryAllocate(count, out start);

        // Appends free space at the tail (helps against fragmentation too); false at the 24-bit id limit.
        public bool TryGrowFor(int count)
        {
            int target = (int)Math.Min(MaxSlots, Math.Max((long)Capacity * 3 / 2, (long)Capacity + count));
            if (target == Capacity) return false;
            Debug.LogWarning($"[Gsplat] streaming pool grows from {Capacity} to {target} splats; raise the budget headroom if this repeats.");
            Grow(target);
            return true;
        }

        public void Free(int start) => m_allocator.Free(start);

        public void Upload(DecodedChunk chunk, int source, int destination, int count)
        {
            if (m_centerScratch.Length < count) m_centerScratch = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                var c = chunk.Centers[source + i];
                m_centerScratch[i] = new Vector4(c.x, c.y, c.z, 0);
            }
            Centers.SetData(m_centerScratch, 0, destination, count);
            Packed.SetData(chunk.Packed, source, destination, count);
            if (ShBands >= 1 && chunk.Sh1 != null) Sh1.SetData(chunk.Sh1, source * 2, destination * 2, count * 2);
            if (ShBands >= 2 && chunk.Sh2 != null) Sh2.SetData(chunk.Sh2, source * 4, destination * 4, count * 4);
            if (ShBands >= 3 && chunk.Sh3 != null) Sh3.SetData(chunk.Sh3, source * 4, destination * 4, count * 4);
        }

        void Create(int capacity)
        {
            int n = Math.Max(1, capacity);
            Centers = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            Packed = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16);
            Sh1 = ShBands >= 1 ? new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 8) : null;
            Sh2 = ShBands >= 2 ? new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16) : null;
            Sh3 = ShBands >= 3 ? new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, 16) : null;
        }

        void Grow(int capacity)
        {
            var (c, p, s1, s2, s3) = (Centers, Packed, Sh1, Sh2, Sh3);
            int old = m_allocator.Capacity;
            Create(capacity);
            try
            {
                Copy4(c, Centers, old);
                Copy4(p, Packed, old);
                if (s1 != null) Copy2(s1, Sh1, old);
                if (s2 != null) Copy4(s2, Sh2, old);
                if (s3 != null) Copy4(s3, Sh3, old);
                WaitForCopies();
            }
            finally
            {
                c.Dispose(); p.Dispose(); s1?.Dispose(); s2?.Dispose(); s3?.Dispose();
            }
            m_allocator.Grow(capacity);
            Grown?.Invoke();
        }

        // On Metal a SetData that follows a copy dispatch the GPU has not run yet can drop the copied contents
        // (of two uploads in a row, the second buffer loses its copy). Uploads follow growth in the same frame, so
        // stall until the copies are done; one-element reads suffice, and growth is rare and logged.
        void WaitForCopies()
        {
            Centers.GetData(new Vector4[1], 0, 0, 1);
            Packed.GetData(new uint4[1], 0, 0, 1);
            Sh1?.GetData(new uint[2], 0, 0, 2);
            Sh2?.GetData(new uint[4], 0, 0, 4);
            Sh3?.GetData(new uint[4], 0, 0, 4);
        }

        void Copy4(GraphicsBuffer src, GraphicsBuffer dst, int count) => Copy("CopyUint4", k_srcUint4, k_dstUint4, src, dst, count);
        void Copy2(GraphicsBuffer src, GraphicsBuffer dst, int count) => Copy("CopyUint2", k_srcUint2, k_dstUint2, src, dst, count);

        void Copy(string kernel, int srcId, int dstId, GraphicsBuffer src, GraphicsBuffer dst, int count)
        {
            if (count <= 0) return;
            int k = m_copy.FindKernel(kernel);
            m_copy.SetInt(k_srcElementCount, count);
            m_copy.SetInt(k_dstElementOffset, 0);
            m_copy.SetBuffer(k, srcId, src);
            m_copy.SetBuffer(k, dstId, dst);
            m_copy.Dispatch(k, (count + 255) / 256, 1, 1);
        }

        public void Dispose()
        {
            Centers?.Dispose(); Packed?.Dispose(); Sh1?.Dispose(); Sh2?.Dispose(); Sh3?.Dispose();
            Centers = Packed = Sh1 = Sh2 = Sh3 = null;
        }
    }
}

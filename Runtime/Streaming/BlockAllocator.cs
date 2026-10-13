// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Gsplat
{
    // First-fit allocator over a range of pool slots; freed neighbours are merged.
    internal sealed class BlockAllocator
    {
        readonly SortedList<int, int> m_free = new(); // start -> size
        readonly Dictionary<int, int> m_used = new();  // start -> size

        public int Capacity { get; private set; }
        public int Used { get; private set; }

        public BlockAllocator(int capacity)
        {
            Capacity = capacity;
            if (capacity > 0) m_free.Add(0, capacity);
        }

        public bool TryAllocate(int count, out int start)
        {
            start = -1;
            if (count <= 0) return false;
            for (int i = 0; i < m_free.Count; i++)
            {
                int s = m_free.Keys[i], size = m_free.Values[i];
                if (size < count) continue;
                m_free.RemoveAt(i);
                if (size > count) m_free.Add(s + count, size - count);
                m_used.Add(s, count);
                Used += count;
                start = s;
                return true;
            }
            return false;
        }

        public void Free(int start)
        {
            if (!m_used.Remove(start, out int size))
                throw new ArgumentException($"no block starts at {start}", nameof(start));
            Used -= size;
            Insert(start, size);
        }

        public void Grow(int newCapacity)
        {
            if (newCapacity <= Capacity) return;
            Insert(Capacity, newCapacity - Capacity);
            Capacity = newCapacity;
        }

        void Insert(int start, int size)
        {
            m_free.Add(start, size);
            int i = m_free.IndexOfKey(start);
            if (i + 1 < m_free.Count && start + size == m_free.Keys[i + 1])
            {
                size += m_free.Values[i + 1];
                m_free.RemoveAt(i + 1);
                m_free[start] = size;
            }
            if (i > 0 && m_free.Keys[i - 1] + m_free.Values[i - 1] == start)
            {
                int prev = m_free.Keys[i - 1];
                m_free[prev] = m_free.Values[i - 1] + size;
                m_free.RemoveAt(i);
            }
        }
    }
}

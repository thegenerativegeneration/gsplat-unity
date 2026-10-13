// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using NUnit.Framework;

namespace Gsplat.Tests
{
    public class BlockAllocatorTests
    {
        [Test]
        public void FirstFitAndCoalesce()
        {
            var a = new BlockAllocator(100);
            Assert.IsTrue(a.TryAllocate(30, out int s0));
            Assert.IsTrue(a.TryAllocate(30, out int s1));
            Assert.IsTrue(a.TryAllocate(30, out int s2));
            Assert.AreEqual((0, 30, 60), (s0, s1, s2));
            Assert.IsFalse(a.TryAllocate(20, out _));
            a.Free(s0);
            a.Free(s1);
            Assert.IsTrue(a.TryAllocate(60, out int big), "freed neighbours coalesce");
            Assert.AreEqual(0, big);
            Assert.AreEqual(90, a.Used);
        }

        [Test]
        public void GrowAppendsFreeSpaceJoinedToTail()
        {
            var a = new BlockAllocator(50);
            Assert.IsTrue(a.TryAllocate(40, out _));
            a.Grow(100);
            Assert.IsTrue(a.TryAllocate(60, out int s));
            Assert.AreEqual(40, s);
            Assert.AreEqual(100, a.Capacity);
        }

        [Test]
        public void ZeroAndUnknownAreRejected()
        {
            var a = new BlockAllocator(10);
            Assert.IsFalse(a.TryAllocate(0, out _));
            Assert.Throws<System.ArgumentException>(() => a.Free(3));
        }

        [Test]
        public void FragmentedSpaceIsNotEnough()
        {
            var a = new BlockAllocator(30);
            a.TryAllocate(10, out int x);
            a.TryAllocate(10, out _);
            a.TryAllocate(10, out int z);
            a.Free(x);
            a.Free(z);
            Assert.IsFalse(a.TryAllocate(20, out _));
            Assert.AreEqual(10, a.Used);
        }
    }
}

// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class ResidencyControllerTests
    {
        // Two leaves, three levels, one file per (leaf, level): file = leaf * 3 + lod.
        static SogLodMeta Meta()
        {
            var m = new SogLodMeta
            {
                LodLevels = 3, LeafCount = 2, Files = new string[6], FileLod = new int[6], FileCount = new int[6],
                Bounds = new float[12], Ranges = new LodRange[6]
            };
            for (int leaf = 0; leaf < 2; leaf++)
            for (int lod = 0; lod < 3; lod++)
            {
                int f = leaf * 3 + lod;
                m.Files[f] = $"{lod}_{leaf}/meta.json";
                m.FileLod[f] = lod;
                m.FileCount[f] = 100 >> lod;
                m.Ranges[leaf * 3 + lod] = new LodRange(f, 0, 100 >> lod);
            }
            return m;
        }

        static List<FileKey> Drain(ResidencyController r)
        {
            var started = new List<FileKey>();
            while (r.TryNextLoad(out var k)) started.Add(k);
            return started;
        }

        [Test]
        public void CoarseFirstThenNearestAndTwoInFlight()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, 0 }, new[] { 100.0, 4.0 });
            var started = Drain(r);
            Assert.AreEqual(2, started.Count, "max two in flight");
            Assert.AreEqual(new FileKey(0, 3), started[0], "nearest leaf first, both draw nothing (tier 2), same level");
            Assert.AreEqual(new FileKey(0, 0), started[1]);
            Assert.AreEqual(-1, r.DrawnLod(0, 0));
            r.MarkLoaded(started[0]);
            Assert.IsTrue(r.ConsumeDrawnChanged());
            Assert.AreEqual(0, r.DrawnLod(0, 1));
        }

        [Test]
        public void OldLevelDrawsUntilNewIsResident()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 2, 2 }, new[] { 1.0, 1.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            Assert.AreEqual(2, r.DrawnLod(0, 0));

            r.SetWanted(0, new[] { 0, 2 }, new[] { 1.0, 1.0 });
            var started = Drain(r);
            CollectionAssert.AreEqual(new[] { new FileKey(0, 0) }, started);
            Assert.AreEqual(2, r.DrawnLod(0, 0), "keeps the coarse level while level 0 loads");
            r.MarkLoaded(started[0]);
            Assert.AreEqual(0, r.DrawnLod(0, 0));
        }

        [Test]
        public void UnreferencedFilesUnloadAfterCooldown()
        {
            var r = new ResidencyController(unloadAfterFrames: 3);
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 2, 2 }, new[] { 1.0, 1.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            r.SetWanted(0, new[] { 1, 1 }, new[] { 1.0, 1.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            var unloads = new List<FileKey>();
            r.Tick(unloads);
            r.Tick(unloads);
            Assert.IsEmpty(unloads);
            r.Tick(unloads);
            CollectionAssert.AreEquivalent(new[] { new FileKey(0, 2), new FileKey(0, 5) }, unloads);
            Assert.IsFalse(r.IsResident(new FileKey(0, 2)));
        }

        [Test]
        public void EvictIdleDropsOnlyUnreferencedFiles()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 2, 2 }, new[] { 1.0, 1.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            r.SetWanted(0, new[] { 2, 1 }, new[] { 1.0, 1.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            var unloads = new List<FileKey>();
            r.EvictIdle(unloads);
            CollectionAssert.AreEqual(new[] { new FileKey(0, 5) }, unloads);
            Assert.IsTrue(r.IsResident(new FileKey(0, 2)));
        }

        [Test]
        public void FailedFileFallsBackToNextCoarserLevel()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            Assert.IsTrue(r.TryNextLoad(out var k));
            r.MarkFailed(k);
            Assert.IsTrue(r.TryNextLoad(out var next));
            Assert.AreEqual(new FileKey(0, 1), next);
        }

        [Test]
        public void TeleportKeepsDrawingAndQueuesCoarseFirst()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, 2 }, new[] { 1.0, 900.0 });
            foreach (var k in Drain(r)) r.MarkLoaded(k);
            // teleport: leaf 1 is now near and wants level 0, leaf 0 is far and wants level 2
            r.SetWanted(0, new[] { 2, 0 }, new[] { 900.0, 1.0 });
            var started = Drain(r);
            Assert.AreEqual(2, started.Count);
            Assert.AreEqual(new FileKey(0, 2), started[0], "coarser level first");
            Assert.AreEqual(new FileKey(0, 3), started[1]);
            Assert.AreEqual(0, r.DrawnLod(0, 0));
            Assert.AreEqual(2, r.DrawnLod(0, 1));
        }

        [Test]
        public void RemovingALandmarkReturnsItsFiles()
        {
            var r = new ResidencyController();
            r.AddLandmark(4, Meta());
            r.SetWanted(4, new[] { 2, 2 }, new[] { 1.0, 1.0 });
            var started = Drain(r);
            r.MarkLoaded(started[0]);
            var files = r.RemoveLandmark(4);
            CollectionAssert.AreEquivalent(started, files);
            Assert.AreEqual(0, r.InFlight);
            Assert.AreEqual(0, r.ResidentCount);
        }
    }
}

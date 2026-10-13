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

        static void LoadAll(ResidencyController r)
        {
            for (var s = Drain(r); s.Count > 0; s = Drain(r))
                foreach (var k in s) r.MarkLoaded(k);
        }

        // File 0: leaf 0 level 0, file 1: level 1 shared by both leaves, file 2: leaf 1 level 0. No level 2.
        static SogLodMeta SharedMeta()
        {
            var m = new SogLodMeta
            {
                LodLevels = 3, LeafCount = 2, Files = new[] { "a", "b", "c" }, FileLod = new[] { 0, 1, 0 },
                FileCount = new[] { 100, 100, 100 }, Bounds = new float[12], Ranges = new LodRange[6]
            };
            m.Ranges[0] = new LodRange(0, 0, 100);
            m.Ranges[1] = new LodRange(1, 0, 50);
            m.Ranges[3] = new LodRange(2, 0, 100);
            m.Ranges[4] = new LodRange(1, 50, 50);
            return m;
        }

        [Test]
        public void UnderfillLoadsCoarsestLevelsBeforeFinerOnesAndTwoInFlight()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, 0 }, new[] { 100.0, 4.0 });
            var started = Drain(r);
            Assert.AreEqual(2, started.Count, "max two in flight");
            Assert.AreEqual(new FileKey(0, 5), started[0], "underfill: coarsest files first, nearest leaf first");
            Assert.AreEqual(new FileKey(0, 2), started[1]);
            Assert.AreEqual(-1, r.DrawnLod(0, 0));
            r.MarkLoaded(started[0]);
            Assert.IsTrue(r.ConsumeDrawnChanged());
            Assert.AreEqual(2, r.DrawnLod(0, 1), "the coarse file fills the leaf until level 0 lands");
            Assert.AreEqual(new FileKey(0, 0), Drain(r)[0], "leaf 0 still draws nothing (tier 2), leaf 1 only switches (tier 1)");
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
            var r = new ResidencyController(maxInFlight: 1);
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            Assert.IsTrue(r.TryNextLoad(out var coarse));
            Assert.AreEqual(new FileKey(0, 2), coarse, "underfill goes first");
            r.MarkLoaded(coarse);
            Assert.IsTrue(r.TryNextLoad(out var k));
            Assert.AreEqual(new FileKey(0, 0), k);
            r.MarkFailed(k);
            Assert.IsTrue(r.TryNextLoad(out var next));
            Assert.AreEqual(new FileKey(0, 1), next);
            Assert.AreEqual(2, r.DrawnLod(0, 0));
        }

        [Test]
        public void FailedCoarseLevelKeepsDrawingFinerResident()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            LoadAll(r);
            Assert.AreEqual(0, r.DrawnLod(0, 0));
            r.EvictIdle(new List<FileKey>());
            r.SetWanted(0, new[] { 2, -1 }, new[] { 1.0, 1.0 });
            Assert.IsTrue(r.TryNextLoad(out var k));
            Assert.AreEqual(new FileKey(0, 2), k);
            r.MarkFailed(k);
            Assert.AreEqual(0, r.DrawnLod(0, 0), "keeps the finer resident level");
        }

        [Test]
        public void SharedFileIsReferencedByEveryLeafUsingIt()
        {
            var r = new ResidencyController(unloadAfterFrames: 3);
            r.AddLandmark(0, SharedMeta());
            r.SetWanted(0, new[] { 1, 1 }, new[] { 1.0, 1.0 });
            var started = Drain(r);
            CollectionAssert.AreEqual(new[] { new FileKey(0, 1) }, started, "loads once");
            r.MarkLoaded(started[0]);

            r.SetWanted(0, new[] { 1, 0 }, new[] { 1.0, 1.0 });
            LoadAll(r);
            Assert.AreEqual(0, r.DrawnLod(0, 1));
            var unloads = new List<FileKey>();
            for (int i = 0; i < 6; i++) r.Tick(unloads);
            Assert.IsEmpty(unloads);
            Assert.IsTrue(r.IsResident(new FileKey(0, 1)));

            r.SetWanted(0, new[] { 0, 0 }, new[] { 1.0, 1.0 });
            LoadAll(r);
            for (int i = 0; i < 3; i++) r.Tick(unloads);
            CollectionAssert.AreEqual(new[] { new FileKey(0, 1) }, unloads);
        }

        [Test]
        public void LeafDrawingNothingLoadsBeforeLeafSwitching()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            LoadAll(r);
            r.EvictIdle(new List<FileKey>());
            // leaf 0 switches to a coarser, nearer file (tier 1); leaf 1 draws nothing and is far (tier 2)
            r.SetWanted(0, new[] { 2, 0 }, new[] { 1.0, 900.0 });
            var started = Drain(r);
            Assert.AreEqual(new FileKey(0, 5), started[0]);
            Assert.AreEqual(new FileKey(0, 3), started[1]);
            Assert.AreEqual(0, r.DrawnLod(0, 0));
        }

        [Test]
        public void UnderfillLoadsCoarsestLevelFirst()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            Assert.IsTrue(r.TryNextLoad(out var k));
            Assert.AreEqual(new FileKey(0, 2), k);
            r.MarkLoaded(k);
            Assert.AreEqual(2, r.DrawnLod(0, 0));
        }

        [Test]
        public void LoadedButUnwantedFileCoolsDown()
        {
            var r = new ResidencyController(unloadAfterFrames: 3);
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, -1 }, new[] { 1.0, 1.0 });
            LoadAll(r);
            var unloads = new List<FileKey>();
            r.Tick(unloads);
            r.Tick(unloads);
            Assert.IsEmpty(unloads);
            r.Tick(unloads);
            CollectionAssert.AreEqual(new[] { new FileKey(0, 2) }, unloads);
            Assert.IsTrue(r.IsResident(new FileKey(0, 0)));
        }

        [Test]
        public void CancelledLoadIsRequeued()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 2, -1 }, new[] { 1.0, 1.0 });
            Assert.IsTrue(r.TryNextLoad(out var k));
            Assert.AreEqual(1, r.InFlight);
            r.MarkCancelled(k);
            Assert.AreEqual(0, r.InFlight);
            Assert.IsFalse(r.IsResident(k));
            Assert.IsTrue(r.TryNextLoad(out var again));
            Assert.AreEqual(k, again);
        }

        [Test]
        public void TeleportKeepsDrawingAndQueuesCoarseFirst()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, Meta());
            r.SetWanted(0, new[] { 0, 2 }, new[] { 1.0, 900.0 });
            LoadAll(r);
            // teleport: leaf 1 is now near and wants level 0, leaf 0 is far and wants level 1
            r.SetWanted(0, new[] { 1, 0 }, new[] { 900.0, 1.0 });
            var started = Drain(r);
            Assert.AreEqual(2, started.Count);
            Assert.AreEqual(new FileKey(0, 1), started[0], "coarser level first");
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

        [Test]
        public void EmptyLevelDrawsNothing()
        {
            var r = new ResidencyController();
            r.AddLandmark(0, SharedMeta());
            r.SetWanted(0, new[] { 2, 2 }, new[] { 1.0, 1.0 });
            Assert.AreEqual(0, r.QueuedCount);
            Assert.IsFalse(r.TryNextLoad(out _));
            Assert.AreEqual(-1, r.DrawnLod(0, 0));
            Assert.AreEqual(-1, r.DrawnLod(0, 1));
        }

        [Test]
        public void SharedFileTakesTheBestPriorityOfTheLeavesWantingIt()
        {
            // leaf 0: level 0 = file 3, level 1 = file 0 (shared with leaf 1); leaf 2: level 0 = file 1
            var m = new SogLodMeta
            {
                LodLevels = 2, LeafCount = 3, Files = new[] { "s", "c", "x", "a" }, FileLod = new[] { 1, 0, 0, 0 },
                FileCount = new[] { 100, 100, 100, 100 }, Bounds = new float[18], Ranges = new LodRange[6]
            };
            m.Ranges[0] = new LodRange(3, 0, 100);
            m.Ranges[1] = new LodRange(0, 0, 50);
            m.Ranges[3] = new LodRange(0, 50, 50);
            m.Ranges[4] = new LodRange(1, 0, 100);
            var r = new ResidencyController(maxInFlight: 1);
            r.AddLandmark(0, m);
            r.SetWanted(0, new[] { 0, -1, -1 }, new[] { 1.0, 1.0, 1.0 });
            LoadAll(r);
            r.EvictIdle(new List<FileKey>());
            Assert.AreEqual(0, r.DrawnLod(0, 0));

            // file 0 is offered by leaf 0 (tier 1) and leaf 1 (tier 2, far); file 1 by leaf 2 (tier 2, level 0)
            r.SetWanted(0, new[] { 1, 1, 0 }, new[] { 1.0, 900.0, 5.0 });
            Assert.IsTrue(r.TryNextLoad(out var first));
            Assert.AreEqual(new FileKey(0, 0), first, "tier 2 from leaf 1 wins over the tier 1 offer, and over file 1 (coarser)");
        }
    }
}

// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class UploadSchedulerTests
    {
        [Test]
        public void SplitsJobsAcrossFramesUnderTheBudget()
        {
            var s = new UploadScheduler();
            s.Enqueue("a", 250, 1000);
            s.Enqueue("b", 30, 5000);
            var steps = new List<UploadStep>();

            Assert.AreEqual(100, s.Step(100, steps));
            Assert.AreEqual(1, steps.Count);
            Assert.AreEqual((0, 1000, 100, false), (steps[0].Source, steps[0].Destination, steps[0].Count, steps[0].Completes));

            steps.Clear();
            s.Step(100, steps);
            steps.Clear();
            Assert.AreEqual(80, s.Step(100, steps));
            Assert.AreEqual(2, steps.Count);
            Assert.AreEqual((200, 1200, 50, true), (steps[0].Source, steps[0].Destination, steps[0].Count, steps[0].Completes));
            Assert.AreEqual(((object)"b", 0, 5000, 30, true), (steps[1].Tag, steps[1].Source, steps[1].Destination, steps[1].Count, steps[1].Completes));
            Assert.IsTrue(s.IsEmpty);
        }

        [Test]
        public void CancelDropsTheRest()
        {
            var s = new UploadScheduler();
            s.Enqueue("a", 250, 0);
            var steps = new List<UploadStep>();
            s.Step(100, steps);
            s.Cancel("a");
            steps.Clear();
            Assert.AreEqual(0, s.Step(100, steps));
            Assert.IsTrue(s.IsEmpty);
        }
    }
}

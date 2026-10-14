// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class LodSelectionGoldenTests
    {
        static List<object> s_cases;

        static List<object> Cases()
        {
            if (s_cases != null) return s_cases;
            var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Fixtures.Path("lod-golden.json")));
            return s_cases = (List<object>)root["cases"];
        }

        static IEnumerable<int> CaseIndices() => Enumerable.Range(0, Cases().Count);

        static float[] F(object o) => ((List<object>)o).Select(v => (float)(double)v).ToArray();
        static double[] D(object o) => ((List<object>)o).Select(v => (double)v).ToArray();
        static int[] I(object o) => ((List<object>)o).Select(v => (int)(double)v).ToArray();
        static Vector3 V(object o) { var a = F(o); return new Vector3(a[0], a[1], a[2]); }

        [TestCaseSource(nameof(CaseIndices))]
        public void MatchesEngine(int index)
        {
            var c = (Dictionary<string, object>)Cases()[index];
            var cam = (Dictionary<string, object>)c["camera"];
            var instances = new List<LodInstance>();
            var expected = new List<int[]>();
            foreach (Dictionary<string, object> i in (List<object>)c["instances"])
            {
                int levels = (int)(double)i["levels"];
                var bounds = F(i["bounds"]);
                var counts = I(i["counts"]);
                int leaves = bounds.Length / 6;
                var excess = LodDistances.BoundsExcess(bounds);
                CollectionAssert.AreEqual(F(i["excess"]), excess, $"{c["name"]}: excess");
                var dist = new double[leaves];
                LodDistances.Evaluate(bounds, excess, (float)(double)c["shrink"], V(cam["pos"]), V(cam["fwd"]),
                    (double)cam["distanceScale"], (float)(double)c["behindPenalty"], dist);
                var want = D(i["distanceSq"]);
                for (int n = 0; n < leaves; n++)
                    Assert.AreEqual(want[n], dist[n], 1e-9 * System.Math.Max(1, want[n]), $"{c["name"]}: distance {n}");
                instances.Add(new LodInstance
                {
                    Table = new LodTable(counts, leaves, levels, 0, levels - 1),
                    DistanceSq = want,
                    BaseDistance = (float)(double)i["baseDistance"],
                    Multiplier = (float)(double)i["multiplier"],
                    OptimalLod = new int[leaves]
                });
                expected.Add(I(i["optimalLod"]));
            }

            new LodBudgetBalancer().Balance(instances, (double)c["budget"], (bool)c["limit"]);
            for (int k = 0; k < instances.Count; k++)
                CollectionAssert.AreEqual(expected[k], instances[k].OptimalLod, $"{c["name"]}: levels of instance {k}");
        }

        [Test]
        public void CameraInsideALeafIsFinite()
        {
            var bounds = new float[] { -1, -1, -1, 1, 1, 1, 10, 0, 0, 12, 2, 2 };
            var dist = new double[2];
            LodDistances.Evaluate(bounds, LodDistances.BoundsExcess(bounds), 0.75f, Vector3.zero, Vector3.forward, 1, 1.5f, dist);
            Assert.AreEqual(0, dist[0]);
            var inst = new LodInstance
            {
                Table = new LodTable(new[] { 100, 10, 50, 5 }, 2, 2, 0, 1), DistanceSq = dist, OptimalLod = new int[2]
            };
            new LodBudgetBalancer().Balance(new[] { inst }, 120, false);
            Assert.AreEqual(0, inst.OptimalLod[0]);
            Assert.That(inst.OptimalLod[1], Is.InRange(0, 1));
        }

        [TestCase(0)]
        [TestCase(-5)]
        public void NonPositiveBudgetMeansNoBudget(int budget)
        {
            var settings = new GsplatStreamingParams { SplatBudget = budget };
            Assert.AreEqual(double.PositiveInfinity, settings.EffectiveBudget);
            var inst = new LodInstance
            {
                Table = new LodTable(new[] { 100, 10, 50, 5 }, 2, 2, 0, 1), DistanceSq = new double[] { 1e6, 1e6 },
                OptimalLod = new int[2]
            };
            new LodBudgetBalancer().Balance(new[] { inst }, settings.EffectiveBudget, false);
            CollectionAssert.AreEqual(new[] { 0, 0 }, inst.OptimalLod, "finest level everywhere");
        }

        [TestCase(0f)]
        [TestCase(-3f)]
        [TestCase(float.NaN)]
        public void InvalidBaseDistanceIsClamped(float value)
        {
            var settings = new GsplatStreamingParams { LodBaseDistance = value };
            Assert.AreEqual(GsplatStreamingParams.MinBaseDistance, settings.EffectiveBaseDistance);
            Assert.AreEqual(5f, new GsplatStreamingParams().EffectiveBaseDistance);
        }
    }
}

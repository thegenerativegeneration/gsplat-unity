// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class LodCameraTests
    {
        [Test]
        public void MovedRotatedScaledLandmarkGivesWorldDistances()
        {
            var bounds = new float[] { 2, 0, 2, 4, 2, 4 };
            var excess = LodDistances.BoundsExcess(bounds);
            var reference = new double[1];
            LodDistances.Evaluate(bounds, excess, 0f, new Vector3(0, 1, 0), Vector3.forward, 1, 1f, reference);

            var trs = Matrix4x4.TRS(new Vector3(100, -5, 30), Quaternion.Euler(0, 73, 0), Vector3.one * 2f);
            var camWorld = trs.MultiplyPoint3x4(new Vector3(0, 1, 0));
            var fwdWorld = trs.MultiplyVector(Vector3.forward).normalized;
            LodDistances.CameraToLocal(trs, camWorld, fwdWorld, out var camLocal, out var fwdLocal, out float scale);
            Assert.AreEqual(2f, scale, 1e-5f);
            var moved = new double[1];
            LodDistances.Evaluate(bounds, excess, 0f, camLocal, fwdLocal, scale, 1f, moved);
            Assert.AreEqual(reference[0] * 4.0, moved[0], 1e-3);
        }

        [TestCase(45f, 16f / 9f, 1.0)]
        [TestCase(90f, 16f / 9f, 2.414213)]
        [TestCase(60f, 0.5f, 0.697)]
        public void FovScaleUsesNarrowerHalfAngle(float vfov, float aspect, double expected)
        {
            Assert.AreEqual(expected, LodDistances.FovScale(vfov, aspect), 2e-3);
        }
    }
}

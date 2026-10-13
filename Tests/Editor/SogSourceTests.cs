// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Gsplat.Tests
{
    public class SogSourceTests
    {
        // splat-transform's own SOG -> PLY decode is the reference; both sides decode the same bytes,
        // so only float rounding separates them.
        const float k_tol = 1e-4f;

        static void AssertMatchesRoundTrip(SogData sog, PlyData reference)
        {
            Assert.AreEqual(reference.Count, sog.Count);
            for (int i = 0; i < sog.Count; i += 37)
            {
                var p = SogLoader.DecodePosition(sog, i);
                Assert.That(Vector3.Distance(p, reference.Positions[i]), Is.LessThan(1e-3f), $"position {i}");
                var s = SogLoader.DecodeScaleLog(sog, i);
                Assert.That(Vector3.Distance(s, reference.LogScale[i]), Is.LessThan(k_tol), $"scale {i}");
                var c = SogLoader.DecodeColorLinearAlpha(sog, i);
                Assert.That(Mathf.Abs(c.x - reference.Dc[i].x), Is.LessThan(k_tol), $"dc {i}");
                float alpha = 1f / (1f + Mathf.Exp(-reference.Opacity[i]));
                Assert.That(Mathf.Abs(c.w - alpha), Is.LessThan(1f / 255f), $"alpha {i}");
                var q = SogLoader.DecodeRotation(sog, i);
                var r = reference.Rot[i].normalized;
                float dot = Mathf.Abs(q.w * r.x + q.x * r.y + q.y * r.z + q.z * r.w);
                Assert.That(dot, Is.GreaterThan(0.9999f), $"rotation {i}");
            }
        }

        [Test]
        public void ZipBundleDecodesLikeSplatTransform()
        {
            var sog = SogLoader.Load(Fixtures.Path("synth.sog"));
            AssertMatchesRoundTrip(sog, Fixtures.ReadPly("synth-roundtrip.ply"));
        }

        [Test]
        public void FolderDecodesLikeSplatTransform()
        {
            var sog = SogLoader.Load(Fixtures.Path("synth-folder/meta.json"));
            AssertMatchesRoundTrip(sog, Fixtures.ReadPly("synth-roundtrip.ply"));
            Assert.AreEqual(3, sog.ShBands);
        }

        [Test]
        public void ShMatchesRoundTrip()
        {
            var sog = SogLoader.Load(Fixtures.Path("synth-folder/meta.json"));
            var reference = Fixtures.ReadPly("synth-roundtrip.ply");
            var band = new float[7 * 3];
            int i = 101;
            SogLoader.DecodeShBand(sog, i, 1, band);
            // PLY f_rest is channel-major: r0..r14, g0..g14, b0..b14
            for (int k = 0; k < 3; k++)
            {
                Assert.That(Mathf.Abs(band[k * 3 + 0] - reference.Rest[i * 45 + k]), Is.LessThan(k_tol));
                Assert.That(Mathf.Abs(band[k * 3 + 1] - reference.Rest[i * 45 + 15 + k]), Is.LessThan(k_tol));
                Assert.That(Mathf.Abs(band[k * 3 + 2] - reference.Rest[i * 45 + 30 + k]), Is.LessThan(k_tol));
            }
        }

        [Test]
        public void DecodesOffTheMainThread()
        {
            var path = Fixtures.Path("synth-folder/meta.json"); // PackageInfo lookup is main-thread only
            var sog = Task.Run(() => SogLoader.Load(path)).GetAwaiter().GetResult();
            Assert.AreEqual(6000, sog.Count);
        }

        [Test]
        public void MissingImageNamesTheFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "gsplat-sog-missing");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            File.Copy(Fixtures.Path("synth-folder/meta.json"), Path.Combine(dir, "meta.json"));
            var e = Assert.Throws<InvalidDataException>(() => SogLoader.Load(Path.Combine(dir, "meta.json")));
            StringAssert.Contains("missing image", e.Message);
        }

        [Test]
        public void FolderRejectsPathsLeavingTheFolder()
        {
            using var source = new FolderSogSource(Fixtures.Path("synth-folder/meta.json"));
            Assert.IsFalse(source.TryRead("../synth.sog", out _));
        }
    }
}

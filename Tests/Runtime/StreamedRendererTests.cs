// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

// The fixtures live inside the package (Tests/Fixtures~), found through the editor's PackageInfo.
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gsplat.Tests
{
    public class StreamedRendererTests
    {
        GameObject m_cam;
        GameObject m_splat;
        readonly List<GameObject> m_splats = new();

        static string FixturePath(string relative)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SogLoader).Assembly);
            return Path.Combine(info.resolvedPath, "Tests", "Fixtures~", relative);
        }

        [SetUp]
        public void SetUp()
        {
            m_cam = new GameObject("cam", typeof(Camera));
            m_cam.tag = "MainCamera";
            m_cam.transform.SetPositionAndRotation(new Vector3(0, 4, -30), Quaternion.identity);
            m_cam.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            m_cam.GetComponent<Camera>().backgroundColor = Color.black;
        }

        [TearDown]
        public void TearDown()
        {
            // immediate: a renderer left until the end of the frame would join the next test's manager
            foreach (var go in m_splats) Object.DestroyImmediate(go);
            m_splats.Clear();
            m_splat = null;
            Object.Destroy(m_cam);
        }

        GsplatStreamedRenderer Make(string lodMeta, Vector3 position = default)
        {
            var go = new GameObject("streamed");
            go.transform.position = position;
            go.SetActive(false);
            var r = go.AddComponent<GsplatStreamedRenderer>();
            r.LodMetaPath = lodMeta;
            r.SourceCoordinates = SourceCoordinates.RUF;
            go.SetActive(true);
            m_splats.Add(go);
            if (m_splat == null) m_splat = go;
            return r;
        }

        // Nothing loading, and coarse files from the first fill have cooled down, so the pool holds exactly what is drawn.
        static bool Settled(GsplatStreamingManager m) =>
            m.QueuedFiles == 0 && m.InFlight == 0 && m.ActiveCount > 0 && m.PoolUsed == m.ActiveCount;

        // Wall-clock timeout: batchmode runs uncapped, so a frame count says little about decode time.
        static IEnumerator Until(System.Func<bool> condition, float maxSeconds = 30f)
        {
            float end = Time.realtimeSinceStartup + maxSeconds;
            while (!condition() && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsTrue(condition(), $"condition not reached in time; {State()}");
        }

        static string State()
        {
            var m = GsplatStreamingManager.Instance;
            return m == null ? "no manager"
                : $"active {m.ActiveCount}, resident {m.ResidentFiles}, in flight {m.InFlight}, queued {m.QueuedFiles}, pool {m.PoolUsed}/{m.PoolCapacity}";
        }

        [UnityTest]
        public IEnumerator FixtureStreamsAndDraws()
        {
            var r = Make(FixturePath("lod/lod-meta.json"));
            yield return Until(() => r.Ready);
            var m = GsplatStreamingManager.Instance;
            yield return Until(() => m.ActiveCount > 0 && m.InFlight == 0 && m.QueuedFiles == 0);
            Assert.That(m.ActiveCount, Is.InRange(1, 6000));

            var cam = m_cam.GetComponent<Camera>();
            var rt = new RenderTexture(128, 128, 24);
            cam.targetTexture = rt;
            // The manager submits its draw late in the frame, so a cam.Render() from a coroutine would miss it:
            // let the camera render on its own (it does in batchmode too) and read the previous frame.
            yield return null;
            yield return null;
            RenderTexture.active = rt;
            var tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
            RenderTexture.active = null;
            int lit = 0;
            foreach (var p in tex.GetPixels32()) if (p.r + p.g + p.b > 30) lit++;
            cam.targetTexture = null;
            Object.Destroy(rt);
            Object.Destroy(tex);
            TestContext.WriteLine($"lit pixels {lit} of {128 * 128}, active {m.ActiveCount}");
            Assert.Greater(lit, 50, "streamed splats drew something");
        }

        [UnityTest]
        public IEnumerator MissingTreeLogsOnce()
        {
            LogAssert.Expect(LogType.Error, new Regex("lod-meta.json not found"));
            var r = Make(Path.Combine(Path.GetTempPath(), "no-such-dir", "lod-meta.json"));
            yield return Until(() => r.Error != null);
            for (int i = 0; i < 30; i++) yield return null;
            LogAssert.NoUnexpectedReceived();
            Assert.IsFalse(r.Ready);
        }

        [UnityTest]
        public IEnumerator DisablingMidLoadReleasesEverything()
        {
            var r = Make(FixturePath("lod/lod-meta.json"));
            yield return Until(() => r.Ready);
            yield return null;
            m_splat.SetActive(false);
            Assert.IsNull(GsplatStreamingManager.Instance, "last renderer gone: manager disposed");
            for (int i = 0; i < 10; i++) yield return null;
            m_splat.SetActive(true);
            yield return Until(() => r.Ready && GsplatStreamingManager.Instance.ActiveCount > 0);
        }

        [UnityTest]
        public IEnumerator MissingStreamingShaderLogsOnceAndSetsError()
        {
            var settings = GsplatSettings.Instance;
            var saved = settings.StreamingShader;
            settings.StreamingShader = null; // in memory only; the asset is never marked dirty
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("missing StreamingShader"));
                var r = Make(FixturePath("lod/lod-meta.json"));
                yield return Until(() => r.Error != null);
                for (int i = 0; i < 30; i++) yield return null;
                LogAssert.NoUnexpectedReceived();
                StringAssert.Contains("StreamingShader", r.Error);
                Assert.IsFalse(r.Ready);
            }
            finally
            {
                settings.StreamingShader = saved;
            }
        }

        [UnityTest]
        public IEnumerator RemovingOneOfTwoLandmarksReleasesItsSlots()
        {
            var a = Make(FixturePath("lod/lod-meta.json"));
            var b = Make(FixturePath("lod/lod-meta.json"), new Vector3(24, 0, 0));
            yield return Until(() => a.Ready && b.Ready);
            var m = GsplatStreamingManager.Instance;
            yield return Until(() => Settled(m));
            int twoActive = m.ActiveCount, twoPool = m.PoolUsed;

            b.gameObject.SetActive(false);
            Assert.AreSame(m, GsplatStreamingManager.Instance, "one renderer left: the manager survives");
            yield return Until(() => Settled(m) && m.ActiveCount < twoActive);
            TestContext.WriteLine($"two trees: active {twoActive}, pool {twoPool}; one tree: active {m.ActiveCount}, pool {m.PoolUsed}");
            Assert.That(m.ActiveCount, Is.InRange(twoActive * 0.4, twoActive * 0.6));
            Assert.That(m.PoolUsed, Is.InRange(twoPool * 0.4, twoPool * 0.6));

            b.gameObject.SetActive(true); // takes the freed landmark id
            yield return Until(() => b.Ready);
            yield return Until(() => Settled(m) && m.ActiveCount == twoActive);
            Assert.AreSame(m, GsplatStreamingManager.Instance);
            Assert.AreEqual(twoPool, m.PoolUsed);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RemovingALandmarkWhileItDecodesDropsTheResults()
        {
            var a = Make(FixturePath("lod/lod-meta.json"));
            yield return Until(() => a.Ready);
            var m = GsplatStreamingManager.Instance;
            yield return Until(() => Settled(m));
            int oneActive = m.ActiveCount, onePool = m.PoolUsed, stale = m.StaleDecodeResults;

            var b = Make(FixturePath("lod/lod-meta.json"), new Vector3(24, 0, 0));
            // a is fully loaded, so anything in flight now is b decoding
            yield return Until(() => b.Ready && m.InFlight > 0);
            b.gameObject.SetActive(false);
            yield return Until(() => m.StaleDecodeResults > stale);
            yield return Until(() => Settled(m));
            Assert.AreEqual(oneActive, m.ActiveCount);
            Assert.AreEqual(onePool, m.PoolUsed);
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator NearCameraGetsFinerLevelsThanFarCamera()
        {
            var r = Make(FixturePath("lod/lod-meta.json"));
            yield return Until(() => r.Ready);
            var m = GsplatStreamingManager.Instance;
            var p = GsplatSettings.Instance.Streaming;
            var (savedBudget, savedLimit) = (p.SplatBudget, p.LimitMode);
            // limit mode: distances cap detail (target mode would fill the budget at any distance)
            p.SplatBudget = 4000;
            p.LimitMode = true;
            try
            {
                m_cam.transform.position = new Vector3(0, 4, -400);
                yield return Until(() => m.QueuedFiles == 0 && m.InFlight == 0 && m.ActiveCount > 0);
                for (int i = 0; i < 12; i++) yield return null;
                int far = m.ActiveCount;
                m_cam.transform.position = new Vector3(6, 4, 6);
                for (int i = 0; i < 12; i++) yield return null;
                yield return Until(() => m.QueuedFiles == 0 && m.InFlight == 0);
                for (int i = 0; i < 12; i++) yield return null;
                TestContext.WriteLine($"far {far}, near {m.ActiveCount}");
                Assert.Greater(m.ActiveCount, far, "closer camera draws more splats");
                Assert.LessOrEqual(m.ActiveCount, 6000);
            }
            finally
            {
                (p.SplatBudget, p.LimitMode) = (savedBudget, savedLimit);
            }
        }
    }
}
#endif

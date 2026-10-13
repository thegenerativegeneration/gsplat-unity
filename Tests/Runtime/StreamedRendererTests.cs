// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

// The fixtures live inside the package (Tests/Fixtures~), found through the editor's PackageInfo.
#if UNITY_EDITOR
using System.Collections;
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
            Object.Destroy(m_splat);
            Object.Destroy(m_cam);
        }

        GsplatStreamedRenderer Make(string lodMeta)
        {
            m_splat = new GameObject("streamed");
            m_splat.SetActive(false);
            var r = m_splat.AddComponent<GsplatStreamedRenderer>();
            r.LodMetaPath = lodMeta;
            r.SourceCoordinates = SourceCoordinates.RUF;
            m_splat.SetActive(true);
            return r;
        }

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

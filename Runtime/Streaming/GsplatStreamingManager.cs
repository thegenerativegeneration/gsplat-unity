// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Streamed LOD after PlayCanvas engine v2.23.2 (src/scene/gsplat-unified/gsplat-world.js): cadence and change
// detection, budget fit across all streamed landmarks, coarse-first loading, one sorted draw.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gsplat
{
    public sealed class GsplatStreamingManager : IGsplatSortClient
    {
        const int k_maxLandmarks = 255;
        const int k_selectEveryFrames = 10;

        static readonly int k_globalOrderBuffer = Shader.PropertyToID("_GlobalOrderBuffer");
        static readonly int k_globalPackedBuffer = Shader.PropertyToID("_GlobalPackedBuffer");
        static readonly int k_globalSH1Buffer = Shader.PropertyToID("_GlobalSH1Buffer");
        static readonly int k_globalSH2Buffer = Shader.PropertyToID("_GlobalSH2Buffer");
        static readonly int k_globalSH3Buffer = Shader.PropertyToID("_GlobalSH3Buffer");
        static readonly int k_streamedCenters = Shader.PropertyToID("_StreamedCenters");
        static readonly int k_rendererOffsets = Shader.PropertyToID("_RendererOffsets");
        static readonly int k_rendererTransforms = Shader.PropertyToID("_RendererTransforms");
        static readonly int k_rendererParams = Shader.PropertyToID("_RendererParams");
        static readonly int k_totalSplatCount = Shader.PropertyToID("_TotalSplatCount");
        static readonly int k_splatInstanceSize = Shader.PropertyToID("_SplatInstanceSize");

        // Matches RendererParams in GsplatSparkGlobal.hlsl.
        struct LandmarkParams
        {
            public float Brightness;
            public float ScaleFactor;
            public uint GammaToLinear;
            public uint SHDegree;
        }

        public static GsplatStreamingManager Instance { get; private set; }
        public static Camera LodCamera;

        public int ActiveCount { get; private set; }
        public int ResidentFiles => m_residency.ResidentCount;
        public int InFlight => m_residency.InFlight;
        public int QueuedFiles => m_residency.QueuedCount;
        public int PoolCapacity => m_pool?.Capacity ?? 0;
        public int PoolUsed => m_pool?.Used ?? 0;
        public int LastUploadCount { get; private set; }

        readonly Dictionary<GsplatStreamedRenderer, StreamedLandmark> m_byRenderer = new();
        readonly StreamedLandmark[] m_landmarks = new StreamedLandmark[k_maxLandmarks];
        readonly HashSet<GsplatStreamedRenderer> m_pendingMeta = new();
        readonly ConcurrentQueue<(GsplatStreamedRenderer r, SogLodMeta meta, byte bands, Exception e)> m_metaResults = new();
        readonly ResidencyController m_residency;
        readonly LodBudgetBalancer m_balancer = new();
        bool m_paramsWarned;
        readonly List<FileKey> m_unloads = new();
        readonly List<LodInstance> m_instances = new();
        readonly List<uint4> m_ranges = new();
        readonly Matrix4x4[] m_transforms = new Matrix4x4[k_maxLandmarks];
        readonly LandmarkParams[] m_params = new LandmarkParams[k_maxLandmarks];

        SplatPool m_pool;
        StreamedLoader m_loader;
        GraphicsBuffer m_order, m_keys, m_rangeBuffer, m_offsets, m_transformBuffer, m_paramBuffer;
        GsplatSortPass.SupportResources m_sortResources;
        CommandBuffer m_cmd;
        Material m_material;
        MaterialPropertyBlock m_props;
        int m_frame, m_lastSelectFrame = -k_selectEveryFrames;
        Vector3 m_lastCamPos;
        Vector3 m_lastCamFwd;
        float m_lastFov;
        bool m_selectionDirty = true;
        bool m_activeDirty;
        bool m_settingsErrorLogged;

        static GsplatStreamingParams P => GsplatSettings.Instance.Streaming;

        GsplatStreamingManager()
        {
            m_residency = new ResidencyController(Math.Max(1, P.MaxDecodesInFlight), Math.Max(1, P.UnloadAfterFrames));
        }

        // Domain reload off: statics survive leaving play mode.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            if (Instance != null) Shutdown();
            LodCamera = null;
        }

        internal static void Register(GsplatStreamedRenderer r)
        {
            if (Instance == null)
            {
                if (!SystemInfo.supportsComputeShaders)
                {
                    r.Error = "compute shaders are not supported on this device";
                    Debug.LogError($"[Gsplat] {r.name}: streamed splats need compute shader support.");
                    return;
                }
                Instance = new GsplatStreamingManager();
                GsplatSorter.Instance.RegisterClient(Instance);
            }
            Instance.BeginLoad(r);
        }

        internal static void Unregister(GsplatStreamedRenderer r)
        {
            if (Instance == null) return;
            Instance.Remove(r);
            if (Instance.m_byRenderer.Count == 0 && Instance.m_pendingMeta.Count == 0) Shutdown();
        }

        static void Shutdown()
        {
            GsplatSorter.Instance.UnregisterClient(Instance);
            Instance.Dispose();
            Instance = null;
        }

        internal static void TickIfActive() => Instance?.Tick();

        internal SogLodMeta MetaOf(GsplatStreamedRenderer r) => m_byRenderer.TryGetValue(r, out var lm) ? lm.Meta : null;
        internal int StaleDecodeResults => m_loader?.StaleResults ?? 0;

        void BeginLoad(GsplatStreamedRenderer r)
        {
            string path = r.ResolvedPath;
            var src = r.SourceCoordinates;
            m_pendingMeta.Add(r);
            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        throw new FileNotFoundException($"lod-meta.json not found at '{path}'");
                    var meta = SogLodMeta.Load(path, src);
                    m_metaResults.Enqueue((r, meta, ReadShBands(meta.Files[0]), null));
                }
                catch (Exception e)
                {
                    m_metaResults.Enqueue((r, null, 0, e));
                }
            });
        }

        // shN.bands of a unit's meta.json, 0 when absent. Runs on a worker thread.
        static byte ReadShBands(string unitMetaPath)
        {
            if (!(MiniJson.Parse(File.ReadAllText(unitMetaPath)) is Dictionary<string, object> unit)) return 0;
            if (!(unit.TryGetValue("shN", out var so) && so is Dictionary<string, object> shN)) return 0;
            return shN.TryGetValue("bands", out var bo) && bo is double b ? (byte)Math.Clamp((int)b, 0, 3) : (byte)0;
        }

        void FinishMetaLoads()
        {
            while (m_metaResults.TryDequeue(out var res))
            {
                if (!m_pendingMeta.Remove(res.r) || !res.r) continue;
                if (res.e != null)
                {
                    res.r.Error = res.e.Message;
                    Debug.LogError($"[Gsplat] {res.r.name}: {res.e.Message}");
                    continue;
                }
                int id = Array.IndexOf(m_landmarks, null);
                if (id < 0)
                {
                    res.r.Error = $"more than {k_maxLandmarks} streamed renderers";
                    Debug.LogError($"[Gsplat] {res.r.name}: {res.r.Error}");
                    continue;
                }
                var lm = new StreamedLandmark(id, res.r, res.meta, res.bands);
                if (res.bands > m_pool.ShBands)
                    Debug.LogWarning($"[Gsplat] {res.r.name}: SH bands {res.bands} truncated to {m_pool.ShBands}.");
                m_landmarks[id] = lm;
                m_byRenderer[res.r] = lm;
                m_residency.AddLandmark(id, res.meta);
                res.r.Ready = true;
                m_selectionDirty = true;
            }
        }

        void Remove(GsplatStreamedRenderer r)
        {
            m_pendingMeta.Remove(r);
            if (!m_byRenderer.Remove(r, out var lm)) return;
            m_landmarks[lm.Id] = null;
            foreach (var key in m_residency.RemoveLandmark(lm.Id)) m_loader?.Release(key);
            m_selectionDirty = true;
            m_activeDirty = true;
        }

        void Tick()
        {
            var settings = GsplatSettings.Instance;
            string missing = MissingSettings(settings);
            if (missing != null)
            {
                ReportSettingsError(missing);
                return;
            }
            EnsureGpu(settings);
            m_frame++;
            FinishMetaLoads();

            var cam = LodCamera ? LodCamera : Camera.main;
            if (cam && ShouldSelect(cam)) Select(cam);

            m_loader.StartDecodes();
            m_loader.FinishDecodes();
            if (m_loader.StepUploads(Math.Max(1, P.UploadSplatsPerFrame), out int uploaded))
                m_selectionDirty = true; // PlayCanvas re-evaluates when a file finished loading
            LastUploadCount = uploaded;

            m_unloads.Clear();
            m_residency.Tick(m_unloads);
            foreach (var key in m_unloads) m_loader.Release(key);

            if (m_residency.ConsumeDrawnChanged() || m_activeDirty) RebuildActive();
            UpdateTransformsAndParams();
            Draw();
        }

        static string MissingSettings(GsplatSettings s)
        {
            if (!s) return "the GsplatSettings asset";
            if (s.StreamingCompute && s.StreamingShader && s.GlobalMaterial && s.GlobalMaterial.CopyBufferShader && s.Valid)
                return null;
            var missing = new List<string>();
            if (!s.StreamingCompute) missing.Add(nameof(GsplatSettings.StreamingCompute));
            if (!s.StreamingShader) missing.Add(nameof(GsplatSettings.StreamingShader));
            if (!s.GlobalMaterial) missing.Add(nameof(GsplatSettings.GlobalMaterial));
            else if (!s.GlobalMaterial.CopyBufferShader) missing.Add("GlobalMaterial.CopyBufferShader");
            if (!s.Valid) missing.Add("Materials / SplatInstanceSize (settings not valid)");
            return string.Join(", ", missing);
        }

        // Without this nothing loads and nothing would say why: one log, and every renderer carries the error.
        void ReportSettingsError(string missing)
        {
            string error = $"GsplatSettings is missing {missing}; streamed splats cannot load";
            if (!m_settingsErrorLogged)
            {
                m_settingsErrorLogged = true;
                Debug.LogError($"[Gsplat] {error}.");
            }
            foreach (var r in m_pendingMeta)
                if (r && r.Error == null) r.Error = error;
            foreach (var r in m_byRenderer.Keys)
                if (r && r.Error == null) r.Error = error;
        }

        void WarnOnInvalidParams()
        {
            if (m_paramsWarned || P.LodBaseDistance >= GsplatStreamingParams.MinBaseDistance) return;
            m_paramsWarned = true;
            Debug.LogWarning($"[Gsplat] Streaming LodBaseDistance {P.LodBaseDistance} is below {GsplatStreamingParams.MinBaseDistance}; using {GsplatStreamingParams.MinBaseDistance}.");
        }

        bool ShouldSelect(Camera cam)
        {
            if (m_byRenderer.Count == 0) return false;
            if (m_frame - m_lastSelectFrame < k_selectEveryFrames) return false;
            if (m_selectionDirty) return true;
            var t = cam.transform;
            if ((t.position - m_lastCamPos).sqrMagnitude > 1f
                || Vector3.Angle(t.forward, m_lastCamFwd) > 90f
                || Mathf.Abs(cam.fieldOfView - m_lastFov) > 0.02f * Mathf.Max(1e-3f, m_lastFov))
                return true;
            foreach (var lm in m_byRenderer.Values)
                if (lm.Renderer.transform.localToWorldMatrix != lm.LocalToWorld) return true;
            return false;
        }

        void Select(Camera cam)
        {
            m_lastSelectFrame = m_frame;
            m_lastCamPos = cam.transform.position;
            m_lastCamFwd = cam.transform.forward;
            m_lastFov = cam.fieldOfView;
            m_selectionDirty = false;
            m_instances.Clear();
            foreach (var lm in m_byRenderer.Values)
            {
                lm.LocalToWorld = lm.Renderer.transform.localToWorldMatrix;
                lm.EvaluateDistances(cam, P);
                m_instances.Add(lm.Lod);
            }
            WarnOnInvalidParams();
            m_balancer.Balance(m_instances, P.EffectiveBudget, P.LimitMode);
            foreach (var lm in m_byRenderer.Values)
                m_residency.SetWanted(lm.Id, lm.Lod.OptimalLod, lm.Lod.DistanceSq);
        }

        // Rewrites the order buffer as (landmark | pool slot) for every drawn leaf range; sorting starts from it.
        void RebuildActive()
        {
            m_activeDirty = false;
            m_ranges.Clear();
            uint prefix = 0;
            foreach (var lm in m_byRenderer.Values)
            {
                for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
                {
                    int lod = m_residency.DrawnLod(lm.Id, leaf);
                    if (lod < 0) continue;
                    var r = lm.Meta.Range(leaf, lod);
                    if (r.Count == 0 || !m_loader.TryGetSlot(new FileKey(lm.Id, r.File), out int slot)) continue;
                    uint start = (uint)(slot + r.Offset);
                    int last = m_ranges.Count - 1;
                    if (last >= 0 && m_ranges[last].x == (uint)lm.Id && m_ranges[last].y + m_ranges[last].z == start)
                        m_ranges[last] += new uint4(0, 0, (uint)r.Count, 0);
                    else
                        m_ranges.Add(new uint4((uint)lm.Id, start, (uint)r.Count, prefix));
                    prefix += (uint)r.Count;
                }
            }
            ActiveCount = (int)prefix;
            if (ActiveCount == 0) return;
            if (m_rangeBuffer == null || m_rangeBuffer.count < m_ranges.Count)
            {
                m_rangeBuffer?.Dispose();
                m_rangeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(256, m_ranges.Count * 2), 16);
            }
            m_rangeBuffer.SetData(m_ranges);
            m_cmd.Clear();
            StreamedKernels.Expand(m_cmd, GsplatSettings.Instance.StreamingCompute, m_rangeBuffer, m_ranges.Count, ActiveCount, m_order);
            Graphics.ExecuteCommandBuffer(m_cmd);
        }

        void UpdateTransformsAndParams()
        {
            foreach (var lm in m_byRenderer.Values)
            {
                var r = lm.Renderer;
                m_transforms[lm.Id] = r.transform.localToWorldMatrix;
                m_params[lm.Id] = new LandmarkParams
                {
                    Brightness = r.Brightness,
                    ScaleFactor = 1f - r.SplatDownscaleFactor,
                    GammaToLinear = r.GammaToLinear ? 1u : 0u,
                    SHDegree = (uint)Mathf.Clamp(r.SHDegree, 0, Math.Min(lm.ShBands, m_pool.ShBands)),
                };
            }
            m_transformBuffer.SetData(m_transforms);
            m_paramBuffer.SetData(m_params);
        }

        public bool HasWork(Camera camera) => ActiveCount > 0 && camera.cameraType != CameraType.Preview;

        public void DispatchSort(CommandBuffer cmd, Camera camera, GsplatSortPass sortPass)
        {
            StreamedKernels.CalcDepth(cmd, GsplatSettings.Instance.StreamingCompute, camera.worldToCameraMatrix,
                m_order, m_pool.Centers, m_transformBuffer, ActiveCount, m_keys);
            sortPass.Dispatch(cmd, new GsplatSortPass.Args
            {
                Count = (uint)ActiveCount,
                InputKeys = m_keys,
                InputValues = m_order,
                Resources = m_sortResources,
            });
        }

        void Draw()
        {
            if (ActiveCount == 0) return;
            var settings = GsplatSettings.Instance;
            m_props.Clear();
            m_props.SetBuffer(k_globalOrderBuffer, m_order);
            m_props.SetBuffer(k_globalPackedBuffer, m_pool.Packed);
            m_props.SetBuffer(k_streamedCenters, m_pool.Centers);
            m_props.SetBuffer(k_rendererOffsets, m_offsets);
            m_props.SetBuffer(k_rendererTransforms, m_transformBuffer);
            m_props.SetBuffer(k_rendererParams, m_paramBuffer);
            if (m_pool.ShBands >= 1) m_props.SetBuffer(k_globalSH1Buffer, m_pool.Sh1);
            if (m_pool.ShBands >= 2) m_props.SetBuffer(k_globalSH2Buffer, m_pool.Sh2);
            if (m_pool.ShBands >= 3) m_props.SetBuffer(k_globalSH3Buffer, m_pool.Sh3);
            m_props.SetInteger(k_totalSplatCount, ActiveCount);
            m_props.SetInteger(k_splatInstanceSize, (int)settings.SplatInstanceSize);
            var rp = new RenderParams(m_material)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e6f),
                matProps = m_props,
            };
            int instances = Mathf.CeilToInt(ActiveCount / (float)settings.SplatInstanceSize);
            Graphics.RenderMeshPrimitives(rp, settings.Mesh, 0, instances);
        }

        void EnsureGpu(GsplatSettings settings)
        {
            if (m_pool != null) return;
            // Decode workers call GsplatAssetSpark's packers; its static initialisers use Shader.PropertyToID.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(GsplatAssetSpark).TypeHandle);
            int capacity = (int)Math.Min(SplatPool.MaxSlots, Math.Max(1L << 16, P.SplatBudget * 2L));
            m_pool = new SplatPool(capacity, Math.Clamp(P.MaxShBands, 0, 3), settings.GlobalMaterial.CopyBufferShader);
            m_pool.Grown += OnPoolGrown;
            m_loader = new StreamedLoader(m_residency, m_landmarks, m_pool);
            CreateSortBuffers(m_pool.Capacity);
            m_offsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_maxLandmarks, 4);
            m_offsets.SetData(new uint[k_maxLandmarks]);
            m_transformBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_maxLandmarks, 64);
            m_paramBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_maxLandmarks, 16);
            m_cmd = new CommandBuffer { name = "Gsplat.Streamed" };
            m_props = new MaterialPropertyBlock();
            m_material = new Material(settings.StreamingShader) { hideFlags = HideFlags.HideAndDontSave };
            for (int b = 0; b <= 4; b++) m_material.DisableKeyword($"SH_BANDS_{b}");
            m_material.EnableKeyword($"SH_BANDS_{m_pool.ShBands}");
        }

        void OnPoolGrown()
        {
            CreateSortBuffers(m_pool.Capacity);
            m_activeDirty = true;
        }

        void CreateSortBuffers(int capacity)
        {
            m_order?.Dispose();
            m_keys?.Dispose();
            m_sortResources.Dispose();
            m_order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
            m_keys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
            m_sortResources = GsplatSortPass.SupportResources.Load((uint)capacity);
        }

        void Dispose()
        {
            m_loader?.Dispose();
            m_pool?.Dispose();
            m_order?.Dispose();
            m_keys?.Dispose();
            m_rangeBuffer?.Dispose();
            m_offsets?.Dispose();
            m_transformBuffer?.Dispose();
            m_paramBuffer?.Dispose();
            m_sortResources.Dispose();
            m_cmd?.Dispose();
            if (!m_material) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(m_material);
            else UnityEngine.Object.DestroyImmediate(m_material);
        }
    }
}

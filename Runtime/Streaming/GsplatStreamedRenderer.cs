// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.IO;
using UnityEngine;

namespace Gsplat
{
    // A splat scene streamed from a PlayCanvas LOD tree (lod-meta.json + SOG units). Every streamed renderer is drawn by
    // GsplatStreamingManager in one sorted draw under one splat budget (GsplatSettings.Streaming).
    [DisallowMultipleComponent]
    public sealed class GsplatStreamedRenderer : MonoBehaviour
    {
        [Tooltip("lod-meta.json: absolute, or relative to StreamingAssets")]
        public string LodMetaPath;
        [Tooltip("Editor only: used instead of LodMetaPath when set and present")]
        public string EditorLodMetaPath;
        public SourceCoordinates SourceCoordinates = SourceCoordinates.RDB;
        [Range(0, 3)] public int SHDegree = 3;
        public float Brightness = 1.0f;
        [Range(0, 1)] public float SplatDownscaleFactor;
        public bool GammaToLinear;

        public string Error { get; internal set; }
        public bool Ready { get; internal set; }

        public string ResolvedPath
        {
            get
            {
#if UNITY_EDITOR
                if (!string.IsNullOrEmpty(EditorLodMetaPath) && File.Exists(EditorLodMetaPath))
                    return EditorLodMetaPath;
#endif
                if (string.IsNullOrEmpty(LodMetaPath)) return null;
                return Path.IsPathRooted(LodMetaPath) ? LodMetaPath : Path.Combine(Application.streamingAssetsPath, LodMetaPath);
            }
        }

        void OnEnable()
        {
            Error = null;
            Ready = false;
            GsplatStreamingManager.Register(this);
        }

        void OnDisable() => GsplatStreamingManager.Unregister(this);

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || GsplatStreamingManager.Instance == null) return;
            var meta = GsplatStreamingManager.Instance.MetaOf(this);
            if (meta == null) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            for (int leaf = 0; leaf < meta.LeafCount; leaf++)
            {
                int b = leaf * 6;
                var min = new Vector3(meta.Bounds[b], meta.Bounds[b + 1], meta.Bounds[b + 2]);
                var max = new Vector3(meta.Bounds[b + 3], meta.Bounds[b + 4], meta.Bounds[b + 5]);
                Gizmos.DrawWireCube((min + max) * 0.5f, max - min);
            }
        }
    }
}

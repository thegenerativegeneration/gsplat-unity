// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Gsplat
{
    // Decodes the SOG units the residency controller asks for on worker threads, places them in the SplatPool and
    // uploads them over several frames. Everything except the decode itself runs on the main thread.
    internal sealed class StreamedLoader : IDisposable
    {
        sealed class Decoded
        {
            public FileKey Key;
            public CancellationTokenSource Source;
            public DecodedChunk Chunk;
            public Exception Error;
        }

        readonly ResidencyController m_residency;
        readonly StreamedLandmark[] m_landmarks;
        readonly SplatPool m_pool;
        readonly ConcurrentQueue<Decoded> m_decoded = new();
        readonly Dictionary<FileKey, CancellationTokenSource> m_decoding = new();
        readonly Dictionary<FileKey, int> m_slots = new();
        readonly Dictionary<FileKey, DecodedChunk> m_uploading = new();
        readonly UploadScheduler m_uploads = new();
        readonly List<UploadStep> m_steps = new();
        readonly List<FileKey> m_evicted = new();
        readonly HashSet<string> m_warnedFull = new();

        public StreamedLoader(ResidencyController residency, StreamedLandmark[] landmarks, SplatPool pool)
        {
            m_residency = residency;
            m_landmarks = landmarks;
            m_pool = pool;
        }

        public bool TryGetSlot(FileKey key, out int slot) => m_slots.TryGetValue(key, out slot);

        public void StartDecodes()
        {
            while (m_residency.TryNextLoad(out var key))
            {
                var lm = m_landmarks[key.Landmark];
                string path = lm.Meta.Files[key.File];
                var src = lm.Renderer.SourceCoordinates;
                int bands = m_pool.ShBands;
                var cts = new CancellationTokenSource();
                m_decoding[key] = cts;
                Task.Run(() =>
                {
                    var d = new Decoded { Key = key, Source = cts };
                    try
                    {
                        d.Chunk = SogChunkDecoder.Decode(SogLoader.Load(path), src, bands, cts.Token);
                    }
                    catch (Exception e)
                    {
                        d.Error = e;
                    }
                    m_decoded.Enqueue(d);
                });
            }
        }

        public void FinishDecodes()
        {
            while (m_decoded.TryDequeue(out var d))
            {
                // released while decoding, or a stale result for a landmark id that has since been reused
                if (!m_decoding.TryGetValue(d.Key, out var cts) || cts != d.Source)
                {
                    d.Source.Dispose();
                    continue;
                }
                m_decoding.Remove(d.Key);
                cts.Dispose();
                var lm = m_landmarks[d.Key.Landmark];
                string file = lm.Meta.Files[d.Key.File];
                int expected = lm.Meta.FileCount[d.Key.File];
                if (d.Error == null && d.Chunk.Count < expected)
                    d.Error = new InvalidDataException($"unit has {d.Chunk.Count} splats, lod-meta.json expects {expected}");
                if (d.Error != null)
                {
                    Debug.LogError($"[Gsplat] {lm.Renderer.name}: {file}: {d.Error.Message}");
                    m_residency.MarkFailed(d.Key);
                    continue;
                }
                if (!AllocateSlot(d.Chunk.Count, out int slot))
                {
                    // transient: the file requeues and fits once other files cool down
                    if (m_warnedFull.Add(file))
                        Debug.LogWarning($"[Gsplat] streaming pool is full ({m_pool.Capacity} splats); {file} waits for space.");
                    m_residency.MarkCancelled(d.Key);
                    continue;
                }
                m_slots[d.Key] = slot;
                m_uploading[d.Key] = d.Chunk;
                m_uploads.Enqueue(d.Key, d.Chunk.Count, slot);
            }
        }

        bool AllocateSlot(int count, out int slot)
        {
            if (m_pool.TryAllocate(count, out slot)) return true;
            m_evicted.Clear();
            m_residency.EvictIdle(m_evicted);
            foreach (var key in m_evicted) Release(key);
            if (m_pool.TryAllocate(count, out slot)) return true;
            // TryGrowFor may succeed at the 24-bit cap without making enough room
            return m_pool.TryGrowFor(count) && m_pool.TryAllocate(count, out slot);
        }

        // Returns true when at least one file finished uploading and is now resident.
        public bool StepUploads(int budget, out int uploaded)
        {
            m_steps.Clear();
            uploaded = m_uploads.Step(budget, m_steps);
            bool completed = false;
            foreach (var s in m_steps)
            {
                var key = (FileKey)s.Tag;
                if (!m_uploading.TryGetValue(key, out var chunk)) continue;
                m_pool.Upload(chunk, s.Source, s.Destination, s.Count);
                if (!s.Completes) continue;
                m_uploading.Remove(key);
                m_residency.MarkLoaded(key);
                completed = true;
            }
            return completed;
        }

        public void Release(FileKey key)
        {
            if (m_decoding.Remove(key, out var cts)) cts.Cancel();
            if (m_uploading.Remove(key)) m_uploads.Cancel(key);
            if (m_slots.Remove(key, out int slot)) m_pool.Free(slot);
        }

        public void Dispose()
        {
            foreach (var cts in m_decoding.Values) cts.Cancel();
            m_decoding.Clear();
            m_uploading.Clear();
            m_slots.Clear();
        }
    }
}

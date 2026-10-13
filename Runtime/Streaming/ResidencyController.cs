// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Load ordering and unload cooldown follow PlayCanvas engine v2.23.2 (gsplat-octree-instance.js, gsplat-octree.js);
// the prefetch tier is not ported.

using System;
using System.Collections.Generic;

namespace Gsplat
{
    internal readonly struct FileKey : IEquatable<FileKey>
    {
        public readonly int Landmark;
        public readonly int File;

        public FileKey(int landmark, int file)
        {
            Landmark = landmark;
            File = file;
        }

        public bool Equals(FileKey o) => Landmark == o.Landmark && File == o.File;
        public override bool Equals(object o) => o is FileKey k && Equals(k);
        public override int GetHashCode() => (Landmark << 20) ^ File;
        public override string ToString() => $"({Landmark}, {File})";
    }

    internal sealed class ResidencyController
    {
        sealed class Landmark
        {
            public SogLodMeta Meta;
            public int[] Wanted;
            public int[] Drawn;
            public double[] DistanceSq;
            public int[] Refs;      // per file: leaves wanting or drawing it
            public int[] Idle;      // per file: frames with Refs == 0 while resident
            public bool[] Resident;
            public bool[] Loading;
            public bool[] Failed;
        }

        readonly Dictionary<int, Landmark> m_landmarks = new();
        readonly int m_maxInFlight;
        readonly int m_unloadAfterFrames;
        readonly List<(FileKey key, int tier, int lod, double dist)> m_queue = new();
        bool m_queueDirty;
        bool m_drawnChanged;

        public int InFlight { get; private set; }
        public int ResidentCount { get; private set; }
        public int QueuedCount { get { RebuildQueue(); return m_queue.Count; } }

        public ResidencyController(int maxInFlight = 2, int unloadAfterFrames = 100)
        {
            m_maxInFlight = maxInFlight;
            m_unloadAfterFrames = unloadAfterFrames;
        }

        public void AddLandmark(int landmark, SogLodMeta meta)
        {
            int files = meta.Files.Length;
            var lm = new Landmark
            {
                Meta = meta,
                Wanted = new int[meta.LeafCount],
                Drawn = new int[meta.LeafCount],
                DistanceSq = new double[meta.LeafCount],
                Refs = new int[files],
                Idle = new int[files],
                Resident = new bool[files],
                Loading = new bool[files],
                Failed = new bool[files],
            };
            for (int i = 0; i < meta.LeafCount; i++) lm.Wanted[i] = lm.Drawn[i] = -1;
            m_landmarks.Add(landmark, lm);
        }

        public List<FileKey> RemoveLandmark(int landmark)
        {
            var result = new List<FileKey>();
            if (!m_landmarks.Remove(landmark, out var lm)) return result;
            for (int f = 0; f < lm.Resident.Length; f++)
            {
                if (lm.Resident[f]) { ResidentCount--; result.Add(new FileKey(landmark, f)); }
                if (lm.Loading[f]) { InFlight--; result.Add(new FileKey(landmark, f)); }
            }
            m_queueDirty = true;
            m_drawnChanged = true;
            return result;
        }

        public bool IsResident(FileKey k) => m_landmarks.TryGetValue(k.Landmark, out var lm) && lm.Resident[k.File];
        public int DrawnLod(int landmark, int leaf) => m_landmarks[landmark].Drawn[leaf];

        public bool ConsumeDrawnChanged()
        {
            bool c = m_drawnChanged;
            m_drawnChanged = false;
            return c;
        }

        public void SetWanted(int landmark, int[] wantedLod, double[] distanceSq)
        {
            var lm = m_landmarks[landmark];
            for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
            {
                lm.Wanted[leaf] = Effective(lm, leaf, wantedLod[leaf]);
                lm.DistanceSq[leaf] = distanceSq[leaf];
            }
            Recount(lm);
            UpdateDrawn(lm);
            m_queueDirty = true;
        }

        public bool TryNextLoad(out FileKey key)
        {
            key = default;
            if (InFlight >= m_maxInFlight) return false;
            RebuildQueue();
            if (m_queue.Count == 0) return false;
            key = m_queue[0].key;
            m_queue.RemoveAt(0);
            m_landmarks[key.Landmark].Loading[key.File] = true;
            InFlight++;
            return true;
        }

        public void MarkLoaded(FileKey key)
        {
            if (!m_landmarks.TryGetValue(key.Landmark, out var lm) || !lm.Loading[key.File]) return;
            lm.Loading[key.File] = false;
            InFlight--;
            lm.Resident[key.File] = true;
            lm.Idle[key.File] = 0;
            ResidentCount++;
            UpdateDrawn(lm);
            m_queueDirty = true;
        }

        public void MarkFailed(FileKey key)
        {
            if (!m_landmarks.TryGetValue(key.Landmark, out var lm) || !lm.Loading[key.File]) return;
            lm.Loading[key.File] = false;
            InFlight--;
            lm.Failed[key.File] = true;
            for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
                lm.Wanted[leaf] = Effective(lm, leaf, lm.Wanted[leaf]);
            Recount(lm);
            UpdateDrawn(lm);
            m_queueDirty = true;
        }

        public void MarkCancelled(FileKey key)
        {
            if (!m_landmarks.TryGetValue(key.Landmark, out var lm) || !lm.Loading[key.File]) return;
            lm.Loading[key.File] = false;
            InFlight--;
            m_queueDirty = true;
        }

        public void Tick(List<FileKey> unloads)
        {
            foreach (var (id, lm) in m_landmarks)
            {
                for (int f = 0; f < lm.Resident.Length; f++)
                {
                    if (!lm.Resident[f] || lm.Refs[f] > 0) { lm.Idle[f] = 0; continue; }
                    if (++lm.Idle[f] < m_unloadAfterFrames) continue;
                    lm.Resident[f] = false;
                    lm.Idle[f] = 0;
                    ResidentCount--;
                    unloads.Add(new FileKey(id, f));
                }
            }
        }

        public void EvictIdle(List<FileKey> unloads)
        {
            foreach (var (id, lm) in m_landmarks)
                for (int f = 0; f < lm.Resident.Length; f++)
                {
                    if (!lm.Resident[f] || lm.Refs[f] > 0) continue;
                    lm.Resident[f] = false;
                    lm.Idle[f] = 0;
                    ResidentCount--;
                    unloads.Add(new FileKey(id, f));
                }
        }

        // The wanted level, moved coarser past failed files, then finer if no coarser level is usable;
        // -1 when nothing can be drawn.
        static int Effective(Landmark lm, int leaf, int lod)
        {
            var meta = lm.Meta;
            if (lod < 0 || lod >= meta.LodLevels) return -1;
            for (int l = lod; l < meta.LodLevels; l++)
                if (Usable(lm, leaf, l)) return l;
            for (int l = lod - 1; l >= 0; l--)
                if (Usable(lm, leaf, l)) return l;
            return -1;
        }

        static bool Usable(Landmark lm, int leaf, int lod)
        {
            var r = lm.Meta.Range(leaf, lod);
            return r.Count > 0 && !lm.Failed[r.File];
        }

        static int CoarsestUsable(Landmark lm, int leaf)
        {
            for (int l = lm.Meta.LodLevels - 1; l >= 0; l--)
                if (Usable(lm, leaf, l)) return l;
            return -1;
        }

        static int FileOf(Landmark lm, int leaf, int lod) =>
            lod < 0 ? -1 : (lm.Meta.Range(leaf, lod).Count > 0 ? lm.Meta.Range(leaf, lod).File : -1);

        static void Recount(Landmark lm)
        {
            Array.Clear(lm.Refs, 0, lm.Refs.Length);
            for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
            {
                int w = FileOf(lm, leaf, lm.Wanted[leaf]);
                int d = FileOf(lm, leaf, lm.Drawn[leaf]);
                if (w >= 0) lm.Refs[w]++;
                if (d >= 0 && d != w) lm.Refs[d]++;
            }
        }

        void UpdateDrawn(Landmark lm)
        {
            for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
            {
                int next = Choose(lm, leaf);
                if (next == lm.Drawn[leaf]) continue;
                lm.Drawn[leaf] = next;
                m_drawnChanged = true;
            }
            Recount(lm);
        }

        static int Choose(Landmark lm, int leaf)
        {
            int wanted = lm.Wanted[leaf];
            int wf = FileOf(lm, leaf, wanted);
            if (wanted < 0) return -1;
            if (wf >= 0 && lm.Resident[wf]) return wanted;
            int df = FileOf(lm, leaf, lm.Drawn[leaf]);
            if (df >= 0 && lm.Resident[df]) return lm.Drawn[leaf];
            for (int l = lm.Meta.LodLevels - 1; l >= 0; l--)
            {
                int f = FileOf(lm, leaf, l);
                if (f >= 0 && lm.Resident[f]) return l;
            }
            return -1;
        }

        void RebuildQueue()
        {
            if (!m_queueDirty) return;
            m_queueDirty = false;
            m_queue.Clear();
            var best = new Dictionary<FileKey, (int tier, int lod, double dist)>();
            foreach (var (id, lm) in m_landmarks)
            {
                for (int leaf = 0; leaf < lm.Meta.LeafCount; leaf++)
                {
                    if (lm.Wanted[leaf] < 0) continue;
                    if (lm.Drawn[leaf] < 0)
                    {
                        // Underfill: a leaf drawing nothing also loads its coarsest level first.
                        int c = CoarsestUsable(lm, leaf);
                        int cf = FileOf(lm, leaf, c);
                        if (cf >= 0 && !lm.Resident[cf] && !lm.Loading[cf] && !lm.Failed[cf])
                            Offer(best, id, cf, (2, c, lm.DistanceSq[leaf]));
                    }
                    int f = FileOf(lm, leaf, lm.Wanted[leaf]);
                    if (f < 0 || lm.Resident[f] || lm.Loading[f] || lm.Failed[f]) continue;
                    Offer(best, id, f, (lm.Drawn[leaf] < 0 ? 2 : 1, lm.Wanted[leaf], lm.DistanceSq[leaf]));
                }
            }
            foreach (var (k, p) in best) m_queue.Add((k, p.tier, p.lod, p.dist));
            m_queue.Sort((a, b) => Better((a.tier, a.lod, a.dist), (b.tier, b.lod, b.dist)) ? -1
                : Better((b.tier, b.lod, b.dist), (a.tier, a.lod, a.dist)) ? 1 : 0);
        }

        static void Offer(Dictionary<FileKey, (int tier, int lod, double dist)> best, int landmark, int file,
            (int tier, int lod, double dist) p)
        {
            var key = new FileKey(landmark, file);
            if (!best.TryGetValue(key, out var have) || Better(p, have)) best[key] = p;
        }

        static bool Better((int tier, int lod, double dist) a, (int tier, int lod, double dist) b)
        {
            if (a.tier != b.tier) return a.tier > b.tier;
            if (a.lod != b.lod) return a.lod > b.lod;
            return a.dist < b.dist;
        }
    }
}

// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Reads the lod-meta.json written by PlayCanvas splat-transform (MIT, Copyright (c) 2011-2026 PlayCanvas Ltd.,
// https://github.com/playcanvas/splat-transform/blob/v3.10.1/src/lib/writers/write-lod.ts). Leaves are flattened the
// way the PlayCanvas engine does (v2.23.2, src/scene/gsplat-unified/gsplat-octree.js).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Gsplat
{
    internal readonly struct LodRange
    {
        public readonly int File;
        public readonly int Offset;
        public readonly int Count;

        public LodRange(int file, int offset, int count)
        {
            File = file;
            Offset = offset;
            Count = count;
        }
    }

    internal sealed class SogLodMeta
    {
        const int k_supportedVersion = 1;

        public int LodLevels;
        public int LeafCount;
        public string[] Files;
        public int[] FileLod;
        public int[] FileCount;
        public float[] Bounds;
        public LodRange[] Ranges;

        public int Count(int leaf, int lod) => Ranges[leaf * LodLevels + lod].Count;
        public LodRange Range(int leaf, int lod) => Ranges[leaf * LodLevels + lod];

        public static SogLodMeta Load(string lodMetaPath, SourceCoordinates src)
        {
            string name = lodMetaPath;
            Dictionary<string, object> root;
            try
            {
                root = MiniJson.Parse(File.ReadAllText(lodMetaPath)) as Dictionary<string, object>;
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"{name}: {e.Message}", e);
            }
            if (root == null) throw Bad(name, "top level is not an object");

            int version = (int)Num(root, "version", name);
            if (version != k_supportedVersion)
                throw new NotSupportedException($"{name}: lod-meta.json version {version} is not supported (expected {k_supportedVersion}).");
            if (root.ContainsKey("environment"))
                Debug.LogWarning($"[Gsplat] {name}: environment splats are not supported and are ignored.");

            var meta = new SogLodMeta { LodLevels = (int)Num(root, "lodLevels", name) };
            if (meta.LodLevels < 1 || meta.LodLevels > 32) throw Bad(name, $"lodLevels {meta.LodLevels} out of range");

            var baseDir = Path.GetDirectoryName(Path.GetFullPath(lodMetaPath));
            if (!(root.TryGetValue("filenames", out var fo) && fo is List<object> fl)) throw Bad(name, "missing filenames");
            meta.Files = new string[fl.Count];
            meta.FileLod = new int[fl.Count];
            meta.FileCount = new int[fl.Count];
            for (int f = 0; f < fl.Count; f++)
            {
                if (!(fl[f] is string rel) || rel.Contains("..") || Path.IsPathRooted(rel)) throw Bad(name, $"bad filename #{f}");
                meta.Files[f] = Path.Combine(baseDir, rel);
                meta.FileLod[f] = -1;
            }

            if (!(root.TryGetValue("tree", out var to) && to is Dictionary<string, object> tree)) throw Bad(name, "missing tree");
            var bounds = new List<float>();
            var ranges = new List<LodRange>();
            Flatten(tree, meta, bounds, ranges, name, 0);
            meta.LeafCount = bounds.Count / 6;
            meta.Bounds = bounds.ToArray();
            meta.Ranges = ranges.ToArray();

            ReadUnitCounts(meta, name);
            ValidateRanges(meta, name);
            ConvertAxes(meta, src);
            return meta;
        }

        static void Flatten(Dictionary<string, object> node, SogLodMeta meta, List<float> bounds, List<LodRange> ranges,
            string name, int depth)
        {
            if (depth > 64) throw Bad(name, "tree deeper than 64 levels");
            if (node.TryGetValue("children", out var co) && co is List<object> children)
            {
                foreach (var c in children)
                {
                    if (!(c is Dictionary<string, object> child)) throw Bad(name, "child is not an object");
                    Flatten(child, meta, bounds, ranges, name, depth + 1);
                }
                return;
            }

            if (!(node.TryGetValue("bound", out var bo) && bo is Dictionary<string, object> bound)) throw Bad(name, "leaf without bound");
            var min = Vec3(bound, "min", name);
            var max = Vec3(bound, "max", name);
            bounds.AddRange(new[] { min[0], min[1], min[2], max[0], max[1], max[2] });

            var lods = node.TryGetValue("lods", out var lo) ? lo as Dictionary<string, object> : null;
            for (int lod = 0; lod < meta.LodLevels; lod++)
            {
                if (lods == null || !lods.TryGetValue(lod.ToString(System.Globalization.CultureInfo.InvariantCulture), out var ro))
                {
                    ranges.Add(default);
                    continue;
                }
                if (!(ro is Dictionary<string, object> r)) throw Bad(name, "lod entry is not an object");
                int file = (int)Num(r, "file", name);
                int offset = r.ContainsKey("offset") ? (int)Num(r, "offset", name) : 0;
                int count = r.ContainsKey("count") ? (int)Num(r, "count", name) : 0;
                if (file < 0 || file >= meta.Files.Length) throw Bad(name, $"lod {lod} refers to file #{file} of {meta.Files.Length}");
                if (offset < 0 || count < 0) throw Bad(name, $"lod {lod} has a negative offset or count");
                if (count > 0)
                {
                    if (meta.FileLod[file] >= 0 && meta.FileLod[file] != lod) throw Bad(name, $"file #{file} used by two levels");
                    meta.FileLod[file] = lod;
                }
                ranges.Add(count > 0 ? new LodRange(file, offset, count) : default);
            }
        }

        static void ReadUnitCounts(SogLodMeta meta, string name)
        {
            for (int f = 0; f < meta.Files.Length; f++)
            {
                if (!File.Exists(meta.Files[f])) throw Bad(name, $"unit '{meta.Files[f]}' does not exist");
                object parsed;
                try
                {
                    parsed = MiniJson.Parse(File.ReadAllText(meta.Files[f]));
                }
                catch (InvalidDataException e)
                {
                    throw Bad(name, $"unit '{meta.Files[f]}' is not valid JSON ({e.Message})");
                }
                if (!(parsed is Dictionary<string, object> unit)
                    || !(unit.TryGetValue("count", out var co) && co is double count)
                    || count < 0 || count > int.MaxValue || count != Math.Floor(count))
                    throw Bad(name, $"unit '{meta.Files[f]}' has no valid count");
                meta.FileCount[f] = (int)count;
            }
        }

        static void ValidateRanges(SogLodMeta meta, string name)
        {
            foreach (var r in meta.Ranges)
                if (r.Count > 0 && (long)r.Offset + r.Count > meta.FileCount[r.File])
                    throw Bad(name, $"range {r.Offset}+{r.Count} runs past the end of '{meta.Files[r.File]}' ({meta.FileCount[r.File]} splats)");
        }

        static void ConvertAxes(SogLodMeta meta, SourceCoordinates src)
        {
            var (sx, sy, sz) = GsplatUtils.AxisSigns(src);
            var signs = new[] { sx, sy, sz };
            for (int leaf = 0; leaf < meta.LeafCount; leaf++)
            for (int a = 0; a < 3; a++)
            {
                if (signs[a] > 0) continue;
                int b = leaf * 6;
                float mn = meta.Bounds[b + a], mx = meta.Bounds[b + 3 + a];
                meta.Bounds[b + a] = -mx;
                meta.Bounds[b + 3 + a] = -mn;
            }
        }

        static float[] Vec3(Dictionary<string, object> d, string key, string name)
        {
            if (!(d.TryGetValue(key, out var o) && o is List<object> l && l.Count == 3)) throw Bad(name, $"bound.{key} is not a 3-vector");
            var v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!(l[i] is double x) || double.IsNaN(x) || double.IsInfinity(x)) throw Bad(name, $"bound.{key} is not finite");
                v[i] = (float)x;
            }
            return v;
        }

        static double Num(Dictionary<string, object> d, string key, string name)
        {
            if (!(d.TryGetValue(key, out var o) && o is double v)) throw Bad(name, $"missing number '{key}'");
            return v;
        }

        static InvalidDataException Bad(string name, string what) => new($"{name}: lod-meta.json {what}");
    }
}

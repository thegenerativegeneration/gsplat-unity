// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Gsplat.Tests
{
    public sealed class PlyData
    {
        public int Count;
        public Vector3[] Positions;
        public Vector3[] Dc;
        public float[] Opacity;
        public Vector3[] LogScale;
        public Vector4[] Rot; // w, x, y, z as stored in the PLY
        public float[] Rest;  // Count * 45, f_rest order
    }

    public static class Fixtures
    {
        public static string Path(string relative)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SogLoader).Assembly);
            return System.IO.Path.Combine(info.resolvedPath, "Tests", "Fixtures~", relative);
        }

        // Reads the binary little-endian float PLYs written by make-fixtures.mjs and splat-transform.
        public static PlyData ReadPly(string relative)
        {
            var bytes = File.ReadAllBytes(Path(relative));
            int end = IndexOf(bytes, Encoding.ASCII.GetBytes("end_header\n")) + "end_header\n".Length;
            var header = Encoding.ASCII.GetString(bytes, 0, end).Split('\n');
            int count = 0;
            var props = new System.Collections.Generic.List<string>();
            foreach (var line in header)
            {
                var t = line.Split(' ');
                if (t[0] == "element" && t[1] == "vertex") count = int.Parse(t[2]);
                if (t[0] == "property")
                {
                    if (t[1] != "float") throw new InvalidDataException($"unexpected property type {t[1]}");
                    props.Add(t[2]);
                }
            }
            int stride = props.Count;
            var f = new float[count * stride];
            Buffer.BlockCopy(bytes, end, f, 0, f.Length * 4);
            int I(string name) => props.IndexOf(name);
            var d = new PlyData
            {
                Count = count, Positions = new Vector3[count], Dc = new Vector3[count], Opacity = new float[count],
                LogScale = new Vector3[count], Rot = new Vector4[count], Rest = new float[count * 45]
            };
            int rest0 = I("f_rest_0");
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                d.Positions[i] = new Vector3(f[o + I("x")], f[o + I("y")], f[o + I("z")]);
                d.Dc[i] = new Vector3(f[o + I("f_dc_0")], f[o + I("f_dc_1")], f[o + I("f_dc_2")]);
                d.Opacity[i] = f[o + I("opacity")];
                d.LogScale[i] = new Vector3(f[o + I("scale_0")], f[o + I("scale_1")], f[o + I("scale_2")]);
                d.Rot[i] = new Vector4(f[o + I("rot_0")], f[o + I("rot_1")], f[o + I("rot_2")], f[o + I("rot_3")]);
                if (rest0 >= 0)
                    for (int k = 0; k < 45; k++) d.Rest[i * 45 + k] = f[o + rest0 + k];
            }
            return d;
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            throw new InvalidDataException("PLY without end_header");
        }
    }
}

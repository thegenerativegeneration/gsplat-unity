// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.IO;
using NUnit.Framework;

namespace Gsplat.Tests
{
    public class SogLodMetaTests
    {
        static string WriteTemp(string name, string json)
        {
            var dir = Path.Combine(Path.GetTempPath(), "gsplat-lodmeta-" + name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "lod-meta.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Test]
        public void FixtureFlattensToLeavesWithEveryLevel()
        {
            var meta = SogLodMeta.Load(Fixtures.Path("lod/lod-meta.json"), SourceCoordinates.RUF);
            Assert.AreEqual(3, meta.LodLevels);
            Assert.GreaterOrEqual(meta.LeafCount, 6);
            long[] perLevel = new long[3];
            for (int leaf = 0; leaf < meta.LeafCount; leaf++)
                for (int lod = 0; lod < 3; lod++)
                    perLevel[lod] += meta.Count(leaf, lod);
            Assert.AreEqual(6000, perLevel[0]);
            Assert.That(perLevel[1], Is.InRange(2700, 3300));
            for (int f = 0; f < meta.Files.Length; f++)
                Assert.IsTrue(File.Exists(meta.Files[f]), meta.Files[f]);
        }

        [Test]
        public void BackAxisFlipsBoundsOnZ()
        {
            var ruf = SogLodMeta.Load(Fixtures.Path("lod/lod-meta.json"), SourceCoordinates.RUF);
            var rub = SogLodMeta.Load(Fixtures.Path("lod/lod-meta.json"), SourceCoordinates.RUB);
            Assert.AreEqual(-ruf.Bounds[5], rub.Bounds[2], 1e-6f);
            Assert.AreEqual(-ruf.Bounds[2], rub.Bounds[5], 1e-6f);
            Assert.AreEqual(ruf.Bounds[0], rub.Bounds[0], 1e-6f);
        }

        [Test]
        public void RangePastTheEndOfItsUnitIsRejected()
        {
            var json = File.ReadAllText(Fixtures.Path("lod/lod-meta.json"));
            var dir = Path.GetDirectoryName(Fixtures.Path("lod/lod-meta.json"));
            var bad = json.Replace("\"offset\":0", "\"offset\":99999999");
            Assert.AreNotEqual(json, bad, "fixture must contain an offset of 0");
            var path = WriteTemp("pastend", bad);
            foreach (var f in Directory.GetDirectories(dir))
                CopyDir(f, Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(f)));
            var e = Assert.Throws<InvalidDataException>(() => SogLodMeta.Load(path, SourceCoordinates.RUF));
            StringAssert.Contains("past the end", e.Message);
        }

        [Test]
        public void UnknownVersionIsNotSupported()
        {
            var path = WriteTemp("version", "{\"version\":2,\"lodLevels\":1,\"filenames\":[],\"tree\":{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"lods\":{}}}");
            Assert.Throws<System.NotSupportedException>(() => SogLodMeta.Load(path, SourceCoordinates.RUF));
        }

        [TestCase("")]
        [TestCase("{")]
        [TestCase("{\"version\":1}")]
        [TestCase("{\"version\":1,\"lodLevels\":1,\"filenames\":[\"0_0/meta.json\"],\"tree\":{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"lods\":{\"0\":{\"file\":3,\"offset\":0,\"count\":1}}}}")]
        public void MalformedIsInvalidData(string json)
        {
            var path = WriteTemp("malformed", json);
            var e = Assert.Throws<InvalidDataException>(() => SogLodMeta.Load(path, SourceCoordinates.RUF));
            StringAssert.Contains("lod-meta.json", e.Message);
        }

        [Test]
        public void MiniJsonParsesNumbersStringsAndNesting()
        {
            var o = (System.Collections.Generic.Dictionary<string, object>)MiniJson.Parse(
                "{\"a\":[1,-2.5e3,true,null,\"x\\\"y\\u00e9\"],\"b\":{}}");
            var a = (System.Collections.Generic.List<object>)o["a"];
            Assert.AreEqual(1.0, a[0]);
            Assert.AreEqual(-2500.0, a[1]);
            Assert.AreEqual(true, a[2]);
            Assert.IsNull(a[3]);
            Assert.AreEqual("x\"yé", a[4]);
        }

        static string WriteWithUnit(string name, string unitJson)
        {
            var path = WriteTemp(name, "{\"version\":1,\"lodLevels\":1,\"filenames\":[\"0_0/meta.json\"],\"tree\":{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]},\"lods\":{\"0\":{\"file\":0,\"offset\":0,\"count\":1}}}}");
            var unitDir = Path.Combine(Path.GetDirectoryName(path), "0_0");
            Directory.CreateDirectory(unitDir);
            File.WriteAllText(Path.Combine(unitDir, "meta.json"), unitJson);
            return path;
        }

        [Test]
        public void UnitWithoutCountIsInvalidData()
        {
            var path = WriteWithUnit("nocount", "{\"version\":2}");
            var e = Assert.Throws<InvalidDataException>(() => SogLodMeta.Load(path, SourceCoordinates.RUF));
            StringAssert.Contains("count", e.Message);
            StringAssert.Contains("lod-meta.json", e.Message);
        }

        [Test]
        public void MalformedUnitIsInvalidData()
        {
            var path = WriteWithUnit("badunit", "{\"count\":");
            var e = Assert.Throws<InvalidDataException>(() => SogLodMeta.Load(path, SourceCoordinates.RUF));
            StringAssert.Contains("lod-meta.json", e.Message);
        }

        [Test]
        public void BadUnicodeEscapeIsInvalidData()
        {
            Assert.Throws<InvalidDataException>(() => MiniJson.Parse("\"\\uZZZZ\""));
            Assert.Throws<InvalidDataException>(() => MiniJson.Parse("\"\\u 12 \""));
        }

        [Test]
        public void DeepNestingIsInvalidDataNotACrash()
        {
            var json = new string('[', 10000) + new string(']', 10000);
            var e = Assert.Throws<InvalidDataException>(() => MiniJson.Parse(json));
            StringAssert.Contains("nesting", e.Message);
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        }
    }
}

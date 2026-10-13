// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Gsplat
{
    internal interface ISogEntrySource : IDisposable
    {
        string DisplayName { get; }
        bool TryRead(string name, out byte[] bytes);
    }

    internal sealed class ZipSogSource : ISogEntrySource
    {
        readonly FileStream m_stream;
        readonly ZipArchive m_zip;
        readonly Dictionary<string, ZipArchiveEntry> m_entries = new(StringComparer.Ordinal);

        public string DisplayName { get; }

        public ZipSogSource(string path)
        {
            DisplayName = Path.GetFileName(path);
            m_stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            try
            {
                m_zip = new ZipArchive(m_stream, ZipArchiveMode.Read);
            }
            catch (Exception e)
            {
                m_stream.Dispose();
                throw new InvalidDataException($"SOG: failed to read '{DisplayName}' as a ZIP bundle: {e.Message}", e);
            }

            foreach (var entry in m_zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.Contains("/") && !string.IsNullOrEmpty(name))
                    m_entries[name] = entry;
            }
        }

        public bool TryRead(string name, out byte[] bytes)
        {
            bytes = null;
            if (!m_entries.TryGetValue(name, out var entry))
                return false;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = ms.ToArray();
            return true;
        }

        public void Dispose()
        {
            m_zip.Dispose();
            m_stream.Dispose();
        }
    }

    // Folder-style SOG as written by splat-transform for LOD units: <dir>/meta.json plus sibling WebPs.
    internal sealed class FolderSogSource : ISogEntrySource
    {
        readonly string m_dir;

        public string DisplayName { get; }

        public FolderSogSource(string metaJsonPath)
        {
            m_dir = Path.GetDirectoryName(Path.GetFullPath(metaJsonPath));
            DisplayName = metaJsonPath;
        }

        public bool TryRead(string name, out byte[] bytes)
        {
            bytes = null;
            if (name.Contains("/") || name.Contains("\\") || name.Contains(".."))
                return false;
            var path = Path.Combine(m_dir, name);
            if (!File.Exists(path))
                return false;
            bytes = File.ReadAllBytes(path);
            return true;
        }

        public void Dispose() { }
    }
}

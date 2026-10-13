// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gsplat
{
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new InvalidDataException("JSON: null input");
            int i = 0;
            var v = Value(json, ref i);
            Ws(json, ref i);
            if (i != json.Length) throw Error(json, i, "trailing characters");
            return v;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            return Num(s, ref i);
        }

        static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Error(s, i, "expected key");
                var key = Str(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Error(s, i, "expected ':'");
                i++;
                d[key] = Value(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw Error(s, i, "expected ',' or '}'");
            }
        }

        static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw Error(s, i, "expected ',' or ']'");
            }
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': case '\\': case '/': sb.Append(e); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Error(s, i, "bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Error(s, i, "bad escape");
                }
            }
            throw Error(s, i, "unterminated string");
        }

        static double Num(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw Error(s, start, "bad value");
            return v;
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static InvalidDataException Error(string s, int i, string what) => new($"JSON: {what} at offset {i}");
    }
}

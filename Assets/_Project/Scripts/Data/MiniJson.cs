using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 极简 JSON 对象解析器，只为解析 MQTT 报文里的 {"value":25.3,"ts":1730000000} 这类扁平对象。
    ///
    /// 为什么不用 Newtonsoft（工程已装 com.unity.nuget.newtonsoft-json）：
    ///   本路径在 10Hz × 500 点位下每秒被调用 8000 次，Newtonsoft 的 JObject 解析
    ///   每次都会产生 Dictionary/字符串分配，是 GC 的主要来源之一。
    ///   P1 验收的硬指标是「零 GC」，因此这里只做“够用”的手写解析：
    ///   · 只支持对象、字符串、数字、true/false/null（不支持数组、嵌套对象 → 遇到即返回 false）
    ///   · 数值直接 float.TryParse，不经过 double→float 的装箱
    ///   · 键名用 ordinal 比较，不做大小写折叠
    ///
    /// 若将来载荷变复杂（嵌套/数组），应换成 Newtonsoft 并重新压测 GC。
    /// </summary>
    internal static class MiniJson
    {
        /// <summary>解析结果的只读视图。字段数很小（&lt;16），线性查找比建哈希表更快。</summary>
        public struct Object
        {
            internal string[] Keys;
            internal string[] StringValues;
            internal double[] NumberValues;
            internal byte[] Kinds;   // 0=string 1=number 2=bool 3=null

            internal const byte KindString = 0;
            internal const byte KindNumber = 1;
            internal const byte KindBool = 2;
            internal const byte KindNull = 3;

            public bool IsValid { get { return Keys != null; } }

            public bool TryGetNumber(string key, out double value)
            {
                value = 0d;
                if (Keys == null || string.IsNullOrEmpty(key)) return false;
                for (int i = 0; i < Keys.Length; i++)
                {
                    if (!string.Equals(Keys[i], key, StringComparison.Ordinal)) continue;
                    if (Kinds[i] == KindNumber)
                    {
                        value = NumberValues[i];
                        return true;
                    }
                    // 字符串形式的数字也接受（有些网关会把数值发成 "25.3"）
                    if (Kinds[i] == KindString &&
                        double.TryParse(StringValues[i], NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                    return false;
                }
                return false;
            }

            /// <summary>float 便捷重载，避免调用方 double→float 的隐式转换歧义。</summary>
            public bool TryGetNumber(string key, out float value)
            {
                double d;
                if (TryGetNumber(key, out d))
                {
                    value = (float)d;
                    return true;
                }
                value = 0f;
                return false;
            }

            public bool TryGetString(string key, out string value)
            {
                value = null;
                if (Keys == null || string.IsNullOrEmpty(key)) return false;
                for (int i = 0; i < Keys.Length; i++)
                {
                    if (!string.Equals(Keys[i], key, StringComparison.Ordinal)) continue;
                    if (Kinds[i] == KindString)
                    {
                        value = StringValues[i];
                        return true;
                    }
                    return false;
                }
                return false;
            }
        }

        /// <summary>解析 UTF-8 字节。失败返回 false（调用方计入 parseFail）。</summary>
        public static bool TryParse(byte[] utf8, out Object result)
        {
            result = default(Object);
            if (utf8 == null || utf8.Length == 0) return false;

            try
            {
                int pos = 0;
                SkipWhitespace(utf8, ref pos);
                if (pos >= utf8.Length || utf8[pos] != (byte)'{') return false;

                List<string> keys = new List<string>(8);
                List<string> svals = new List<string>(8);
                List<double> nvals = new List<double>(8);
                List<byte> kinds = new List<byte>(8);

                pos++; // 跳过 {

                while (true)
                {
                    SkipWhitespace(utf8, ref pos);
                    if (pos >= utf8.Length) return false;

                    if (utf8[pos] == (byte)'}')
                    {
                        pos++;
                        break;
                    }

                    // ---- 键 ----
                    if (utf8[pos] != (byte)'"') return false;
                    string key;
                    if (!ReadString(utf8, ref pos, out key)) return false;

                    SkipWhitespace(utf8, ref pos);
                    if (pos >= utf8.Length || utf8[pos] != (byte)':') return false;
                    pos++;
                    SkipWhitespace(utf8, ref pos);

                    // ---- 值 ----
                    string sval;
                    double nval;
                    byte kind;
                    if (!ReadValue(utf8, ref pos, out sval, out nval, out kind)) return false;

                    keys.Add(key);
                    svals.Add(sval);
                    nvals.Add(nval);
                    kinds.Add(kind);

                    SkipWhitespace(utf8, ref pos);
                    if (pos < utf8.Length && utf8[pos] == (byte)',')
                    {
                        pos++;
                        continue;
                    }
                    if (pos < utf8.Length && utf8[pos] == (byte)'}')
                    {
                        pos++;
                        break;
                    }
                    return false;
                }

                result.Keys = keys.ToArray();
                result.StringValues = svals.ToArray();
                result.NumberValues = nvals.ToArray();
                result.Kinds = kinds.ToArray();
                return true;
            }
            catch (Exception)
            {
                result = default(Object);
                return false;
            }
        }

        private static void SkipWhitespace(byte[] b, ref int pos)
        {
            while (pos < b.Length)
            {
                byte c = b[pos];
                if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n') pos++;
                else break;
            }
        }

        private static bool ReadString(byte[] b, ref int pos, out string value)
        {
            value = null;
            if (pos >= b.Length || b[pos] != (byte)'"') return false;
            pos++;

            StringBuilder sb = new StringBuilder(16);
            while (pos < b.Length)
            {
                byte c = b[pos++];
                if (c == (byte)'"')
                {
                    value = sb.ToString();
                    return true;
                }
                if (c == (byte)'\\')
                {
                    if (pos >= b.Length) return false;
                    byte esc = b[pos++];
                    switch (esc)
                    {
                        case (byte)'"': sb.Append('"'); break;
                        case (byte)'\\': sb.Append('\\'); break;
                        case (byte)'/': sb.Append('/'); break;
                        case (byte)'b': sb.Append('\b'); break;
                        case (byte)'f': sb.Append('\f'); break;
                        case (byte)'n': sb.Append('\n'); break;
                        case (byte)'r': sb.Append('\r'); break;
                        case (byte)'t': sb.Append('\t'); break;
                        case (byte)'u':
                            {
                                if (pos + 4 > b.Length) return false;
                                int code = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    int d = HexVal(b[pos++]);
                                    if (d < 0) return false;
                                    code = (code << 4) | d;
                                }
                                sb.Append((char)code);
                                break;
                            }
                        default: return false;
                    }
                }
                else
                {
                    sb.Append((char)c);
                }
            }
            return false;
        }

        private static int HexVal(byte c)
        {
            if (c >= (byte)'0' && c <= (byte)'9') return c - '0';
            if (c >= (byte)'a' && c <= (byte)'f') return c - 'a' + 10;
            if (c >= (byte)'A' && c <= (byte)'F') return c - 'A' + 10;
            return -1;
        }

        private static bool ReadValue(byte[] b, ref int pos, out string sval, out double nval, out byte kind)
        {
            sval = null;
            nval = 0d;
            kind = Object.KindNull;

            if (pos >= b.Length) return false;

            byte c = b[pos];

            if (c == (byte)'"')
            {
                if (!ReadString(b, ref pos, out sval)) return false;
                kind = Object.KindString;
                return true;
            }

            if (c == (byte)'t')
            {
                if (Match(b, pos, "true")) { pos += 4; kind = Object.KindBool; return true; }
                return false;
            }

            if (c == (byte)'f')
            {
                if (Match(b, pos, "false")) { pos += 5; kind = Object.KindBool; return true; }
                return false;
            }

            if (c == (byte)'n')
            {
                if (Match(b, pos, "null")) { pos += 4; kind = Object.KindNull; return true; }
                return false;
            }

            if (c == (byte)'{' || c == (byte)'[')
            {
                // 不支持嵌套：直接判失败，让调用方计入 parseFail 并打日志，
                // 而不是静默只取到外层字段（静默失败比报错更难查）。
                return false;
            }

            // ---- 数字 ----
            int start = pos;
            if (pos < b.Length && (b[pos] == (byte)'-' || b[pos] == (byte)'+')) pos++;
            while (pos < b.Length)
            {
                byte d = b[pos];
                if ((d >= (byte)'0' && d <= (byte)'9') || d == (byte)'.' ||
                    d == (byte)'e' || d == (byte)'E' || d == (byte)'-' || d == (byte)'+')
                {
                    pos++;
                }
                else break;
            }
            if (pos == start) return false;

            string num = Encoding.ASCII.GetString(b, start, pos - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out nval))
                return false;
            kind = Object.KindNumber;
            return true;
        }

        private static bool Match(byte[] b, int pos, string literal)
        {
            if (pos + literal.Length > b.Length) return false;
            for (int i = 0; i < literal.Length; i++)
            {
                if (b[pos + i] != (byte)literal[i]) return false;
            }
            return true;
        }
    }
}

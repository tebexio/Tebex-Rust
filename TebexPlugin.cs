using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Facepunch.Extend;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Tebex.Plugin;
using UnityEngine;

namespace Tebex.QR
{
    public sealed class QrCode
    {
        public byte[] Bytes;

        private QrCode(byte[] bytes) => Bytes = bytes;

        public bool[,] GetModules()
        {
            int version = Bytes[0];
            int size = Bytes[2];
            var modules = new bool[size, size];
            int bitIndex = 0;
            for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                int byteIndex = 3 + (bitIndex >> 3);
                int bitInByte = 7 - (bitIndex & 7);
                bool black = ((Bytes[byteIndex] >> bitInByte) & 1) != 0;
                modules[r, c] = black;
                bitIndex++;
            }
            return modules;
        }

        public static QrCode Encode(string content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));

            if (!content.StartsWith("https://"))
            {
                throw new ArgumentException("Content must be a web address beginning with https://, got " + content);
            }

            if (content.Contains("javascript:"))
            {
                throw new ArgumentException("Content must not contain Javascript");
            }

            if (content.Contains("<script>"))
            {
                throw new ArgumentException("Content must not contain script tags");
            }

            if (content.Contains("eval(") || content.Contains("onerror=") || content.Contains("onload=") ||
                content.Contains("document."))
            {
                throw new ArgumentException("Invalid content url");
            }

            byte[] dataBytes = Encoding.UTF8.GetBytes(content);

            int version = ChooseVersionByteModeL(dataBytes.Length);
            if (version == 0)
                throw new ArgumentException("Content too long for versions 1..4 at EC Level L in this basic implementation.");

            int size = 21 + 4 * (version - 1);

            GetV1to4LParams(version, out int dataCodewords, out int eccCodewords, out int totalCodewords);

            var bb = new BitBuffer();
            bb.AppendBits(0b0100, 4);
            bb.AppendBits(dataBytes.Length, 8);
            foreach (byte b in dataBytes) bb.AppendBits(b, 8);

            int totalDataBits = dataCodewords * 8;

            int remaining = totalDataBits - bb.BitLength;
            if (remaining > 0)
                bb.AppendBits(0, Math.Min(4, remaining));

            while ((bb.BitLength & 7) != 0) bb.AppendBits(0, 1);

            bool toggle = true;
            while (bb.BitLength < totalDataBits)
            {
                bb.AppendBits(toggle ? 0xEC : 0x11, 8);
                toggle = !toggle;
            }

            byte[] dataCw = bb.ToBytes();
            if (dataCw.Length != dataCodewords)
                throw new InvalidOperationException("Unexpected data codeword length.");

            byte[] eccCw = ReedSolomon.ComputeECC(dataCw, eccCodewords);

            var allCw = new byte[totalCodewords];
            Buffer.BlockCopy(dataCw, 0, allCw, 0, dataCw.Length);
            Buffer.BlockCopy(eccCw, 0, allCw, dataCw.Length, eccCw.Length);

            var modules = new bool[size, size];
            var isFunction = new bool[size, size];

            DrawFunctionPatterns(version, modules, isFunction);

            PlaceCodewords(modules, isFunction, allCw);

            int bestMask = -1;
            int bestPenalty = int.MaxValue;
            bool[,] best = null;

            for (int mask = 0; mask < 8; mask++)
            {
                var temp = (bool[,])modules.Clone();
                ApplyMask(temp, isFunction, mask);
                WriteFormatInfo(temp, isFunction, ecLevelBits: 0b01, mask);
                int penalty = PenaltyScore(temp);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestMask = mask;
                    best = temp;
                }
            }

            byte[] packed = PackModules(best);
            byte[] output = new byte[3 + packed.Length];
            output[0] = (byte)version;
            output[1] = 0;
            output[2] = (byte)size;
            Buffer.BlockCopy(packed, 0, output, 3, packed.Length);

            return new QrCode(output);
        }

        public static string Decode(QrCode code)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));

            int version = code.Bytes[0];
            int size = code.Bytes[2];
            var modules = code.GetModules();

            var isFunction = new bool[size, size];
            var dummy = new bool[size, size];
            DrawFunctionPatterns(version, dummy, isFunction);

            int mask = ReadMaskFromFormat(modules);

            ApplyMask(modules, isFunction, mask);

            GetV1to4LParams(version, out int dataCodewords, out int eccCodewords, out int totalCodewords);
            byte[] allCw = ReadCodewords(modules, isFunction, totalCodewords);

            byte[] dataCw = new byte[dataCodewords];
            Buffer.BlockCopy(allCw, 0, dataCw, 0, dataCodewords);

            var br = new BitReader(dataCw);

            int mode = br.ReadBits(4);
            if (mode == 0) return "";
            if (mode != 0b0100) throw new NotSupportedException("Only Byte mode is supported by this basic decoder.");

            int count = br.ReadBits(8);
            var payload = new byte[count];
            for (int i = 0; i < count; i++)
                payload[i] = (byte)br.ReadBits(8);

            return Encoding.UTF8.GetString(payload);
        }

        private static int ChooseVersionByteModeL(int byteLen)
        {
            if (byteLen <= 17) return 1;
            if (byteLen <= 32) return 2;
            if (byteLen <= 53) return 3;
            if (byteLen <= 78) return 4;
            return 0;
        }

        private static void GetV1to4LParams(int version, out int data, out int ecc, out int total)
        {
            switch (version)
            {
                case 1: data = 19; ecc = 7; total = 26; return;
                case 2: data = 34; ecc = 10; total = 44; return;
                case 3: data = 55; ecc = 15; total = 70; return;
                case 4: data = 80; ecc = 20; total = 100; return;
                default: throw new NotSupportedException("This basic implementation supports versions 1..4 only.");
            }
        }

        private static void DrawFunctionPatterns(int version, bool[,] modules, bool[,] isFunction)
        {
            int size = modules.GetLength(0);

            DrawFinder(modules, isFunction, 0, 0);
            DrawFinder(modules, isFunction, 0, size - 7);
            DrawFinder(modules, isFunction, size - 7, 0);

            DrawSeparators(isFunction, 0, 0);
            DrawSeparators(isFunction, 0, size - 7);
            DrawSeparators(isFunction, size - 7, 0);

            for (int i = 8; i < size - 8; i++)
            {
                bool val = (i % 2) == 0;
                modules[6, i] = val; isFunction[6, i] = true;
                modules[i, 6] = val; isFunction[i, 6] = true;
            }

            int darkRow = 4 * version + 9;
            modules[darkRow, 8] = true;
            isFunction[darkRow, 8] = true;

            ReserveFormatAreas(isFunction);

            DrawAlignmentPatterns(version, modules, isFunction);

        }

        private static void DrawFinder(bool[,] modules, bool[,] isFunction, int top, int left)
        {
            for (int r = 0; r < 7; r++)
            for (int c = 0; c < 7; c++)
            {
                int rr = top + r, cc = left + c;
                bool on =
                    (r == 0 || r == 6 || c == 0 || c == 6) ||
                    (r >= 2 && r <= 4 && c >= 2 && c <= 4);

                modules[rr, cc] = on;
                isFunction[rr, cc] = true;
            }
        }

        private static void DrawSeparators(bool[,] isFunction, int top, int left)
        {
            int size = isFunction.GetLength(0);

            for (int i = -1; i <= 7; i++)
            {
                SetFunc(isFunction, top - 1, left + i, size);
                SetFunc(isFunction, top + 7, left + i, size);
                SetFunc(isFunction, top + i, left - 1, size);
                SetFunc(isFunction, top + i, left + 7, size);
            }
        }

        private static void SetFunc(bool[,] isFunction, int r, int c, int size)
        {
            if ((uint)r < (uint)size && (uint)c < (uint)size)
                isFunction[r, c] = true;
        }

        private static void ReserveFormatAreas(bool[,] isFunction)
        {
            int size = isFunction.GetLength(0);

            for (int i = 0; i < 9; i++)
            {
                if (i != 6)
                {
                    isFunction[8, i] = true;
                    isFunction[i, 8] = true;
                }
            }

            for (int i = 0; i < 8; i++)
            {
                isFunction[8, size - 1 - i] = true;
                isFunction[size - 1 - i, 8] = true;
            }

            isFunction[8, 8] = true;
        }

        private static void DrawAlignmentPatterns(int version, bool[,] modules, bool[,] isFunction)
        {
            if (version == 1) return;

            int size = modules.GetLength(0);
            int center = size - 7;

            DrawAlignment(modules, isFunction, center - 2, center - 2);
        }

        private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int top, int left)
        {
            for (int r = 0; r < 5; r++)
            for (int c = 0; c < 5; c++)
            {
                int rr = top + r, cc = left + c;
                bool on =
                    (r == 0 || r == 4 || c == 0 || c == 4) ||
                    (r == 2 && c == 2);

                modules[rr, cc] = on;
                isFunction[rr, cc] = true;
            }
        }

        private static void PlaceCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
        {
            int size = modules.GetLength(0);

            int bitIndex = 0;
            int dir = -1;
            int col = size - 1;

            while (col > 0)
            {
                if (col == 6) col--;

                for (int row = (dir == -1 ? size - 1 : 0);
                     (dir == -1 ? row >= 0 : row < size);
                     row += dir)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        int x = col - c;
                        if (isFunction[row, x]) continue;

                        bool bit = GetBit(codewords, bitIndex++);
                        modules[row, x] = bit;
                    }
                }

                col -= 2;
                dir = -dir;
            }
        }

        private static bool GetBit(byte[] data, int bitIndex)
        {
            int byteIndex = bitIndex >> 3;
            int bitInByte = 7 - (bitIndex & 7);
            if (byteIndex >= data.Length) return false;
            return ((data[byteIndex] >> bitInByte) & 1) != 0;
        }

        private static byte[] ReadCodewords(bool[,] modules, bool[,] isFunction, int totalCodewords)
        {
            int size = modules.GetLength(0);
            int totalBits = totalCodewords * 8;
            var outBytes = new byte[totalCodewords];

            int bitIndex = 0;
            int dir = -1;
            int col = size - 1;

            while (col > 0 && bitIndex < totalBits)
            {
                if (col == 6) col--;

                for (int row = (dir == -1 ? size - 1 : 0);
                     (dir == -1 ? row >= 0 : row < size);
                     row += dir)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        int x = col - c;
                        if (isFunction[row, x]) continue;
                        if (bitIndex >= totalBits) break;

                        bool bit = modules[row, x];
                        int byteIndex = bitIndex >> 3;
                        int bitInByte = 7 - (bitIndex & 7);
                        if (bit) outBytes[byteIndex] |= (byte)(1 << bitInByte);
                        bitIndex++;
                    }
                    if (bitIndex >= totalBits) break;
                }

                col -= 2;
                dir = -dir;
            }

            return outBytes;
        }

        private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
        {
            int size = modules.GetLength(0);
            for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (isFunction[r, c]) continue;
                if (MaskBit(mask, r, c))
                    modules[r, c] = !modules[r, c];
            }
        }

        private static bool MaskBit(int mask, int r, int c)
        {
            return mask switch
            {
                0 => ((r + c) & 1) == 0,
                1 => (r & 1) == 0,
                2 => (c % 3) == 0,
                3 => ((r + c) % 3) == 0,
                4 => (((r / 2) + (c / 3)) & 1) == 0,
                5 => ((r * c) % 2 + (r * c) % 3) == 0,
                6 => ((((r * c) % 2) + ((r * c) % 3)) & 1) == 0,
                7 => ((((r + c) % 2) + ((r * c) % 3)) & 1) == 0,
                _ => throw new ArgumentOutOfRangeException(nameof(mask))
            };
        }

        private static void WriteFormatInfo(bool[,] modules, bool[,] isFunction, int ecLevelBits, int mask)
        {
            int format = (ecLevelBits << 3) | mask;
            int bch = ComputeBCH(format, 0b10100110111, 10);
            int bits15 = ((format << 10) | bch) ^ 0x5412;

            int size = modules.GetLength(0);

            int[] rPos = { 8,8,8,8,8,8,8,8,7,5,4,3,2,1,0 };
            int[] cPos = { 0,1,2,3,4,5,7,8,8,8,8,8,8,8,8 };

            for (int i = 0; i < 15; i++)
            {
                bool bit = ((bits15 >> (14 - i)) & 1) != 0;
                modules[rPos[i], cPos[i]] = bit;
                isFunction[rPos[i], cPos[i]] = true;
            }

            for (int i = 0; i < 8; i++)
            {
                bool bit = ((bits15 >> i) & 1) != 0;
                modules[8, size - 1 - i] = bit;
                isFunction[8, size - 1 - i] = true;
            }
            for (int i = 8; i < 15; i++)
            {
                bool bit = ((bits15 >> i) & 1) != 0;
                modules[size - 15 + i, 8] = bit;
                isFunction[size - 15 + i, 8] = true;
            }
        }

        private static int ReadMaskFromFormat(bool[,] modules)
        {
            int[] rPos = { 8,8,8,8,8,8,8,8,7,5,4,3,2,1,0 };
            int[] cPos = { 0,1,2,3,4,5,7,8,8,8,8,8,8,8,8 };

            int bits15 = 0;
            for (int i = 0; i < 15; i++)
            {
                bits15 <<= 1;
                if (modules[rPos[i], cPos[i]]) bits15 |= 1;
            }
            bits15 ^= 0x5412;
            int format = bits15 >> 10;
            return format & 0b111;
        }

        private static int ComputeBCH(int value, int poly, int polyShift)
        {
            int v = value << polyShift;
            int msbPoly = HighestBit(poly);
            while (HighestBit(v) >= msbPoly)
            {
                int shift = HighestBit(v) - msbPoly;
                v ^= (poly << shift);
            }
            return v;
        }

        private static int HighestBit(int x)
        {
            int hb = -1;
            while (x != 0) { x >>= 1; hb++; }
            return hb;
        }

        private static int PenaltyScore(bool[,] m)
        {
            int size = m.GetLength(0);
            int penalty = 0;

            for (int r = 0; r < size; r++)
            {
                penalty += RunPenalty(GetRow(m, r));
            }
            for (int c = 0; c < size; c++)
            {
                penalty += RunPenalty(GetCol(m, c));
            }

            for (int r = 0; r < size - 1; r++)
            for (int c = 0; c < size - 1; c++)
            {
                bool v = m[r, c];
                if (m[r, c + 1] == v && m[r + 1, c] == v && m[r + 1, c + 1] == v)
                    penalty += 3;
            }

            penalty += FinderLikePenalty(m);

            int dark = 0;
            for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (m[r, c]) dark++;

            int total = size * size;
            int percent = (dark * 100) / total;
            int k = Math.Abs(percent - 50) / 5;
            penalty += k * 10;

            return penalty;
        }

        private static bool[] GetRow(bool[,] m, int r)
        {
            int size = m.GetLength(0);
            var a = new bool[size];
            for (int i = 0; i < size; i++) a[i] = m[r, i];
            return a;
        }

        private static bool[] GetCol(bool[,] m, int c)
        {
            int size = m.GetLength(0);
            var a = new bool[size];
            for (int i = 0; i < size; i++) a[i] = m[i, c];
            return a;
        }

        private static int RunPenalty(bool[] line)
        {
            int p = 0;
            int run = 1;
            for (int i = 1; i < line.Length; i++)
            {
                if (line[i] == line[i - 1]) run++;
                else
                {
                    if (run >= 5) p += 3 + (run - 5);
                    run = 1;
                }
            }
            if (run >= 5) p += 3 + (run - 5);
            return p;
        }

        private static int FinderLikePenalty(bool[,] m)
        {
            int size = m.GetLength(0);
            int p = 0;

            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c <= size - 11; c++)
                {
                    if (m[r, c] && !m[r, c + 1] && m[r, c + 2] && m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] &&
                        !m[r, c + 7] && !m[r, c + 8] && !m[r, c + 9] && !m[r, c + 10])
                        p += 40;

                    if (!m[r, c] && !m[r, c + 1] && !m[r, c + 2] && !m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] &&
                        m[r, c + 7] && m[r, c + 8] && !m[r, c + 9] && m[r, c + 10])
                        p += 40;
                }
            }

            for (int c = 0; c < size; c++)
            {
                for (int r = 0; r <= size - 11; r++)
                {
                    if (m[r, c] && !m[r + 1, c] && m[r + 2, c] && m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] &&
                        !m[r + 7, c] && !m[r + 8, c] && !m[r + 9, c] && !m[r + 10, c])
                        p += 40;

                    if (!m[r, c] && !m[r + 1, c] && !m[r + 2, c] && !m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] &&
                        m[r + 7, c] && m[r + 8, c] && !m[r + 9, c] && m[r + 10, c])
                        p += 40;
                }
            }

            return p;
        }

        private static byte[] PackModules(bool[,] modules)
        {
            int size = modules.GetLength(0);
            int bitCount = size * size;
            int byteCount = (bitCount + 7) / 8;
            var packed = new byte[byteCount];

            int bitIndex = 0;
            for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (modules[r, c])
                {
                    int b = bitIndex >> 3;
                    int bitInByte = 7 - (bitIndex & 7);
                    packed[b] |= (byte)(1 << bitInByte);
                }
                bitIndex++;
            }

            return packed;
        }

        private sealed class BitBuffer
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _bitLen;

            public int BitLength => _bitLen;

            public void AppendBits(int value, int count)
            {
                if (count < 0 || count > 31) throw new ArgumentOutOfRangeException(nameof(count));
                for (int i = count - 1; i >= 0; i--)
                {
                    int bit = (value >> i) & 1;
                    AppendBit(bit != 0);
                }
            }

            private void AppendBit(bool bit)
            {
                int byteIndex = _bitLen >> 3;
                int bitInByte = 7 - (_bitLen & 7);
                if (byteIndex == _bytes.Count) _bytes.Add(0);
                if (bit) _bytes[byteIndex] |= (byte)(1 << bitInByte);
                _bitLen++;
            }

            public byte[] ToBytes()
            {
                int byteCount = (_bitLen + 7) / 8;
                var arr = _bytes.ToArray();
                if (arr.Length == byteCount) return arr;
                var outArr = new byte[byteCount];
                Array.Copy(arr, outArr, byteCount);
                return outArr;
            }
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _bitPos;

            public BitReader(byte[] data) => _data = data ?? throw new ArgumentNullException(nameof(data));

            public int ReadBits(int count)
            {
                int v = 0;
                for (int i = 0; i < count; i++)
                {
                    int byteIndex = _bitPos >> 3;
                    int bitInByte = 7 - (_bitPos & 7);
                    int bit = ((_data[byteIndex] >> bitInByte) & 1);
                    v = (v << 1) | bit;
                    _bitPos++;
                }
                return v;
            }
        }

        private static class ReedSolomon
        {
            private static readonly byte[] Exp = new byte[512];
            private static readonly byte[] Log = new byte[256];

            static ReedSolomon()
            {
                int x = 1;
                for (int i = 0; i < 255; i++)
                {
                    Exp[i] = (byte)x;
                    Log[(byte)x] = (byte)i;

                    x <<= 1;
                    if ((x & 0x100) != 0)
                        x ^= 0x11D;

                    x &= 0xFF;
                }
                for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
            }

            private static byte Mul(byte a, byte b)
            {
                if (a == 0 || b == 0) return 0;
                int la = Log[a];
                int lb = Log[b];
                return Exp[la + lb];
            }

            public static byte[] ComputeECC(byte[] data, int eccLen)
            {
                byte[] gen = BuildGenerator(eccLen);

                var ecc = new byte[eccLen];
                foreach (byte d in data)
                {
                    byte factor = (byte)(d ^ ecc[0]);
                    for (int i = 0; i < eccLen - 1; i++)
                        ecc[i] = ecc[i + 1];

                    ecc[eccLen - 1] = 0;

                    for (int i = 0; i < eccLen; i++)
                        ecc[i] ^= Mul(gen[i], factor);
                }
                return ecc;
            }

            private static byte[] BuildGenerator(int degree)
            {
                var poly = new List<byte> { 1 };
                for (int i = 0; i < degree; i++)
                {
                    byte aPow = Exp[i];
                    poly = PolyMul(poly, new List<byte> { 1, aPow });
                }

                var gen = new byte[degree];
                for (int i = 0; i < degree; i++)
                    gen[i] = poly[i + 1];
                return gen;
            }

            private static List<byte> PolyMul(List<byte> p, List<byte> q)
            {
                var outp = new byte[p.Count + q.Count - 1];
                for (int i = 0; i < p.Count; i++)
                for (int j = 0; j < q.Count; j++)
                    outp[i + j] ^= Mul(p[i], q[j]);
                return new List<byte>(outp);
            }
        }
    }
}

namespace Tebex.Headless
{
    #nullable enable
        [Serializable]
        public class AddPackagePayload
        {
            public int package_id;
            public int quantity;
            public Dictionary<string, string>? variable_data;

            public AddPackagePayload(int packageId, int qty = 1, Dictionary<string, string>? variableData = null)
            {
                package_id = packageId;
                quantity = qty;
                variable_data = variableData;
            }
        }

        [Serializable]
        public class Basket
        {
            public string ident = string.Empty;
            public bool complete;
            public string email = string.Empty;
            public string username = string.Empty;
            public List<BasketCoupon> coupons = new List<BasketCoupon>();
            public List<BasketGiftCard> gift_cards = new List<BasketGiftCard>();
            public string creator_code = string.Empty;
            public string cancel_url = string.Empty;
            public string complete_url = string.Empty;
            public bool complete_auto_redirect;
            public string country = string.Empty;
            public string ip = string.Empty;
            public string username_id = string.Empty;
            public float base_price;
            public float sales_tax;
            public float total_price;
            public string currency = string.Empty;
            public List<BasketPackage> packages = new List<BasketPackage>();
            [JsonConverter(typeof(EmptyArrayTolerantConverter<Dictionary<string, string>>))]
            public Dictionary<string, string> custom = new Dictionary<string, string>();
            [JsonConverter(typeof(EmptyArrayTolerantConverter<BasketLinks>))]
            public BasketLinks links = new BasketLinks();
        }

        [Serializable]
        public class BasketAuthLink
        {
            public string name = string.Empty;
            public string url = string.Empty;
        }

        [Serializable]
        public class BasketCoupon
        {
            public string coupon_code;

            public BasketCoupon(string couponCode)
            {
                coupon_code = couponCode;
            }
        }

        [Serializable]
        public class BasketGiftCard
        {
            public string card_number = string.Empty;
        }

        [Serializable]
        public class BasketLinks
        {
            public string payment = string.Empty;
            public string checkout = string.Empty;
        }

        [Serializable]
        public class BasketPackage
        {
            public int id = -1;
            public string name = string.Empty;
            public string description = string.Empty;
            public InnerPackageMeta in_basket = new InnerPackageMeta();
        }

        [Serializable]
        public class BasketRevenueShare
        {
            public string wallet_ref = string.Empty;
            public float amount = -1.0f;
            public int gateway_fee_percent = -1;
        }

    #nullable enable
        [Serializable]
        public class Category
        {
            public int id = -1;
            public string name = string.Empty;
            public string slug = string.Empty;
            public string description = string.Empty;
            public PackageCategory? parent = null;
            public int order = -1;
            public string display_type = string.Empty;
            public bool tiered = false;
            public List<Package> packages = new List<Package>();
            public Tier? active_tier = null;
        }

    #nullable enable
        [Serializable]
        public class CreateBasketPayload
        {
            public string? email;
            public string? username;
            public string? cancel_url;
            public string? complete_url;
            public Dictionary<string, string> custom = new Dictionary<string, string>();
            public bool complete_auto_redirect;

            public CreateBasketPayload(string? email = null, string? username = null, string? cancelUrl = null,
                string? completeUrl = null, Dictionary<string, string>? custom = null, bool completeAutoRedirect = true)
            {
                this.email = email;
                this.username = username;
                cancel_url = cancelUrl;
                complete_url = completeUrl;
                this.custom = custom ?? new Dictionary<string, string>();
                complete_auto_redirect = completeAutoRedirect;
            }
        }

        [Serializable]
        public class CreatorCode
        {
            public string code;

            public CreatorCode(string c)
            {
                code = c;
            }
        }

        public class EmptyArrayTolerantConverter<T> : JsonConverter where T : new()
        {
            public override bool CanConvert(Type objectType) => objectType == typeof(T);

            public override bool CanWrite => false;

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.StartArray)
                {
                    reader.Skip();
                    return new T();
                }
                if (reader.TokenType == JsonToken.Null) return new T();

                var value = new T();
                serializer.Populate(reader, value);
                return value;
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
                => throw new NotImplementedException();
        }

        [Serializable]
        public class GiftCard
        {
            public string card_number;

            public GiftCard(string cardNumber)
            {
                card_number = cardNumber;
            }
        }

        [Serializable]
        public class InnerPackageMeta
        {
            public int quantity = -1;
            public float price = -1.0f;
            public string gift_username_id = string.Empty;
            public string gift_username = string.Empty;
            public string gift_image = string.Empty;
        }

        [Serializable]
        public class Package
        {
            public int id;
            public string name = string.Empty;
            public string description = string.Empty;
            public string image = string.Empty;
            public string type = string.Empty;
            public PackageCategory category = new PackageCategory();
            public float base_price;
            public float sales_tax;
            public float total_price;
            public string currency = string.Empty;
            public float prorate_price;
            public float discount;
            public bool disable_quantity;
            public bool disable_gifting;
            public string created_at = string.Empty;
            public string updated_at = string.Empty;
            public int order = -1;
            public List<PackageVariable> variables = new List<PackageVariable>();
        }

        [Serializable]
        public class PackageCategory
        {
            public int id;
            public string name;
        }

        [Serializable]
        public class PackageQuantityPayload
        {
            public int quantity;

            public PackageQuantityPayload(int qty)
            {
                quantity = qty;
            }
        }

        [Serializable]
        public class PackageRemovePayload
        {
            public int package_id;

            public PackageRemovePayload(int packageId)
            {
                package_id = packageId;
            }
        }

        [Serializable]
        public class PackageVariable
        {
            public int id = -1;
            public string identifier = string.Empty;
            public string description = string.Empty;
            public int min_length;
            public int max_length;
            public string type = string.Empty;
            public bool allow_colors;
            public List<PackageVariableOption> options = new List<PackageVariableOption>();
        }

        [Serializable]
        public class PackageVariableOption
        {
            public int id = -1;
            public string name = string.Empty;
            public string value = string.Empty;
            public double price;
            public double percentage;
        }

    #nullable enable
        [Serializable]
        public class Tier
        {
            public int id;
            public string created_at = string.Empty;
            public long username_id = -1L;
            public Package package = new Package();
            public bool active = false;
            public string recurring_payment_reference = string.Empty;
            public string next_payment_date = string.Empty;
            public TierStatus status = new TierStatus();
            public Package? pending_downgrade_package;
        }

        [Serializable]
        public class TierStatus
        {
            public int id = -1;
            public string status = string.Empty;
        }

        [Serializable]
        public class TierUpgradeResponse
        {
            public bool success = false;
            public string message = string.Empty;
        }

        [Serializable]
        public class UpdateTierPayload
        {
            public int package_id;

            public UpdateTierPayload(int packageId)
            {
                package_id = packageId;
            }
        }

        [Serializable]
        public class Webstore
        {
            public uint id;
            public string description = string.Empty;
            public string name = string.Empty;
            public string webstore_url = string.Empty;
            public string currency = string.Empty;
            public string lang = string.Empty;
            public string logo = string.Empty;
            public string platform_type = string.Empty;
            public string platform_type_id = string.Empty;
            public DateTime created_at = DateTime.MinValue;
        }

        [Serializable]
        public class WrappedBasket
        {
            public Basket data = new Basket();
        }

        [Serializable]
        public class WrappedBasketLinks
        {
            public List<BasketAuthLink> data = new List<BasketAuthLink>();
        }

        [Serializable]
        public class WrappedBasketPackages
        {
            public List<BasketPackage> data = new List<BasketPackage>();
        }

        [Serializable]
        public class WrappedCategories
        {
            public List<Category> data = new List<Category>();
        }

        [Serializable]
        public class WrappedCategory
        {
            public Category data = new Category();
        }

        [Serializable]
        public class WrappedPackage
        {
            public Package data = new Package();
        }

        [Serializable]
        public class WrappedPackages
        {
            public List<Package> data = new List<Package>();
        }
}

namespace Tebex.Plugin
{
        [Serializable]
        public class Account
        {
            public int id;
            public string domain = string.Empty;
            public string name = string.Empty;
            public Currency currency = new Currency();
            public bool online_mode;
            public string game_type = string.Empty;
            public bool log_events;
            public string public_token;
        }

        [Serializable]
        public class ActivePackage
        {
            public string txn_id = string.Empty;
            public DateTime date;
            public int quantity;
            public PackageInfo package = new PackageInfo();
        }

    #nullable enable
        [Serializable]
        public class Ban
        {
            public int id;
            public DateTime time;
            public string ip = string.Empty;
            public string? payment_email;
            public string reason = string.Empty;
            public BanUserInfo user = new BanUserInfo();
        }

        [Serializable]
        public class BanUserInfo
        {
            public string ign = string.Empty;
            public string uuid = string.Empty;
        }

    #nullable enable
        [Serializable]
        public class Category
        {
            public int id;
            public int order;
            public string name = string.Empty;
            public bool only_subcategories;
            public List<Category> subcategories = new List<Category>();
            public List<Package> packages = new List<Package>();
            public object? gui_item;
        }

        [Serializable]
        public class CheckoutResponse
        {
            public string url = string.Empty;
            public string expires = string.Empty;
            public string ident = string.Empty;
        }

        [Serializable]
        public class Command
        {
            public int id;
            [JsonProperty("command")]
            public string command_to_run = string.Empty;
            public long payment;
            [JsonProperty("package")]
            public long package_ref;
            public CommandConditions conditions = new CommandConditions();
            public PlayerInfo player = new PlayerInfo();
        }

        [Serializable]
        public class CommandConditions
        {
            public int delay;
            public int slots;
        }

        [Serializable]
        public class CommandQueueMeta
        {
            public bool execute_offline;
            public int next_check;
            public bool more;
        }

        [Serializable]
        public class CommandQueueResponse
        {
            public CommandQueueMeta meta = new CommandQueueMeta();
            public List<DuePlayer> players = new List<DuePlayer>();
        }

    #nullable enable
        [Serializable]
        public class CommunityGoal
        {
            public int id;
            public DateTime created_at;
            public DateTime updated_at;
            public int account;
            public string name = string.Empty;
            public string description = string.Empty;
            public string image = string.Empty;
            public double target;
            public double current;
            public int repeatable;
            public DateTime? last_achieved;
            public int times_achieved;
            public string status = string.Empty;
            public int sale;
        }

        [Serializable]
        public class Coupon
        {
            public int id;
            public string code = string.Empty;
            public PromotionEffectiveListings effective = new PromotionEffectiveListings();
            public PromotionDiscount discount = new PromotionDiscount();
            public CouponExpiry expire = new CouponExpiry();
            public string basket_type = string.Empty;
            public DateTime start_date;
            public int user_limit;
            public int minimum;
            public string username = string.Empty;
            public string note = string.Empty;
        }

    #nullable enable
        [Serializable]
        public class CouponExpiry
        {
            public bool redeem_unlimited;
            public bool expire_never;
            public int limit;
            public DateTime? date;
        }

        [Serializable]
        public class CouponPage
        {
            public CouponPagination pagination = new CouponPagination();
            public List<Coupon> data = new List<Coupon>();
        }

    #nullable enable
        [Serializable]
        public class CouponPagination
        {
            public int total_results;
            public int current_page;
            public int last_page;
            public string? previous;
            public string? next;
        }

        [Serializable]
        public class CreateBanPayload
        {
            public string reason = string.Empty;
            public string ip = string.Empty;
            public string user = string.Empty;
        }

        [Serializable]
        public class CreateCheckoutPayload
        {
            public int package_id;
            public string username = string.Empty;
        }

        [Serializable]
        public class CreateGiftCardPayload
        {
            public DateTime expires_at;
            public string note = string.Empty;
            public double amount;
        }

        [Serializable]
        public class Currency
        {
            public string iso4217 = string.Empty;
            public string symbol = string.Empty;
        }

        [Serializable]
        public class CustomerPackagePurchaseRecord
        {
            public string transaction_id = string.Empty;
            public DateTime date;
            public int quantity;
            public PurchaseRecordPackageInfo purchase_record_package = new PurchaseRecordPackageInfo();
        }

        [Serializable]
        public class DeleteCommandsPayload
        {
            public int[] ids = Array.Empty<int>();
        }

        [Serializable]
        public class DuePlayer
        {
            public int id;
            public string name = string.Empty;
            public string uuid = string.Empty;
        }

        [Serializable]
        public class GatewayInfo
        {
            public int id;
            public string name = string.Empty;
        }

    #nullable enable
        [Serializable]
        public class GiftCard
        {
            public int id;
            public string code = string.Empty;
            public GiftCardBalance balance = new GiftCardBalance();
            public string note = string.Empty;
            public bool voided;
            public DateTime created_at;
            public DateTime? expires_at;
        }

        [Serializable]
        public class GiftCardBalance
        {
            public double starting = -1.0d;
            public double remaining = -1.0d;
            public string currency = string.Empty;
        }

        [Serializable]
        public class JoinEvent
        {
            public string username_id;
            public string event_type;
            public DateTime event_date;
            public string ip;

            public JoinEvent(string usernameId, string eventType, string ipAddress)
            {
                username_id = usernameId;
                event_type = eventType;
                event_date = DateTime.UtcNow;
                ip = ipAddress;
            }
        }

        [Serializable]
        public class ListingsResponse
        {
            public List<Category> categories = new List<Category>();
        }

        [Serializable]
        public class NoteInfo
        {
            public string created_at = string.Empty;
            public string note = string.Empty;
        }

        [Serializable]
        public class OfflineCommandsMeta
        {
            public bool limited;
        }

        [Serializable]
        public class OfflineCommandsResponse
        {
            public OfflineCommandsMeta meta = new OfflineCommandsMeta();
            public List<Command> commands = new List<Command>();
        }

        [Serializable]
        public class OnlineCommandPlayerMeta
        {
            public string avatar = string.Empty;
            public string avatar_full = string.Empty;
            public string steam_id = string.Empty;
        }

        [Serializable]
        public class OnlineCommandsPlayer
        {
            public string id = string.Empty;
            public string username = string.Empty;
            public OnlineCommandPlayerMeta meta = new OnlineCommandPlayerMeta();
        }

        [Serializable]
        public class OnlineCommandsResponse
        {
            public OnlineCommandsPlayer player = new OnlineCommandsPlayer();
            public List<Command> commands = new List<Command>();
        }

    #nullable enable
        [Serializable]
        public class Package
        {
            public int id;
            public string name = string.Empty;
            public int order = -1;
            public string image = string.Empty;
            public double price = -1.0d;
            public PackageSaleData? sale;
            public int expiry_length;
            public string expiry_period = string.Empty;
            public string type = string.Empty;
            public Category category = new Category();
            public int global_limit;
            public string global_limit_period = string.Empty;
            public int user_limit;
            public string user_limit_period = string.Empty;
            public List<Server>? servers;
            public List<object>? required_packages;
            public bool require_any;
            public bool create_giftcard;
            public string show_until = string.Empty;
            public string gui_item = string.Empty;
            public bool disabled;
            public bool disable_quantity;
            public bool custom_price;
            public bool choose_server;
            public bool limit_expires;
            public bool inherit_commands;
            public bool variable_giftcard;
            public string description = string.Empty;

            public string GetFriendlyPayFrequency()
            {
                switch (type)
                {
                    case "single": return "One-Time";
                    case "subscription": return $"Each {expiry_length} {expiry_period}";
                    default: return "???";
                }
            }
        }

        [Serializable]
        public class PackageInfo
        {
            public int id;
            public string name = string.Empty;
        }

        [Serializable]
        public class PackageSaleData
        {
            public bool active = false;
            public double discount = -1.0d;
        }

    #nullable enable
        [Serializable]
        public class PaginatedPaymentInfo
        {
            public int total = -1;
            public int per_page = -1;
            public int current_page = -1;
            public int last_page = -1;
            public string? next_page_url = string.Empty;
            public string? previous_page_url = string.Empty;
            public int from = -1;
            public int to = -1;
            public List<PaymentDetails> data = new List<PaymentDetails>();
        }

        [Serializable]
        public class PaidPlayer
        {
            public int id;
            public string name = string.Empty;
            public string uuid = string.Empty;
        }

    #nullable enable
        [Serializable]
        public class PaymentDetails
        {
            public int id;
            public double amount;
            public DateTime date;
            public Currency currency = new Currency();
            public GatewayInfo gateway = new GatewayInfo();
            public string status = string.Empty;
            public string email = string.Empty;
            public PaidPlayer player = new PaidPlayer();
            public List<PackageInfo> packages = new List<PackageInfo>();
            public List<NoteInfo> notes = new List<NoteInfo>();
            public string? creator_code;
        }

        [Serializable]
        public class PlayerInfo
        {
            [JsonIgnore]
            public string id = string.Empty;
            [JsonProperty("name")]
            public string username = string.Empty;
            public OnlineCommandPlayerMeta meta = new OnlineCommandPlayerMeta();
            public string uuid = string.Empty;
            [JsonProperty("id")]
            public int plugin_username_id;
        }

        [Serializable]
        public class PlayerPaymentInfo
        {
            public string transaction_id = string.Empty;
            public long time;
            public double price;
            public string currency = string.Empty;
            public string status = string.Empty;
        }

        [Serializable]
        public class PromotionDiscount
        {
            public string type = string.Empty;
            public double percentage;
            public double value;
        }

        [Serializable]
        public class PromotionEffectiveListings
        {
            public string type = string.Empty;
            public List<int> packages = new List<int>();
            public List<int> categories = new List<int>();
        }

        [Serializable]
        public class PurchaseRecordPackageInfo
        {
            public int id;
            public string name = string.Empty;
        }

        [Serializable]
        public class Sale
        {
            public int id;
            public string name = string.Empty;
            public PromotionEffectiveListings effective = new PromotionEffectiveListings();
            public PromotionDiscount discount = new PromotionDiscount();
            public int start;
            public int expire;
            public int order;
        }

        [Serializable]
        public class Server
        {
            public int id;
            public string name = string.Empty;
        }

        [Serializable]
        public class Store
        {
            public Account account = new Account();
            public Server server = new Server();
        }

        [Serializable]
        public class TopUpGiftCardPayload
        {
            public string amount = string.Empty;
        }

        [Serializable]
        public class UserLookupResponse
        {
            public PlayerInfo player = new PlayerInfo();
            public int ban_count;
            public int chargeback_rate;
            public List<PlayerPaymentInfo> payments = new List<PlayerPaymentInfo>();
            public object[] purchase_totals = Array.Empty<object>();
        }

        [Serializable]
        public class WrappedBan
        {
            public Ban data = new Ban();
        }

        [Serializable]
        public class WrappedBans
        {
            public List<Ban> data = new List<Ban>();
        }

        [Serializable]
        public class WrappedCoupon
        {
            public Coupon data = new Coupon();
        }

        [Serializable]
        public class WrappedGiftCard
        {
            public GiftCard data = new GiftCard();
        }

        [Serializable]
        public class WrappedGiftCards
        {
            public List<GiftCard> data = new List<GiftCard>();
        }

        [Serializable]
        public class WrappedSales
        {
            public List<Sale> data = new List<Sale>();
        }
}

namespace Oxide.Plugins
{
    [Info("TebexPlugin", "Tebex", "3.0.0")]
    [Description("Official Tebex monetization plugin for Rust")]
    public class TebexPlugin : RustPlugin
    {
        #region Constants

        private const string PermAdmin = "tebexplugin.admin";

        private const string DataFilePlayerIds = "Tebex/player_ids";
        private const string DataFilePermissions = "Tebex/permission_ledger";

        private const int JoinFlushThreshold = 10;
        private const int TelemetryFlushThreshold = 10;
        private const int TelemetryBufferMax = 500;
        private const int DefaultNextCheckSeconds = 120;

        private const string UiName = "Tebex.BuyUi";
        private const string DebugUiName = "Tebex.DebugUi";
        private const string TelemetryUiName = "Tebex.TelemetryUi";
        private const string CommandRefUiName = "Tebex.CommandRefUi";
        private const string UiOverlay = "Overlay";

        #endregion

        #region Fields

        private TebexConfig _config;
        private Tebex.Plugin.Store _serverInfo;
        private string _publicToken;

        private bool _secretValid;
        private int _nextCheckSeconds = DefaultNextCheckSeconds;

        private readonly Dictionary<string, JoinRecord> _joinTimes = new();

        private readonly Dictionary<string, PlayerLookupRecord> _playerLookup = new();
        private readonly List<Tebex.Plugin.JoinEvent> _pendingJoinEvents = new();

        private readonly Dictionary<string, PermissionLedgerRecord> _permissionLedger = new();

        private readonly HashSet<int> _inflightCommandIds = new();
        private readonly List<DelayedCommand> _delayedCommands = new();

        private List<Tebex.Headless.Category> _categories = new();
        private List<Tebex.Plugin.CommunityGoal> _communityGoals = new();
        private DateTime _lastListingRefresh = DateTime.MinValue;

        private readonly Dictionary<string, TelemetryEvent> _telemetryIndex = new();

        private readonly Dictionary<ulong, BuyUiState> _uiStates = new();

        private Timer _queueTimer;
        private Timer _joinFlushTimer;
        private Timer _telemetryTimer;
        private Timer _listingTimer;
        private int _timerEpoch;

        private DateTime _lastQueueTickAt;
        private DateTime _lastJoinFlushTickAt;
        private DateTime _lastTelemetryTickAt;
        private DateTime _lastListingTickAt;
        private DateTime _nextQueueTickAt;
        private DateTime _nextJoinFlushTickAt;
        private DateTime _nextTelemetryTickAt;
        private DateTime _nextListingTickAt;

        private const double HookSlowThresholdMs = 100.0;
        private const double UiHookSlowThresholdMs = 400.0;
        private readonly Dictionary<string, HookStats> _hookStats = new();

        private readonly Dictionary<ulong, Timer> _debugUiRefresh = new();

        private enum DebugView { Main, Telemetry }
        private readonly Dictionary<ulong, DebugView> _debugView = new();

        private readonly Dictionary<ulong, string> _debugActionFeedback = new();

        private readonly Dictionary<ulong, string> _commandRefFeedback = new();

        private bool _warnedUndecodableImages;

        private const int TelemetrySentHistoryMax = 50;
        private readonly Queue<PluginEvent> _telemetrySentHistory = new();

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.DateTimeOffset
        };

        #endregion

        #region Configuration

        public class TebexConfig
        {
            [JsonProperty("secret_key")]
            public string SecretKey { get; set; } = "";

            [JsonProperty("buy_command")]
            public string BuyCommand { get; set; } = "buy";

            [JsonProperty("disable_ui")]
            public bool DisableUi { get; set; } = false;

            [JsonProperty("admin_command")]
            public string AdminCommand { get; set; } = "tebex";

            [JsonProperty("debug")]
            public bool Debug { get; set; } = false;

            [JsonProperty("auto_report_logs")]
            public bool AutoReportLogs { get; set; } = true;

            [JsonProperty("enable_basket")]
            public bool EnableBasket { get; set; } = true;

            [JsonProperty("track_permissions")]
            public bool TrackPermissions { get; set; } = true;

            [JsonProperty("custom_permission_commands")]
            public List<CustomPermissionCommand> CustomPermissionCommands { get; set; } = new();

            [JsonProperty("queue_check_seconds")]
            public int QueueCheckSeconds { get; set; } = DefaultNextCheckSeconds;

            [JsonProperty("listing_refresh_seconds")]
            public int ListingRefreshSeconds { get; set; } = 120;

            [JsonProperty("telemetry_flush_seconds")]
            public int TelemetryFlushSeconds { get; set; } = 120;

            [JsonProperty("join_flush_seconds")]
            public int JoinFlushSeconds { get; set; } = 60;
        }

        public class CustomPermissionCommand
        {
            [JsonProperty("label")]
            public string Label { get; set; } = "";

            [JsonProperty("grant_command")]
            public string GrantCommand { get; set; } = "";

            [JsonProperty("revoke_command")]
            public string RevokeCommand { get; set; } = "";
        }

        protected override void LoadDefaultConfig()
        {
            _config = new TebexConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<TebexConfig>() ?? new TebexConfig();
            }
            catch (Exception e)
            {
                PrintWarning($"Failed to read config ({e.Message}); using defaults.");
                _config = new TebexConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        private string ResolveSecret()
        {
            var env = Environment.GetEnvironmentVariable("TEBEX_SECRET_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return _config?.SecretKey?.Trim() ?? "";
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);

            AddCovalenceCommand(_config.AdminCommand, nameof(CmdTebex));
            AddCovalenceCommand(_config.BuyCommand, nameof(CmdBuy));

            LoadPlayerLookup();
            LoadPermissionLedger();

            lang.RegisterMessages(LangDefaults, this);
        }

        private void OnServerInitialized()
        {
            LoadEmbeddedImages();

            var key = ResolveSecret();
            if (string.IsNullOrEmpty(key))
            {
                PrintWarning("No Tebex secret key set. Run `tebex secret <key>` from console or set TEBEX_SECRET_KEY.");
                return;
            }

            VerifySecret(key, (ok, info, err) =>
            {
                if (!ok)
                {
                    PrintWarning($"Tebex secret key invalid: {err}");
                    _secretValid = false;
                    return;
                }
                _secretValid = true;
                _serverInfo = info;
                Puts($"Authenticated with {info.server?.name ?? "Tebex"} (account: {info.account?.name}).");
                StartTimers();
                RefreshListing();
                FetchCommunityGoals();
            });
        }

        private void Unload()
        {
            try
            {
                FlushAllUi();
                FlushTelemetry(force: true);
                FlushJoinEvents(force: true);
                SavePlayerLookup();
                SavePermissionLedger();
            }
            catch (Exception e)
            {
                PrintWarning($"Error during unload: {e.Message}");
            }
            finally
            {
                _timerEpoch++;
                _queueTimer?.Destroy();
                _joinFlushTimer?.Destroy();
                _telemetryTimer?.Destroy();
                _listingTimer?.Destroy();
            }
        }

        private void StartTimers()
        {
            var epoch = ++_timerEpoch;
            _queueTimer?.Destroy();
            _joinFlushTimer?.Destroy();
            _telemetryTimer?.Destroy();
            _listingTimer?.Destroy();

            ScheduleQueueTick(epoch);
            ScheduleJoinFlushTick(epoch);
            ScheduleTelemetryTick(epoch);
            ScheduleListingTick(epoch);
        }

        private void ScheduleQueueTick(int epoch)
        {
            if (epoch != _timerEpoch) return;
            var interval = Math.Max(_nextCheckSeconds, 30);
            _nextQueueTickAt = DateTime.UtcNow.AddSeconds(interval);
            _queueTimer = timer.In(interval, () =>
            {
                if (epoch != _timerEpoch) return;
                _lastQueueTickAt = DateTime.UtcNow;
                try { CheckQueue(); }
                catch (Exception e) { PrintWarning($"CheckQueue tick failed: {e.Message}"); }
                ScheduleQueueTick(epoch);
            });
        }

        private void ScheduleJoinFlushTick(int epoch)
        {
            if (epoch != _timerEpoch) return;
            var interval = Math.Max(_config.JoinFlushSeconds, 15);
            _nextJoinFlushTickAt = DateTime.UtcNow.AddSeconds(interval);
            _joinFlushTimer = timer.In(interval, () =>
            {
                if (epoch != _timerEpoch) return;
                _lastJoinFlushTickAt = DateTime.UtcNow;
                try { FlushJoinEvents(false); }
                catch (Exception e) { PrintWarning($"FlushJoinEvents tick failed: {e.Message}"); }
                ScheduleJoinFlushTick(epoch);
            });
        }

        private void ScheduleTelemetryTick(int epoch)
        {
            if (epoch != _timerEpoch) return;
            var interval = Math.Max(_config.TelemetryFlushSeconds, 30);
            _nextTelemetryTickAt = DateTime.UtcNow.AddSeconds(interval);
            _telemetryTimer = timer.In(interval, () =>
            {
                if (epoch != _timerEpoch) return;
                _lastTelemetryTickAt = DateTime.UtcNow;
                try { FlushTelemetry(false); }
                catch (Exception e) { PrintWarning($"FlushTelemetry tick failed: {e.Message}"); }
                ScheduleTelemetryTick(epoch);
            });
        }

        private void ScheduleListingTick(int epoch)
        {
            if (epoch != _timerEpoch) return;
            var interval = Math.Max(_config.ListingRefreshSeconds, 30);
            _nextListingTickAt = DateTime.UtcNow.AddSeconds(interval);
            _listingTimer = timer.In(interval, () =>
            {
                if (epoch != _timerEpoch) return;
                _lastListingTickAt = DateTime.UtcNow;
                try { RefreshListing(); FetchCommunityGoals(); }
                catch (Exception e) { PrintWarning($"RefreshListing tick failed: {e.Message}"); }
                ScheduleListingTick(epoch);
            });
        }

        private static double SlowThresholdFor(string name)
            => name == "tebex.ui" || name == "tebex.dbg" || name == "CmdBuy" || name == "CmdTebex"
                ? UiHookSlowThresholdMs
                : HookSlowThresholdMs;

        private void MeasureHook(string name, Action body)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                body();
            }
            finally
            {
                sw.Stop();
                var ms = sw.Elapsed.TotalMilliseconds;
                if (!_hookStats.TryGetValue(name, out var stats))
                {
                    stats = new HookStats();
                    _hookStats[name] = stats;
                }
                stats.Count++;
                stats.TotalMs += ms;
                if (ms > stats.MaxMs) stats.MaxMs = ms;
                var threshold = SlowThresholdFor(name);
                if (ms > threshold)
                {
                    stats.LastSlowAt = DateTime.UtcNow;
                    ReportTelemetry("WARN", $"hook '{name}' exceeded {threshold:0}ms", null,
                        new Dictionary<string, object> { ["last_ms"] = Math.Round(ms), ["max_ms"] = Math.Round(stats.MaxMs) });
                }
            }
        }

        #endregion

        #region Hooks

        private void OnPlayerConnected(BasePlayer player) => MeasureHook("OnPlayerConnected", () =>
        {
            if (player == null || !player.IsConnected) return;
            var id = player.UserIDString;
            var rec = new JoinRecord { Id = id, Name = player.displayName, JoinedAt = DateTime.UtcNow };
            _joinTimes[id] = rec;
            _pendingJoinEvents.Add(new Tebex.Plugin.JoinEvent(id, "server.join", GetAnonymizedPlayerIp(player)));

            if (_secretValid)
            {
                if (_playerLookup.TryGetValue(id, out var lookup) && lookup.PluginUsernameId > 0)
                {
                    CheckOnlineCommandsForPlayer(lookup.PluginUsernameId);
                }
                if (_pendingJoinEvents.Count >= JoinFlushThreshold) FlushJoinEvents(false);
            }
        });

        private void OnPlayerDisconnected(BasePlayer player, string reason) => MeasureHook("OnPlayerDisconnected", () =>
        {
            if (player == null) return;
            var id = player.UserIDString;
            _pendingJoinEvents.Add(new Tebex.Plugin.JoinEvent(id, "server.leave", GetAnonymizedPlayerIp(player)));

            _joinTimes.Remove(id);

            if (_pendingJoinEvents.Count >= JoinFlushThreshold) FlushJoinEvents(false);
            DestroyUi(player);
            DestroyDebugUi(player);
        });

        #endregion

        #region Command Dispatch

        private void CmdTebex(IPlayer player, string command, string[] args) => MeasureHook("CmdTebex", () =>
        {
            if (args == null || args.Length == 0)
            {
                if (!IsAdminPlayer(player))
                {
                    if (player.IsServer) { player.Reply(Lang("StoreUnavailable", player.Id)); return; }
                    if (!_secretValid) { player.Reply(Lang("StoreUnavailable", player.Id)); return; }
                    if (player.Object is BasePlayer bp) OpenStoreForPlayer(player, bp);
                    return;
                }
                if (player.IsServer || _config.DisableUi) player.Reply(Lang("HelpHeader", player.Id));
                HandleHelp(player);
                return;
            }

            var sub = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            switch (sub)
            {
                case "secret": HandleSecret(player, rest); break;
                case "info": HandleInfo(player); break;
                case "forcecheck": HandleForceCheck(player, rest); break;
                case "checkout": HandleCheckout(player, rest); break;
                case "sendlink": HandleSendLink(player, rest); break;
                case "debug": HandleDebug(player, rest); break;
                case "ban": HandleBan(player, rest); break;
                case "goals": HandleGoals(player); break;
                case "lookup": HandleLookup(player, rest); break;
                case "replay": HandleReplay(player, rest); break;
                case "selftest": HandleSelfTest(player); break;
                case "reload": HandleReload(player); break;
                case "help": HandleHelp(player); break;
                default:
                    player.Reply(IsOperator(player)
                        ? $"Unknown subcommand '{sub}'. Try `{_config.AdminCommand} help`."
                        : $"Unknown subcommand '{sub}'. Use /{_config.BuyCommand} to open the store.");
                    break;
            }
        });

        private void CmdBuy(IPlayer player, string command, string[] args) => MeasureHook("CmdBuy", () =>
        {
            if (player.IsServer)
            {
                player.Reply("The /buy command must be run as a player.");
                return;
            }
            if (!_secretValid)
            {
                player.Reply(Lang("StoreUnavailable", player.Id));
                return;
            }
            var basePlayer = player.Object as BasePlayer;
            if (basePlayer == null) return;

            OpenStoreForPlayer(player, basePlayer);
        });

        private void OpenStoreForPlayer(IPlayer player, BasePlayer basePlayer)
        {
            var uiEnabled = !_config.DisableUi;
            ReportTelemetry("INFO", $"store opened, ui {(uiEnabled ? "enabled" : "disabled")}", null);

            if (!uiEnabled)
            {
                var webUrl = ResolveWebstoreUrl();
                basePlayer.ConsoleMessage(webUrl);
                basePlayer.ChatMessage("Our webstore link has been sent to your F1 console. Click the timestamp to copy the link.");
                return;
            }
            OpenBuyUi(basePlayer);
        }

        private bool IsAdminPlayer(IPlayer player)
            => player.IsServer || player.IsAdmin
               || (!string.IsNullOrEmpty(player.Id) && permission.UserHasPermission(player.Id, PermAdmin));

        private bool IsOperator(IPlayer player)
            => IsAdminPlayer(player)
               || (player.Object is BasePlayer bp && bp.net?.connection != null && bp.net.connection.authLevel >= 1);

        private bool RequireAdmin(IPlayer player)
        {
            if (IsAdminPlayer(player)) return true;
            player.Reply(Lang("NoPermission", player.Id));
            return false;
        }

        private bool RequireSecret(IPlayer player)
        {
            if (_secretValid) return true;
            player.Reply(Lang("SecretMissing", player.Id));
            return false;
        }

        #endregion

        #region Subcommand Handlers

        private void HandleSecret(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (args.Length < 1)
            {
                player.Reply($"Usage: {_config.AdminCommand} secret <key>");
                return;
            }
            var key = args[0].Trim();
            VerifySecret(key, (ok, info, err) =>
            {
                if (!ok)
                {
                    player.Reply($"Secret key rejected: {err}");
                    return;
                }
                _config.SecretKey = key;
                SaveConfig();
                _secretValid = true;
                _serverInfo = info;
                player.Reply($"Secret key saved. Linked to '{info.server?.name}' on '{info.account?.domain}'.");
                StartTimers();
                RefreshListing();
                FetchCommunityGoals();
            });
        }

        private void HandleInfo(IPlayer player)
        {
            if (!RequireAdmin(player)) return;
            if (!RequireSecret(player)) return;
            ApiGet<Tebex.Plugin.Store>("/information", (ok, info, err) =>
            {
                if (!ok) { player.Reply($"Failed to fetch info: {err}"); return; }
                var sb = new StringBuilder();
                sb.AppendLine($"Server : {info.server?.name}");
                sb.AppendLine($"Account: {info.account?.name} ({info.account?.domain})");
                player.Reply(sb.ToString().TrimEnd());
            });
        }

        private void HandleForceCheck(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (!RequireSecret(player)) return;
            if (args != null && args.Length > 0)
            {
                var name = args[0];
                var rec = FindPlayerByUsername(name);
                if (rec == null)
                {
                    player.Reply($"No cached Tebex id for player '{name}'. Run a normal `forcecheck` first or wait for the next /queue tick.");
                    return;
                }
                player.Reply($"Checking online commands for {rec.Username} (Tebex id {rec.PluginUsernameId})...");
                CheckOnlineCommandsForPlayer(rec.PluginUsernameId, n => player.Reply($"Executed {n} command(s) for {rec.Username}."));
                return;
            }
            player.Reply("Forcing queue check...");
            CheckQueue(n => player.Reply($"Queue check complete. Executed {n} command(s)."));
        }

        private void HandleCheckout(IPlayer player, string[] args)
        {
            if (!RequireSecret(player)) return;
            if (args.Length < 1)
            {
                player.Reply($"Usage: {_config.AdminCommand} checkout <packageId>");
                return;
            }
            if (!int.TryParse(args[0], out var packageId))
            {
                player.Reply("Package id must be a number.");
                return;
            }
            if (player.IsServer)
            {
                player.Reply("Checkout requires a player context. Use sendlink instead.");
                return;
            }
            CreateCheckout(packageId, player.Name, (ok, url, err) =>
            {
                if (!ok) { player.Reply($"Checkout failed: {err}"); return; }
                SendCheckoutLink(player, url, packageId);
            });
        }

        private void HandleSendLink(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (!RequireSecret(player)) return;
            if (args.Length < 2)
            {
                player.Reply($"Usage: {_config.AdminCommand} sendlink <packageId> <username>");
                return;
            }
            if (!int.TryParse(args[0], out var packageId))
            {
                player.Reply("Package id must be a number.");
                return;
            }
            var target = covalence.Players.FindPlayer(args[1]);
            if (target == null)
            {
                player.Reply($"Player '{args[1]}' not found.");
                return;
            }
            CreateCheckout(packageId, target.Name, (ok, url, err) =>
            {
                if (!ok) { player.Reply($"Checkout failed: {err}"); return; }
                if (target.IsConnected) SendCheckoutLink(target, url, packageId);
                player.Reply($"Sent checkout link for package {packageId} to {target.Name}: {url}");
            });
        }

        private void HandleDebug(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (args.Length < 1)
            {
                if (!player.IsServer && player.Object is BasePlayer basePlayer)
                {
                    OpenDebugUi(basePlayer);
                    return;
                }
                player.Reply($"Debug logging is currently {(_config.Debug ? "ON" : "OFF")}. Usage: {_config.AdminCommand} debug [true|false]");
                return;
            }
            if (!bool.TryParse(args[0], out var enabled))
            {
                player.Reply("Argument must be true or false.");
                return;
            }
            _config.Debug = enabled;
            SaveConfig();
            player.Reply($"Debug logging is now {(enabled ? "ON" : "OFF")}.");
        }

        private void HandleBan(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (!RequireSecret(player)) return;
            if (args.Length < 2)
            {
                player.Reply($"Usage: {_config.AdminCommand} ban <name> <reason> [ip]");
                return;
            }
            var name = args[0];
            var ip = args.Length >= 3 ? args[args.Length - 1] : "";
            var reasonParts = ip == "" ? args.Skip(1) : args.Skip(1).Take(args.Length - 2);
            var reason = string.Join(" ", reasonParts);

            var body = JsonConvert.SerializeObject(new { user = name, reason, ip }, JsonSettings);
            ApiRequest("/bans", RequestMethod.POST, body, (code, response) =>
            {
                if (code == 201 || code == 200) player.Reply($"Banned '{name}' from the webstore.");
                else player.Reply($"Ban failed (HTTP {code}): {response}");
            });
        }

        private void HandleGoals(IPlayer player)
        {
            if (!RequireAdmin(player)) return;
            if (!RequireSecret(player)) return;
            ApiGet<List<Tebex.Plugin.CommunityGoal>>("/community_goals", (ok, list, err) =>
            {
                if (!ok) { player.Reply($"Failed to fetch community goals: {err}"); return; }
                var goals = list ?? new List<Tebex.Plugin.CommunityGoal>();
                _communityGoals = goals;
                if (goals.Count == 0) { player.Reply("No community goals configured."); return; }
                var sb = new StringBuilder();
                sb.AppendLine("Community goals:");
                foreach (var g in goals)
                    sb.AppendLine($" {g.name}: {g.current:0}/{g.target:0} ({g.status})");
                player.Reply(sb.ToString().TrimEnd());
            });
        }

        private void HandleLookup(IPlayer player, string[] args)
        {
            player.Reply("User lookup is not available in this build.");
        }

        private void HandleReplay(IPlayer player, string[] args)
        {
            if (!RequireAdmin(player)) return;
            if (!_config.TrackPermissions)
            {
                player.Reply("Permission tracking is disabled (set track_permissions to true in the config).");
                return;
            }
            if (args == null || args.Length < 1)
            {
                player.Reply($"Usage: {_config.AdminCommand} replay <user|steamid|all>");
                return;
            }

            if (args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                int players = 0, grants = 0;
                foreach (var rec in _permissionLedger.Values.ToList())
                {
                    var applied = ReplayLedger(rec);
                    if (applied > 0) { players++; grants += applied; }
                }
                player.Reply($"Replayed {grants} tracked permission(s)/group(s) across {players} player(s).");
                return;
            }

            var record = FindLedgerRecord(_permissionLedger, args[0]);
            if (record == null)
            {
                player.Reply($"No tracked permissions for '{args[0]}'. They may not have purchased a permission package, or tracking was off when they did.");
                return;
            }
            if (record.Entries.Count == 0)
            {
                player.Reply($"{record.Username} has no tracked permissions to replay.");
                return;
            }
            var count = ReplayLedger(record);
            player.Reply($"Replayed {count} tracked permission(s)/group(s) for {record.Username} ({record.Uuid}).");
        }

        private void HandleReload(IPlayer player)
        {
            if (!RequireAdmin(player)) return;
            LoadConfig();
            player.Reply("Configuration reloaded.");

            var key = ResolveSecret();
            if (string.IsNullOrEmpty(key))
            {
                _secretValid = false;
                player.Reply("No secret key set after reload.");
                return;
            }
            VerifySecret(key, (ok, info, err) =>
            {
                if (!ok)
                {
                    _secretValid = false;
                    player.Reply($"Secret invalid after reload: {err}");
                    return;
                }
                _secretValid = true;
                _serverInfo = info;
                StartTimers();
                RefreshListing();
                FetchCommunityGoals();
                player.Reply("Reauthenticated and refreshed.");
            });
        }

        private void HandleHelp(IPlayer player)
        {
            if (!IsOperator(player))
            {
                player.Reply($"Use /{_config.BuyCommand} to open the store.");
                return;
            }
            if (!player.IsServer && !_config.DisableUi && player.Object is BasePlayer bp)
            {
                OpenCommandReferenceUi(bp);
                return;
            }
            player.Reply(BuildCommandReferenceText());
        }

        private string BuildCommandReferenceText()
        {
            int width = 0;
            foreach (var row in CommandReferenceTable)
            {
                var len = FormatCommandSyntax(row.Syntax).Length;
                if (len > width) width = len;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Tebex commands:");
            string section = null;
            foreach (var row in CommandReferenceTable)
            {
                if (row.Section != section)
                {
                    section = row.Section;
                    sb.AppendLine();
                    sb.AppendLine(section);
                }
                sb.AppendLine($"  {FormatCommandSyntax(row.Syntax).PadRight(width)}  {row.Description}");
            }
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Models

        private class JoinRecord
        {
            public string Id;
            public string Name;
            public DateTime JoinedAt;
        }

        private class DelayedCommand
        {
            public Tebex.Plugin.Command Command;
            public BasePlayer Player;
            public DateTime FireAt;
        }

        private class TelemetryEvent
        {
            [JsonProperty("level")] public string Level;
            [JsonProperty("message")] public string Message;
            [JsonProperty("trace")] public string Trace;
            [JsonProperty("count")] public int Count;
            [JsonProperty("first_seen")] public DateTime FirstSeen;
            [JsonProperty("last_seen")] public DateTime LastSeen;
            [JsonProperty("metadata")] public Dictionary<string, object>? Metadata;
            [JsonIgnore] public string Signature => $"{Level}|{Message?.Substring(0, Math.Min(120, Message?.Length ?? 0))}|{Trace?.Substring(0, Math.Min(200, Trace?.Length ?? 0))}";
        }

        private enum BuyUiView { Store, Basket }

        private class BuyUiState
        {
            public int CategoryId;
            public int Page;
            public int? OpenPackageId;
            public BuyUiView View = BuyUiView.Store;
            public string BasketIdent = string.Empty;
            public Tebex.Headless.Basket Basket;
            public string LastBasketError = string.Empty;
            public readonly HashSet<int> InflightAdds = new HashSet<int>();
            public bool OverlayDrawn;
        }

        private class HookStats
        {
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public DateTime LastSlowAt;
            public double AvgMs => Count == 0 ? 0 : TotalMs / Count;
        }

        private class JoinDataFile
        {
            public Dictionary<string, JoinRecord> Joins = new();
        }

        private class TelemetryDataFile
        {
            public List<TelemetryEvent> Events = new();
        }

        private class PlayerLookupRecord
        {
            public int PluginUsernameId;
            public string Uuid = string.Empty;
            public string Username = string.Empty;
            public DateTime LastSeen;
        }

        private class PlayerLookupDataFile
        {
            public Dictionary<string, PlayerLookupRecord> Players = new();
        }

        private class PermissionLedgerEntry
        {
            public string Type = string.Empty;
            public string Value = string.Empty;
            public long PackageRef;
            public string RawCommand = string.Empty;
            public DateTime GrantedAt;
        }

        private class PermissionLedgerRecord
        {
            public string Uuid = string.Empty;
            public string Username = string.Empty;
            public List<PermissionLedgerEntry> Entries = new();
        }

        private class PermissionLedgerDataFile
        {
            public Dictionary<string, PermissionLedgerRecord> Players = new();
        }

        #endregion

        #region API Client

        private void DebugHttpRequest(RequestMethod method, string url, IDictionary<string, string> headers, string body)
        {
            if (!_config.Debug) return;
            var headerStr = headers == null
                ? "<none>"
                : string.Join(", ", headers.Select(h => string.Equals(h.Key, "X-Tebex-Secret", StringComparison.OrdinalIgnoreCase)
                    ? $"{h.Key}=<redacted>"
                    : $"{h.Key}={h.Value}"));
            Puts($"[Tebex HTTP] -> {method} {url}\n        headers: {headerStr}\n        body:    {Truncate(body, 1000) ?? "<none>"}");
        }

        private void DebugHttpResponse(RequestMethod method, string url, int code, string response, double elapsedMs = -1)
        {
            if (!_config.Debug) return;
            var timing = elapsedMs >= 0 ? $" ({elapsedMs:0}ms)" : "";
            Puts($"[Tebex HTTP] <- {method} {url} -> {code}{timing}\n        body: {Truncate(response, 1000) ?? "<empty>"}");
        }

        private void SendHttp(RequestMethod method, string url, string body, IDictionary<string, string> headers, float timeoutSeconds, Action<int, string> callback)
        {
            DebugHttpRequest(method, url, headers, body);
            var startedAt = UnityEngine.Time.realtimeSinceStartup;
            webrequest.Enqueue(url, body ?? "", (code, response) =>
            {
                var elapsedMs = (UnityEngine.Time.realtimeSinceStartup - startedAt) * 1000.0;
                DebugHttpResponse(method, url, code, response, elapsedMs);
                try
                {
                    callback?.Invoke(code, response);
                }
                catch (Exception e)
                {
                    if (url.Contains("https://plugin-logs.tebex.io"))
                    {
                        ReportTelemetry("ERROR", $"Callback for {method} {url} threw: {e.Message}", e.StackTrace);
                        PrintError($"Tebex callback error: {e.Message}");
                    }
                    else
                    {
                        PrintError($"Failed to send telemetry: {e.Message}");
                    }
                }
            }, this, method, headers as Dictionary<string, string>, timeoutSeconds);
        }

        private void ApiRequest(string path, RequestMethod method, string body, Action<int, string> callback, bool requireSecret = true)
        {
            var url = $"https://plugin.tebex.io{path}";
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json"
            };
            var key = ResolveSecret();
            if (requireSecret)
            {
                if (string.IsNullOrEmpty(key))
                {
                    callback?.Invoke(0, "no secret key configured");
                    return;
                }
                headers["X-Tebex-Secret"] = key;
            }
            else if (!string.IsNullOrEmpty(key))
            {
                headers["X-Tebex-Secret"] = key;
            }

            SendHttp(method, url, body, headers, 30f, callback);
        }

        private void HeadlessRequest(RequestMethod method, string apiPath, string body, Action<int, string> callback)
        {
            var url = $"https://headless.tebex.io/api/{apiPath}";
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json"
            };
            SendHttp(method, url, body, headers, 30f, callback);
        }

        private void HeadlessGet<T>(string accountScopedPath, Action<bool, T, string> callback) where T : class
        {
            if (string.IsNullOrEmpty(_publicToken))
            {
                callback?.Invoke(false, null, "Public token not set, use tebex secret to set key and retrieve token");
                return;
            }
            var path = $"accounts/{Uri.EscapeDataString(_publicToken)}/{accountScopedPath}";
            HeadlessRequest(RequestMethod.GET, path, null, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    callback?.Invoke(false, null, $"HTTP {code}: {Truncate(response, 200)}");
                    return;
                }
                try
                {
                    var parsed = JsonConvert.DeserializeObject<T>(response, JsonSettings);
                    callback?.Invoke(true, parsed, null);
                }
                catch (Exception e)
                {
                    callback?.Invoke(false, null, $"Parse error: {e.Message}");
                }
            });
        }

        private void ApiGet<T>(string path, Action<bool, T, string> callback, bool requireSecret = true) where T : class
        {
            ApiRequest(path, RequestMethod.GET, null, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    callback?.Invoke(false, null, $"HTTP {code}: {Truncate(response, 200)}");
                    return;
                }
                try
                {
                    var parsed = JsonConvert.DeserializeObject<T>(response, JsonSettings);
                    callback?.Invoke(true, parsed, null);
                }
                catch (Exception e)
                {
                    callback?.Invoke(false, null, $"Parse error: {e.Message}");
                }
            }, requireSecret);
        }

        private void VerifySecret(string key, Action<bool, Tebex.Plugin.Store, string> callback)
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json",
                ["X-Tebex-Secret"] = key
            };
            SendHttp(RequestMethod.GET, "https://plugin.tebex.io/information", null, headers, 15f, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    callback(false, null, $"HTTP {code}: {Truncate(response, 200)}");
                    return;
                }
                try
                {
                    var info = JsonConvert.DeserializeObject<Tebex.Plugin.Store>(response, JsonSettings);
                    _publicToken = info.account.public_token;
                    callback(true, info, null);
                }
                catch (Exception e)
                {
                    callback(false, null, $"Parse error: {e.Message}");
                }
            });
        }

        private void PluginCheckout(int packageId, string username, Action<bool, Tebex.Plugin.CheckoutResponse, string> callback)
        {
            var payload = new Tebex.Plugin.CreateCheckoutPayload { package_id = packageId, username = username };
            var body = JsonConvert.SerializeObject(payload, JsonSettings);
            ApiRequest("/checkout", RequestMethod.POST, body, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    callback?.Invoke(false, null, ExtractApiError(code, response));
                    return;
                }
                try
                {
                    var parsed = JsonConvert.DeserializeObject<Tebex.Plugin.CheckoutResponse>(response, JsonSettings);
                    if (parsed == null || string.IsNullOrEmpty(parsed.ident))
                    {
                        callback?.Invoke(false, null, "checkout response missing basket ident");
                        return;
                    }
                    callback?.Invoke(true, parsed, null);
                }
                catch (Exception e) { callback?.Invoke(false, null, $"checkout parse: {e.Message}"); }
            });
        }

        private void CreateCheckout(int packageId, string username, Action<bool, string, string> callback)
        {
            PluginCheckout(packageId, username, (ok, resp, err) =>
            {
                if (!ok) { callback?.Invoke(false, null, err); return; }
                callback?.Invoke(true, resp.url, null);
            });
        }

        private class ApiErrorBody
        {
            public string title = null;
            public string detail = null;
        }

        private string ExtractApiError(int code, string response)
        {
            if (!string.IsNullOrEmpty(response))
            {
                try
                {
                    var err = JsonConvert.DeserializeObject<ApiErrorBody>(response, JsonSettings);
                    if (err != null && !string.IsNullOrEmpty(err.title))
                    {
                        return !string.IsNullOrEmpty(err.detail) && err.detail != err.title
                            ? $"{err.title}: {err.detail}"
                            : err.title;
                    }
                }
                catch {   }
            }
            return $"HTTP {code}: {Truncate(response, 200)}";
        }

        private bool ApplyBasketResponse(BuyUiState state, string response, out string err)
        {
            try
            {
                var wrapped = JsonConvert.DeserializeObject<Tebex.Headless.WrappedBasket>(response, JsonSettings);
                if (wrapped?.data == null) { err = "basket response missing data"; return false; }
                state.Basket = wrapped.data;
                if (!string.IsNullOrEmpty(wrapped.data.ident)) state.BasketIdent = wrapped.data.ident;
                err = null;
                return true;
            }
            catch (Exception e) { err = $"basket parse: {e.Message}"; return false; }
        }

        private void RefreshBasket(BasePlayer player, Action<bool, string> cb)
        {
            if (player == null) { cb?.Invoke(false, "no player"); return; }
            if (!_uiStates.TryGetValue(player.userID, out var state)) { cb?.Invoke(false, "no ui state"); return; }
            if (string.IsNullOrEmpty(state.BasketIdent)) { cb?.Invoke(false, "no basket"); return; }
            if (string.IsNullOrEmpty(_publicToken)) { cb?.Invoke(false, "public_token not set"); return; }

            var path = $"accounts/{Uri.EscapeDataString(_publicToken)}/baskets/{Uri.EscapeDataString(state.BasketIdent)}";
            HeadlessRequest(RequestMethod.GET, path, null, (code, response) =>
            {
                if (code == 404)
                {
                    state.BasketIdent = string.Empty;
                    state.Basket = null;
                    cb?.Invoke(false, "basket not found");
                    return;
                }
                if (code < 200 || code >= 300)
                {
                    cb?.Invoke(false, ExtractApiError(code, response));
                    return;
                }
                cb?.Invoke(ApplyBasketResponse(state, response, out var err), err);
            });
        }

        private void EnsureBasket(BasePlayer player, int seedPackageId, Action<bool, string> cb)
        {
            if (player == null) { cb?.Invoke(false, "no player"); return; }
            if (!_uiStates.TryGetValue(player.userID, out var state))
            {
                state = new BuyUiState();
                _uiStates[player.userID] = state;
            }
            if (!string.IsNullOrEmpty(state.BasketIdent)) { cb?.Invoke(true, null); return; }

            PluginCheckout(seedPackageId, player.displayName, (ok, resp, err) =>
            {
                if (!ok) { cb?.Invoke(false, err); return; }
                state.BasketIdent = resp.ident;
                cb?.Invoke(true, null);
            });
        }

        private void AddToBasket(BasePlayer player, int packageId, Action<bool, string> cb)
        {
            if (player == null) { cb?.Invoke(false, "no player"); return; }
            if (!_uiStates.TryGetValue(player.userID, out var state))
            {
                state = new BuyUiState();
                _uiStates[player.userID] = state;
            }
            state.InflightAdds.Add(packageId);

            EnsureBasket(player, packageId, (ok, err) =>
            {
                if (!ok)
                {
                    state.InflightAdds.Remove(packageId);
                    cb?.Invoke(false, err);
                    return;
                }

                var body = JsonConvert.SerializeObject(new Tebex.Headless.AddPackagePayload(packageId), JsonSettings);
                var path = $"baskets/{Uri.EscapeDataString(state.BasketIdent)}/packages";
                HeadlessRequest(RequestMethod.POST, path, body, (code, response) =>
                {
                    state.InflightAdds.Remove(packageId);
                    if (code < 200 || code >= 300)
                    {
                        cb?.Invoke(false, ExtractApiError(code, response));
                        return;
                    }
                    var applied = ApplyBasketResponse(state, response, out var aerr);
                    cb?.Invoke(applied, aerr);
                });
            });
        }

        private void RemoveFromBasket(BasePlayer player, int packageId, Action<bool, string> cb)
        {
            if (player == null) { cb?.Invoke(false, "no player"); return; }
            if (!_uiStates.TryGetValue(player.userID, out var state) || string.IsNullOrEmpty(state.BasketIdent))
            {
                cb?.Invoke(false, "no basket");
                return;
            }
            var body = JsonConvert.SerializeObject(new Tebex.Headless.PackageRemovePayload(packageId), JsonSettings);
            var path = $"baskets/{Uri.EscapeDataString(state.BasketIdent)}/packages/remove";
            HeadlessRequest(RequestMethod.POST, path, body, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    cb?.Invoke(false, ExtractApiError(code, response));
                    return;
                }
                var applied = ApplyBasketResponse(state, response, out var aerr);
                cb?.Invoke(applied, aerr);
            });
        }

        #endregion

        #region Queue Processing

        private void CheckQueue() => CheckQueue(null);

        private void CheckQueue(Action<int> onComplete)
        {
            if (!_secretValid) { onComplete?.Invoke(0); return; }
            ApiGet<Tebex.Plugin.CommandQueueResponse>("/queue", (ok, q, err) =>
            {
                if (!ok)
                {
                    ReportTelemetry("WARN", $"queue fetch failed: {err}", null);
                    onComplete?.Invoke(0);
                    return;
                }
                if (q.meta != null)
                {
                    if (q.meta.next_check > 0 && q.meta.next_check != _nextCheckSeconds)
                    {
                        _nextCheckSeconds = q.meta.next_check;
                    }
                }

                if (q.players != null)
                {
                    foreach (var dp in q.players)
                    {
                        RememberPlayer(dp.id, dp.uuid, dp.name);
                    }
                }

                var pendingPlayers = (q.players ?? new List<Tebex.Plugin.DuePlayer>())
                    .Where(p => IsPlayerOnline(p.uuid))
                    .ToList();

                if (pendingPlayers.Count == 0 && (q.meta == null || !q.meta.execute_offline))
                {
                    onComplete?.Invoke(0);
                    return;
                }

                int remaining = pendingPlayers.Count + (q.meta != null && q.meta.execute_offline ? 1 : 0);
                int totalExecuted = 0;
                void Done(int n) { totalExecuted += n; remaining--; if (remaining <= 0) onComplete?.Invoke(totalExecuted); }

                foreach (var qp in pendingPlayers)
                {
                    CheckOnlineCommandsForPlayer(qp.id, Done);
                }
                if (q.meta != null && q.meta.execute_offline)
                {
                    CheckOfflineCommands(Done);
                }
            });
        }

        private void CheckOnlineCommandsForPlayer(int playerUsernameId, Action<int> onComplete = null)
        {
            ApiGet<Tebex.Plugin.OnlineCommandsResponse>($"/queue/online-commands/{playerUsernameId}", (ok, resp, err) =>
            {
                if (!ok)
                {
                    ReportTelemetry("WARN", $"online-commands fetch failed for {playerUsernameId}: {err}", null);
                    onComplete?.Invoke(0);
                    return;
                }
                if (resp?.player != null && resp.commands != null)
                {
                    foreach (var c in resp.commands)
                    {
                        c.player ??= new Tebex.Plugin.PlayerInfo();
                        if (string.IsNullOrEmpty(c.player.uuid)) c.player.uuid = resp.player.id;
                        if (string.IsNullOrEmpty(c.player.id)) c.player.id = resp.player.id;
                        if (string.IsNullOrEmpty(c.player.username)) c.player.username = resp.player.username;
                        if (c.player.plugin_username_id == 0) c.player.plugin_username_id = playerUsernameId;
                    }
                }
                var n = ProcessCommands(resp.commands, online: true);
                onComplete?.Invoke(n);
            });
        }

        private void CheckOfflineCommands(Action<int> onComplete = null)
        {
            ApiGet<Tebex.Plugin.OfflineCommandsResponse>("/queue/offline-commands", (ok, resp, err) =>
            {
                if (!ok)
                {
                    ReportTelemetry("WARN", $"offline-commands fetch failed: {err}", null);
                    onComplete?.Invoke(0);
                    return;
                }
                var n = ProcessCommands(resp.commands, online: false);
                onComplete?.Invoke(n);
            });
        }

        private int ProcessCommands(List<Tebex.Plugin.Command> commands, bool online)
        {
            if (commands == null || commands.Count == 0) return 0;
            var executedNow = new List<int>();
            int delayedCount = 0;
            foreach (var cmd in commands)
            {
                if (cmd.player != null)
                {
                    RememberPlayer(cmd.player.plugin_username_id, cmd.player.uuid, cmd.player.username);
                }

                if (_inflightCommandIds.Contains(cmd.id)) continue;
                _inflightCommandIds.Add(cmd.id);

                var basePlayer = online ? FindOnlinePlayer(cmd.player?.uuid) : null;
                if (online && basePlayer == null)
                {
                    _inflightCommandIds.Remove(cmd.id);
                    continue;
                }

                if (online && cmd.conditions?.slots > 0 && basePlayer != null)
                {
                    var slotsFree = CountFreeInventorySlots(basePlayer);
                    if (slotsFree < cmd.conditions.slots)
                    {
                        basePlayer.IPlayer?.Reply($"You need at least {cmd.conditions.slots} free inventory slots to receive a Tebex package. Free up space.");
                        _inflightCommandIds.Remove(cmd.id);
                        continue;
                    }
                }

                var delay = cmd.conditions?.delay ?? 0;
                if (delay > 0)
                {
                    _delayedCommands.Add(new DelayedCommand
                    {
                        Command = cmd,
                        Player = basePlayer,
                        FireAt = DateTime.UtcNow.AddSeconds(delay)
                    });
                    timer.In(delay, () => ExecuteAndAck(cmd, basePlayer));
                    delayedCount++;
                }
                else
                {
                    var line = SubstituteVars(cmd.command_to_run, basePlayer, cmd.player);
                    ExecuteServerCommand(line);
                    TrackPermissionCommand(line, cmd);
                    executedNow.Add(cmd.id);
                }
            }
            if (executedNow.Count > 0) AckCommands(executedNow);
            return executedNow.Count + delayedCount;
        }

        private void ExecuteAndAck(Tebex.Plugin.Command cmd, BasePlayer player)
        {
            try
            {
                var line = SubstituteVars(cmd.command_to_run, player, cmd.player);
                ExecuteServerCommand(line);
                TrackPermissionCommand(line, cmd);
                AckCommands(new List<int> { cmd.id });
            }
            catch (Exception e)
            {
                ReportTelemetry("ERROR", $"Failed to execute Tebex command {cmd.id}: {e.Message}", e.StackTrace);
                _inflightCommandIds.Remove(cmd.id);
            }
            finally
            {
                _delayedCommands.RemoveAll(d => d.Command.id == cmd.id);
            }
        }

        private void ExecuteServerCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;
            if (_config.Debug) Puts($"[Tebex] exec: {command}");
            try
            {
                Server.Command(command);
            }
            catch (Exception e)
            {
                ReportTelemetry("ERROR", $"command exec threw: {e.Message}", e.StackTrace);
                PrintError($"Failed running '{command}': {e.Message}");
            }
        }

        private string SubstituteVars(string template, BasePlayer onlinePlayer, Tebex.Plugin.PlayerInfo player)
        {
            if (string.IsNullOrEmpty(template)) return template;
            var name = onlinePlayer?.displayName ?? player?.username ?? "";
            var id = onlinePlayer?.UserIDString ?? player?.uuid ?? player?.id ?? "";
            return template
                .Replace("{username}", name)
                .Replace("{name}", name)
                .Replace("{id}", id)
                .Replace("{steamid}", id);
        }

        private void AckCommands(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return;
            var body = JsonConvert.SerializeObject(new { ids }, JsonSettings);
            ApiRequest("/queue", RequestMethod.DELETE, body, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    ReportTelemetry("WARN", $"ack failed for [{string.Join(",", ids)}]: HTTP {code} {Truncate(response, 200)}", null);
                    foreach (var id in ids) _inflightCommandIds.Remove(id);
                }
            });
        }

        private static int CountFreeInventorySlots(BasePlayer player)
        {
            if (player?.inventory == null) return 0;
            int free = 0;
            free += FreeSlots(player.inventory.containerMain);
            free += FreeSlots(player.inventory.containerBelt);
            return free;
        }

        private static int FreeSlots(ItemContainer container)
        {
            if (container == null) return 0;
            return container.capacity - container.itemList.Count;
        }

        #endregion

        #region Listing Refresh

        private void RefreshListing()
        {
            if (string.IsNullOrEmpty(_config.SecretKey))
            {
                if (_config.Debug) PrintWarning("Skipping listing refresh: secret_key is not set.");
                return;
            }
            HeadlessGet<Tebex.Headless.WrappedCategories>("categories?includePackages=1", (ok, resp, err) =>
            {
                if (!ok)
                {
                    ReportTelemetry("WARN", $"headless listing fetch failed: {err}", null);
                    return;
                }
                _categories = resp?.data ?? new List<Tebex.Headless.Category>();
                _lastListingRefresh = DateTime.UtcNow;
                WarnOnUndecodableImages();
                if (_config.Debug) Puts($"[Tebex] listing refreshed: {_categories.Count} categories.");
            });
        }

        private void FetchCommunityGoals()
        {
            if (!_secretValid) return;
            ApiGet<List<Tebex.Plugin.CommunityGoal>>("/community_goals", (ok, list, err) =>
            {
                if (!ok) { if (_config.Debug) PrintWarning($"[Tebex] community_goals fetch failed: {err}"); return; }
                _communityGoals = list ?? new List<Tebex.Plugin.CommunityGoal>();
            });
        }

        private void WarnOnUndecodableImages()
        {
            if (_warnedUndecodableImages) return;

            var affected = new List<string>();
            foreach (var p in AllPackages())
            {
                if (p == null || string.IsNullOrEmpty(p.image)) continue;
                if (IsDecodableImageUrl(p.image)) continue;
                affected.Add(p.name ?? $"#{p.id}");
            }
            if (affected.Count == 0) return;

            _warnedUndecodableImages = true;
            var sample = string.Join(", ", affected.Take(3));
            if (affected.Count > 3) sample += $", and {affected.Count - 3} more";
            PrintWarning($"{affected.Count} package image(s) are in a format the Rust client cannot display "
                         + $"(WebP, AVIF, SVG or GIF): {sample}. Those packages show a placeholder in the in-game "
                         + "store. Re-upload the images as PNG or JPG in your Tebex webstore to fix this.");
        }

        private IEnumerable<Tebex.Headless.Package> AllPackages()
        {
            foreach (var c in _categories ?? new List<Tebex.Headless.Category>())
            {
                if (c?.packages == null) continue;
                foreach (var p in c.packages) yield return p;
            }
        }

        #endregion

        #region Player Tracking

        private void FlushJoinEvents(bool force)
        {
            if (_pendingJoinEvents.Count == 0) return;
            if (!force && _pendingJoinEvents.Count < JoinFlushThreshold && _config.JoinFlushSeconds > 0)
            {
            }
            if (!_secretValid)
            {
                if (_pendingJoinEvents.Count > 1000) _pendingJoinEvents.RemoveRange(0, _pendingJoinEvents.Count - 1000);
                return;
            }

            var batch = _pendingJoinEvents.ToList();
            _pendingJoinEvents.Clear();
            var body = JsonConvert.SerializeObject(batch, JsonSettings);

            ApiRequest("/events", RequestMethod.POST, body, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    ReportTelemetry("WARN", $"join events flush failed: HTTP {code}", null);
                    _pendingJoinEvents.InsertRange(0, batch);
                    if (_pendingJoinEvents.Count > 1000) _pendingJoinEvents.RemoveRange(0, _pendingJoinEvents.Count - 1000);
                }
            });
        }

        private void RememberPlayer(int pluginUsernameId, string uuid, string username)
        {
            if (pluginUsernameId <= 0 || string.IsNullOrEmpty(uuid)) return;
            if (!_playerLookup.TryGetValue(uuid, out var rec))
            {
                rec = new PlayerLookupRecord { Uuid = uuid };
                _playerLookup[uuid] = rec;
            }
            rec.PluginUsernameId = pluginUsernameId;
            if (!string.IsNullOrEmpty(username)) rec.Username = username;
            rec.LastSeen = DateTime.UtcNow;
        }

        private PlayerLookupRecord FindPlayerByUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            foreach (var rec in _playerLookup.Values)
            {
                if (string.Equals(rec.Username, username, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
            return null;
        }

        private void LoadPlayerLookup()
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(DataFilePlayerIds)) return;

            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<PlayerLookupDataFile>(DataFilePlayerIds);
                if (data?.Players != null)
                {
                    foreach (var players in data.Players)
                    {
                        _playerLookup[players.Key] = players.Value;
                    }
                }
            }
            catch
            {

            }
        }

        private void SavePlayerLookup()
        {
            try
            {
                var data = new PlayerLookupDataFile
                {
                    Players = new Dictionary<string, PlayerLookupRecord>(_playerLookup)
                };
                Interface.Oxide.DataFileSystem.WriteObject(DataFilePlayerIds, data);
            }
            catch (Exception e)
            {
                PrintWarning($"Could not save player lookup data: {e.Message}");
            }
        }

        private void TrackPermissionCommand(string command, Tebex.Plugin.Command cmd)
        {
            if (!_config.TrackPermissions) return;
            try
            {
                bool isGrant;
                string type, value;

                if (TryParsePermissionGrant(command, out isGrant, out type, out value))
                {
                }
                else
                {
                    var custom = MatchCustomCommand(_config.CustomPermissionCommands, command, out isGrant);
                    if (custom == null)
                    {
                        if (_config.Debug && LooksLikePermissionCommand(command))
                            Puts($"[Tebex] ledger skipped unrecognized permission command: {command}");
                        return;
                    }
                    type = "custom";
                    value = custom.Label;
                }

                var uuid = cmd?.player?.uuid ?? "";
                if (uuid.Length == 0) return;
                var username = cmd?.player?.username ?? "";

                var changed = ApplyLedgerMutation(_permissionLedger, uuid, username, isGrant,
                    type, value, cmd?.package_ref ?? 0, command);
                if (!changed) return;

                SavePermissionLedger();
                if (_config.Debug)
                    Puts($"[Tebex] ledger {(isGrant ? "tracked" : "removed")} {type} '{value}' for {username} ({uuid}).");
            }
            catch (Exception e)
            {
                PrintWarning($"Permission ledger tracking failed: {e.Message}");
            }
        }

        private static bool ApplyLedgerMutation(
            Dictionary<string, PermissionLedgerRecord> ledger,
            string uuid, string username, bool isGrant, string type, string value,
            long packageRef, string rawCommand)
        {
            if (ledger == null) return false;
            if (string.IsNullOrEmpty(uuid)) return false;
            if (string.IsNullOrEmpty(value)) return false;

            if (!ledger.TryGetValue(uuid, out var rec))
            {
                if (!isGrant) return false;
                rec = new PermissionLedgerRecord { Uuid = uuid };
                ledger[uuid] = rec;
            }

            rec.Entries ??= new List<PermissionLedgerEntry>();
            if (string.IsNullOrEmpty(rec.Uuid)) rec.Uuid = uuid;

            var changed = false;
            if (!string.IsNullOrEmpty(username) && rec.Username != username)
            {
                rec.Username = username;
                changed = true;
            }

            var removed = rec.Entries.RemoveAll(e =>
                e != null &&
                string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase));

            if (isGrant)
            {
                rec.Entries.Add(new PermissionLedgerEntry
                {
                    Type = type,
                    Value = value,
                    PackageRef = packageRef,
                    RawCommand = rawCommand ?? string.Empty,
                    GrantedAt = DateTime.UtcNow,
                });
                return true;
            }

            if (removed > 0) changed = true;
            if (rec.Entries.Count == 0) ledger.Remove(uuid);
            return changed;
        }

        private static string BuildReplayCommand(PermissionLedgerRecord rec, PermissionLedgerEntry e)
        {
            if (rec == null || e == null) return string.Empty;

            if (!string.Equals(e.Type, "group", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Type, "permission", StringComparison.OrdinalIgnoreCase))
                return e.RawCommand ?? string.Empty;

            var id = rec.Uuid ?? string.Empty;
            if (id.Length == 0 || string.IsNullOrWhiteSpace(e.Value)) return string.Empty;

            var arg = QuoteIfNeeded(e.Value);
            return string.Equals(e.Type, "group", StringComparison.OrdinalIgnoreCase)
                ? $"oxide.usergroup add {id} {arg}"
                : $"oxide.grant user {id} {arg}";
        }

        private static string QuoteIfNeeded(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return arg;
            return arg.IndexOf(' ') >= 0 || arg.IndexOf('\t') >= 0 ? $"\"{arg}\"" : arg;
        }

        private static List<string> BuildReplayCommands(PermissionLedgerRecord rec)
        {
            var commands = new List<string>();
            if (rec?.Entries == null) return commands;
            foreach (var e in rec.Entries)
            {
                var line = BuildReplayCommand(rec, e);
                if (!string.IsNullOrWhiteSpace(line)) commands.Add(line);
            }
            return commands;
        }

        private static List<string> NormalizeLedger(Dictionary<string, PermissionLedgerRecord> ledger)
        {
            var repairs = new List<string>();
            if (ledger == null) return repairs;
            var drop = new List<string>();

            foreach (var kv in ledger)
            {
                var rec = kv.Value;
                if (rec == null)
                {
                    drop.Add(kv.Key);
                    repairs.Add($"{kv.Key}: record was null; dropped");
                    continue;
                }

                if (string.IsNullOrEmpty(rec.Uuid))
                {
                    rec.Uuid = kv.Key;
                    repairs.Add($"{kv.Key}: missing steamid field, backfilled from the key " +
                                "(replay would have issued a malformed command)");
                }
                if (rec.Entries == null)
                {
                    rec.Entries = new List<PermissionLedgerEntry>();
                    repairs.Add($"{kv.Key}: entry list was null, reset to empty");
                }

                rec.Entries.RemoveAll(e =>
                {
                    if (!string.IsNullOrWhiteSpace(BuildReplayCommand(rec, e))) return false;
                    repairs.Add($"{kv.Key} ({rec.Username}): dropped unreplayable entry " +
                                $"type='{e?.Type}' value='{e?.Value}'");
                    return true;
                });

                if (rec.Entries.Count == 0) drop.Add(kv.Key);
            }

            foreach (var key in drop) ledger.Remove(key);
            return repairs;
        }

        private static bool TryParsePermissionGrant(string command, out bool isGrant, out string type, out string value)
        {
            isGrant = false;
            type = string.Empty;
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var t = TokenizeCommand(command);
            if (t.Count < 4) return false;

            var verb = t[0].ToLowerInvariant();
            if (verb.StartsWith("o.")) verb = "oxide." + verb.Substring(2);

            switch (verb)
            {
                case "oxide.grant":
                case "oxide.revoke":
                    if (!t[1].Equals("user", StringComparison.OrdinalIgnoreCase)) return false;
                    type = "permission";
                    value = t[3];
                    isGrant = verb == "oxide.grant";
                    return true;

                case "oxide.usergroup":
                    var op = t[1].ToLowerInvariant();
                    if (op != "add" && op != "remove") return false;
                    type = "group";
                    value = t[3];
                    isGrant = op == "add";
                    return true;

                default:
                    return false;
            }
        }

        private static List<string> TokenizeCommand(string command)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return tokens;

            var sb = new StringBuilder();
            var inQuotes = false;
            var started = false;
            foreach (var ch in command)
            {
                if (ch == '"') { inQuotes = !inQuotes; started = true; continue; }
                if (!inQuotes && (ch == ' ' || ch == '\t'))
                {
                    if (started) { tokens.Add(sb.ToString()); sb.Length = 0; started = false; }
                    continue;
                }
                sb.Append(ch);
                started = true;
            }
            if (started) tokens.Add(sb.ToString());
            return tokens;
        }

        private static bool LooksLikePermissionCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var c = command.TrimStart().ToLowerInvariant();
            return c.StartsWith("oxide.grant") || c.StartsWith("o.grant")
                || c.StartsWith("oxide.revoke") || c.StartsWith("o.revoke")
                || c.StartsWith("oxide.usergroup") || c.StartsWith("o.usergroup");
        }

        private static CustomPermissionCommand? MatchCustomCommand(
            List<CustomPermissionCommand>? configured, string command, out bool isGrant)
        {
            isGrant = false;
            if (configured == null) return null;
            var first = FirstToken(command);
            if (first.Length == 0) return null;
            foreach (var cc in configured)
            {
                if (cc == null || string.IsNullOrWhiteSpace(cc.Label)) continue;
                if (!string.IsNullOrWhiteSpace(cc.GrantCommand) &&
                    string.Equals(first, FirstToken(cc.GrantCommand), StringComparison.OrdinalIgnoreCase))
                {
                    isGrant = true;
                    return cc;
                }
                if (!string.IsNullOrWhiteSpace(cc.RevokeCommand) &&
                    string.Equals(first, FirstToken(cc.RevokeCommand), StringComparison.OrdinalIgnoreCase))
                {
                    isGrant = false;
                    return cc;
                }
            }
            return null;
        }

        private static string FirstToken(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            var trimmed = command.TrimStart();
            var sp = trimmed.IndexOfAny(new[] { ' ', '\t' });
            return sp < 0 ? trimmed : trimmed.Substring(0, sp);
        }

        private static PermissionLedgerRecord? FindLedgerRecord(
            Dictionary<string, PermissionLedgerRecord> ledger, string userArg)
        {
            if (ledger == null || string.IsNullOrEmpty(userArg)) return null;
            if (ledger.TryGetValue(userArg, out var byId)) return byId;
            foreach (var rec in ledger.Values)
            {
                if (rec != null && string.Equals(rec.Username, userArg, StringComparison.OrdinalIgnoreCase))
                    return rec;
            }
            return null;
        }

        private int ReplayLedger(PermissionLedgerRecord rec)
        {
            var commands = BuildReplayCommands(rec);
            foreach (var line in commands) ExecuteServerCommand(line);
            return commands.Count;
        }

        private void LoadPermissionLedger()
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(DataFilePermissions)) return;
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<PermissionLedgerDataFile>(DataFilePermissions);

                if (data?.Players == null)
                {
                    HandleUnreadableLedger("it is empty or truncated (no \"Players\" object)");
                    return;
                }

                foreach (var kv in data.Players)
                    _permissionLedger[kv.Key] = kv.Value;

                var repairs = NormalizeLedger(_permissionLedger);
                if (repairs.Count > 0)
                {
                    PrintWarning($"Permission ledger: repaired {repairs.Count} problem(s) while loading. " +
                                 "Replay would have silently done nothing for these:");
                    foreach (var repair in repairs) PrintWarning($"  - {repair}");
                }
            }
            catch (Exception e)
            {
                HandleUnreadableLedger(e.Message);
            }
        }

        private void HandleUnreadableLedger(string reason)
        {
            _permissionLedger.Clear();
            var preserved = PreserveUnreadableLedger();
            PrintError(
                $"Permission ledger oxide/data/{DataFilePermissions}.json could not be used ({reason}). " +
                (preserved != null
                    ? $"The file was copied to {preserved}; starting a fresh ledger. " +
                      "Recover entitlements from that copy before relying on `replay`."
                    : "It could NOT be copied aside - back it up MANUALLY now, because the next " +
                      "purchased permission will overwrite it."));
        }

        private static string? PreserveUnreadableLedger()
        {
            try
            {
                var relative = DataFilePermissions.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".json";
                var source = System.IO.Path.Combine(Interface.Oxide.DataDirectory, relative);
                if (!System.IO.File.Exists(source)) return null;

                var stem = $"{source}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                var destination = stem;
                for (var attempt = 2; System.IO.File.Exists(destination) && attempt < 100; attempt++)
                    destination = $"{stem}-{attempt}";
                if (System.IO.File.Exists(destination)) return null;

                System.IO.File.Copy(source, destination, false);
                return System.IO.Path.GetFileName(destination);
            }
            catch
            {
                return null;
            }
        }

        private void SavePermissionLedger()
        {
            try
            {
                var data = new PermissionLedgerDataFile
                {
                    Players = new Dictionary<string, PermissionLedgerRecord>(_permissionLedger)
                };
                Interface.Oxide.DataFileSystem.WriteObject(DataFilePermissions, data);
            }
            catch (Exception e)
            {
                PrintWarning($"Could not save permission ledger: {e.Message}");
            }
        }

        private static bool IsPlayerOnline(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            return BasePlayer.activePlayerList.Any(p => p.UserIDString == steamId);
        }

        private static BasePlayer FindOnlinePlayer(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return null;
            return BasePlayer.activePlayerList.FirstOrDefault(p => p.UserIDString == steamId);
        }

        #endregion

        #region Telemetry

        private void ReportTelemetry(string level, string message, string trace, Dictionary<string, object>? metadata=null)
        {
            if (!_config.AutoReportLogs) return;
            var ev = new TelemetryEvent
            {
                Level = level,
                Message = message ?? "",
                Trace = trace,
                Count = 1,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Metadata = metadata,
            };
            var sig = ev.Signature;
            if (_telemetryIndex.TryGetValue(sig, out var existing))
            {
                existing.Count++;
                existing.LastSeen = DateTime.UtcNow;
                if (ev.Metadata != null) existing.Metadata = ev.Metadata;
            }
            else
            {
                _telemetryIndex[sig] = ev;
                if (_telemetryIndex.Count > TelemetryBufferMax)
                {
                    var oldest = _telemetryIndex.OrderBy(kv => kv.Value.FirstSeen).First().Key;
                    _telemetryIndex.Remove(oldest);
                }
            }
            if (_telemetryIndex.Count >= TelemetryFlushThreshold) FlushTelemetry(false);
        }

        public class PluginEvent
        {
            [JsonProperty("game_id")] public string GameId { get; set; }
            [JsonProperty("framework_id")] public string FrameworkId { get; set; }
            [JsonProperty("runtime_version")] public string RuntimeVersion { get; set; }
            [JsonProperty("framework_version")] public string FrameworkVersion { get; set; }
            [JsonProperty("plugin_version")] public string PluginVersion { get; set; }
            [JsonProperty("server_id")] public string ServerId { get; set; }
            [JsonProperty("event_message")] public string EventMessage { get; set; }
            [JsonProperty("event_level")] public String EventLevel { get; set; }
            [JsonProperty("metadata")] public Dictionary<string, object>? Metadata { get; set; }
            [JsonProperty("trace")] public string Trace { get; set; }
            [JsonProperty("store_url")] public string StoreUrl { get; set; }
            [JsonProperty("server_ip")] public string ServerIp { get; set; }
        }

        private void FlushTelemetry(bool force)
        {
            if (_telemetryIndex.Count == 0) return;
            if (!_config.AutoReportLogs) { _telemetryIndex.Clear(); return; }

            var batch = _telemetryIndex.Values.ToList();
            _telemetryIndex.Clear();

            var key = ResolveSecret();
            var payload = new List<PluginEvent>();
            foreach (var e in batch)
            {
                var pluginEvent = new PluginEvent
                {
                    FrameworkId = "Oxide",
                    FrameworkVersion = Interface.Oxide.RootPluginManager == null
                        ? "unknown"
                        : Oxide.Core.OxideMod.Version.ToString(),
                    PluginVersion = Version.ToString(),
                    GameId = "rust",
                    RuntimeVersion = Environment.Version.ToString(),
                    ServerId = (_serverInfo?.server.id).ToString(),
                    EventMessage = e.Message,
                    EventLevel = e.Level,
                    Trace = e.Trace,
                    Metadata = e.Metadata,
                };
                payload.Add(pluginEvent);
            }
            var body = JsonConvert.SerializeObject(payload, JsonSettings);
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json"
            };
            if (!string.IsNullOrEmpty(key)) headers["X-Tebex-Secret"] = key;

            SendHttp(RequestMethod.POST, "https://plugin-logs.tebex.io/events", body, headers, 15f, (code, response) =>
            {
                if (code < 200 || code >= 300)
                {
                    foreach (var ev in batch)
                    {
                        var sig = ev.Signature;
                        if (_telemetryIndex.TryGetValue(sig, out var existing))
                        {
                            existing.Count += ev.Count;
                            if (ev.LastSeen > existing.LastSeen) existing.LastSeen = ev.LastSeen;
                        }
                        else if (_telemetryIndex.Count < TelemetryBufferMax)
                        {
                            _telemetryIndex[sig] = ev;
                        }
                    }
                }
                else
                {
                    foreach (var sent in payload)
                    {
                        _telemetrySentHistory.Enqueue(sent);
                        while (_telemetrySentHistory.Count > TelemetrySentHistoryMax)
                            _telemetrySentHistory.Dequeue();
                    }
                }
            });
        }

        #endregion

        #region UI Constants

        private const string ColorRedAccent   = "0.85 0.20 0.20 1";
        private const string ColorPanelBg     = "0.10 0.10 0.12 1";
        private const string ColorCardBg      = "0.165 0.165 0.165 1";
        private const string ColorSidebarBg   = "0.08 0.08 0.10 1";
        private const string ColorSidebarRow  = "0.16 0.16 0.20 1";
        private const string ColorGreenCta    = "0.18 0.65 0.32 1";
        private const string ColorInBasketCta = "0.14 0.38 0.22 1";
        private const string ColorBlueCta     = "0.20 0.42 0.78 1";
        private const string ColorPriceGreen  = "0.30 0.78 0.40 1";
        private const string ColorTextDim     = "0.65 0.65 0.7 1";
        private const string ColorTextWhite   = "1 1 1 1";
        private const string ColorBlack       = "0 0 0 1";
        private const string ColorDivider     = "1 1 1 0.08";
        private const string ColorFooterBg    = "0.05 0.05 0.05 1";
        private const string ColorNavBtn      = "0.16 0.16 0.20 1";
        private const string ColorScrollHandle = "0.16 0.16 0.20 1";
        private const float ScrollbarPx = 5f;

        private const float PanelPadX = 0.025f;
        private const float PanelPadY = PanelPadX * BuyPanelPixelAspect;

        private const float BodyYMin = 0.077f, BodyYMax = 0.915f;
        private const float BodyXMin = PanelPadX;
        private const float BodyXMax = 1f - PanelPadX;
        private const float SidebarXMax = 0.22f;
        private const float GridBodyXMin = 0.235f;

        private const string ColorHealthPanelBg  = "0.114 0.122 0.122 1";
        private const string ColorHealthAccent   = "0.698 0.196 0.196 1";
        private const string ColorHealthDivider  = "1 1 1 0.04";
        private const string ColorHealthIconBtn  = "0.20 0.20 0.20 1";
        private const string ColorHealthTextDim  = "0.73 0.73 0.73 1";
        private const string ColorHealthGreen    = "0.18 0.73 0.35 1";
        private const string ColorHealthRed      = "1 0.067 0.067 1";
        private const string ColorHealthOrange   = "1 0.52 0 1";
        private const string ColorHealthBadPill  = "0.35 0.14 0.14 1";
        private const string ColorHealthTileBlueBg  = "0.125 0.173 0.204 1";
        private const string ColorHealthTileBlueBar = "0.176 0.380 0.537 1";
        private const string ColorHealthTileGreyBg  = "0.235 0.243 0.243 1";
        private const string ColorHealthTileGreyBar = "0.730 0.730 0.730 1";
        private const string ColorHealthTileRedBg   = "0.290 0.110 0.110 1";
        private const string ColorHealthTileRedBar  = "1 0.067 0.067 1";

        private const float HcRefH = 743f;
        private const float HcColGap = 0.018f;
        private const float HcColW = (1f - PanelPadX * 2f - HcColGap) / 2f;
        private const float HcCol0X0 = PanelPadX;
        private const float HcCol0X1 = HcCol0X0 + HcColW;
        private const float HcCol1X0 = HcCol0X1 + HcColGap;
        private const float HcCol1X1 = 1f - PanelPadX;

        private const float HcAccentYMin = 0.9955f;
        private const float HcHeaderBtnYMin = 0.9381f, HcHeaderBtnYMax = 0.9825f;
        private const float HcHeaderBtnW = (HcHeaderBtnYMax - HcHeaderBtnYMin) * 1.242f / BuyPanelPixelAspect;
        private const float HcDividerYMin = 0.9165f, HcDividerYMax = 0.9192f;
        private const float HcSubtitleYMin = 0.8721f, HcSubtitleYMax = 0.9058f;

        private const float HcPillYMin = 0.8210f, HcPillYMax = 0.8641f;
        private const float HcPillGap = 0.005f;
        private const float HcPillUnitW = (1f - PanelPadX * 2f - HcPillGap * 2f) / 3f;
        private const float HcPillMarkW = 0.070f, HcPillInnerGap = 0.002f;

        private const float HcCardHeaderH = 39f / HcRefH;
        private const float HcCardPadB = 8f / HcRefH;
        private const float HcLabelX0 = 0.052f, HcLabelX1 = 0.53f;
        private const float HcKvValueX0 = 0.60f, HcKvValueX1 = 0.945f;
        private static readonly float[] HcColX = { 0.55f, 0.71f, 0.85f };
        private const float HcColXEnd = 0.99f;

        private const float HcTilesTop = 0.330f;
        private const float HcTileH = 53f / HcRefH, HcTileGap = 18f / HcRefH;
        private const float HcTileBarW = 3f / 697f;
        private const float HcTileTextX0 = 0.095f;

        private const float HcFooterYMin = 0.0175f, HcFooterYMax = 0.0511f;
        private const string HealthPanelAnchorMin = "0.12 0.06";
        private const string HealthPanelAnchorMax = "0.88 0.94";

        private const string ColorCmdRefSubtitle  = "0.30 0.78 0.92 1";
        private const string ColorCmdRefSection   = "0.220 0.090 0.075 1";
        private const string ColorCmdRefSectionTx = "0.900 0.380 0.220 1";
        private const string ColorCmdRefRow       = "0.145 0.145 0.145 1";

        private const float CrRefH = 720f;
        private const float CrBodyYMin = 0.075f, CrBodyYMax = 0.855f;
        private const float CrSectionPx = 24f, CrRowPx = 28f;
        private const float CrSectionGapPx = 12f, CrRowGapPx = 2f;
        private const float CrPadTopPx = 4f, CrPadBotPx = 8f;
        private const float CrTextX0 = 0.020f, CrRowTextX0 = 0.038f;
        private const float CrSyntaxX1 = 0.520f, CrTextX1 = 0.985f;
        #endregion

        #region Embedded Assets

        private const string LogoPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAQAAAACHCAYAAAD5sIeyAAAdRUlEQVR4nO2djVXbytaG93y3AJQKYiqIqeBKFcSpIJ4KYio4cgWYCmIqwFQQUQGmgogKIgq4h+/ZCCHbWCPZlmwc5lnrXS+RZvZszc/2D+ckRvbM09NTaIxJxOPxHBxfADyeD4wvAB7PB+YQBSCmAMTi8XgOziEKQCIiA4pAhns8ngNyiALwhEXGfwzweA7OXgsAZ7+P3aFzCsAE93g8B2TfBUAP/Q80pwCc4R6P54DsuwD8xnqSc2qMScXj8RwMg/YChz8UkV+oYGz8bwM8noOyzwKghz+UkgydGv/bAI/nYOylAHD4Q1l+9S8YG/8uwOM5GPsqAHdYH63j1PjvAjyeg9B5AeDwxyLyD6oioQBEuMfj2TMGdQaHf4BdozouKQIj3OPx7JHOCgCHv4/9QgFqgjXGTMXj8eyNTgrAFoe/wBpfBDyevdF6Adjh8BdY44uAx7MXWi0AHP6hiPxEuzKlCFjc4/F0iEE7w8HXV3s9+APUFnNkjTHqHo+nA3YuABz+79gEBagLYsl/S5DhHo+nRbYuABz8UPLf74fSPalQCCgCV7jH42kJgxrDoQ+wrygWkZ7snwxN0JXx//Wgx7MzBjWCw9/HpiLyBR2aB6TvCKbi8Xi2xqCNeCkEI/Qd7ZtbNOHgz3CPx7MjBm0FhaAnvArLfgrBLdJX/EQ8Hk9rGLQTFII+NkH/RW3zgEYc/Bnu8XhaxqBWoBCMsAvUFldID3+GezyeDjCoNSgCfWyGPqNteUR68Kfi8Xg6pdUCoFAEAiyR7X5boIc/5PDPcY/H0zGtFwBlyyLgD7/Hs2c6KQDKSxGYo6YfB8784fd49ktnBUChCPSxO1THOYd/gns8nj3SaQFQKAIj7AJVccPhH+Aej2fPGA7oL7xrQqlmjjLUJecUmTnu8XgW0ALwhP/tRMb/V4Qezxt8AfB4PjC+AHg8HxhfADyeD4wvAB7PB8YXAE9rsJVCcf9foVfG/01O7wpfADytwVaKJf97IquIjF+Hd4UvAJ7WYCvF4gvAUeELgKc12Eqx+AJwVPgC4GkNtlIsvgAcFb4AdABTGssHPAgf9bmPGV8AOoApjeUDHoSP+tzHjC8AHcCUxvIBD8JHfe5jxheADmBKY/mAB+GjPvcx4wtABzClsXzAg/BRn/uY8QWgA5jSWD7gQfioz33M+ALQAUxpLB/wIHzU5z5muiwAGbpEARqgz+hQRGaPG48pjeUDHoSP+tzHTJcFQJmz4Ge4bo6e5IUglPyfGN8nkelo470812e0yFByVTFCc9SUe/LP8I14yU3nOpS8EPdRgJRUSs3QrdlijEUYL5YtCgD9+th3pK6EkpOhOVJm6Ib+qewBcupJ/j82DVCA+ihASiqlEsnzyvBaiKsx63gwWzwnsQPsC3LxSOw5/kzXBUCZMqDFl2DYARZKrrqkdyUyazZeG/AcU8k3b5dEZoP8yUnziUWkJ5sxFZGx2WLzKYwbywYFgPa6B7R9HzVljibEucJbh5y+YyPUR5uQSD53iTgg/gi7QC7mKDINi0oBsa+xAXJxRtw5/sw+CoByzqATfC2kEGChlPqC2iQyNQuzLeT+G+tJt0SmQf7kMsAuUE92Yyr5mmV4Yxg/lvxAVxEZnoN2PRH5iULZnjnSHBNpAXIKJc+pJ7sxQ9Y45o6xZthX5OKSGCO8EcQcSp6/i3NiTvBXqgrAA0qlOX10glxY0/Df+yOlAAsljxvK7owYe463Cnn2ROQ36prI1Gx0crnARqgt5siaDeaNHGKpKQAoQ79QgNrAmob7qgry/geLpT0yFJmKuWO8AJujz8hFZGrWXSFeT/J/fyNAVdwQa4AvUVUAItNg4AJCJJJ/XqrDmh0X6z3Bcw+lvuq2QWQq1oMcAuwahdI+GYpMxUZehVxicReAc6T3A9Qm1my5r8hZ526A2iZD38grkTUwbih5IXSRSv6WXWNVQiyNE0o1D6i/Ls66AnBDwwHeGEIk0qwAKNZsuVjvDZ57Kt1//lciU72RutrABak02IQKucSSH/BD8I0cZ3hjyPcnNpTuyNAZeaWyBsaPpX6+Luk/wtdCDL13gVycEWOOv2FdATg1FQlXQYhEmhcAZcoYFj9qeO4/WIC6JjJrCgDjT7AfqAn3aI5SyQmFVwV0guqYMf433An5xFK/obsiQ7rRU2kAuY6wC9SEezRHqeSE0nzu5uR0hq+FPBKpPzvfiDHDl6BvT+rf+p/Td4KvZbUAjI0xsWwIIRKpf4hVZsiaBq8s7xGeOcBGaB2huOfjCqXSHC2YqSzA+KHUv4VUdKx4tX8BcYbCt+roBLmwpuadG7Fi2bwAPKKpsB+In8gCxOtjQ8lVl5+SECPCnRA3lGZzd4NGxExlDcQZCnMr9Z/lramYO2IEWCru58vQqVk5K/T9hYVSza0xJhQHiwXgEfXosDRIEwiRiHvDV5FKXt3m+F8D8xGL+yBEZmWzbwpj3GF9VIWu56DJOMQKsETcv31JiOU8XMSJxf3cq1yimLgZXglxA2yCvqM6IlPzzMT7hYXixpqKQ7sIsQIsEffcpcQ6xddCjFDqC9KMGN/wZ+gzwi5QFY+oR58Mr2SxAFjT4IHXQYhEtisAiiYYM/Yl/lfAfMTiPgiRqdmkLog/lPovH88YY443gpg94e0qOkFVRMaRNzFicT/3ItZsuN+IP8IukIsb4g7wtRAjlPrDZs0GuREzwOboM6rCGkdMYsRSP3ffiDGjbZ+f75CLyDjWqqAoAPc01qBbQYhEti8ABYnkk5TKkcN8xOJezMg0WJwqiK+L71qvsdnuo9wIu0BVXBljhlIB/WNxP3fBOXEm+MYwxlTq3wmcmop9RP8Z9hVVcUnfEb4RxB1g16iKG+Jqm0qIkYj7HGXoDOk4fVTF2DRc/6IARGa3DZmIO/GmZCgml0v8aGE+YnEfhMhsOd/EDrA/qIoHY0xPtoT4GXaC1pES+xRfC31jcT+3cmtqPpe6YIwAS6U6R8Waildb+v/BNMY6HlGPvhm+McROpfpdQEbcT3gl9A+wVNzPlqEAVXFrNphfLQAzOgz4eWuIkUg7BaBgjs7JK5EjhPmIxX0QIrPlsxF7KO63/zpvE3wriD8V9yvsqal+dY3F/dxKZLZ89oIG41yZNe9U6DfArlEVl/Qb4VtB/An2A1VxRvw5XgkxBtg12oZH1GOMDG+EFgDtkMoOECORdgtAQUJuEX5UMB+xuDdoZLY8BMSeivuA1m4yF8QfYReoCmuqX11jcT/3gzGmJzvCOH3sDlWRMs4pvgT9YnHn941+M3wriD/ArlEVY9PgrTlxJtgPtCmR2XBfGbQzJJxINwVAeKBWctwnzEcs7o0WmQ0XqoDYiXQ01w0Zm4pNTG6xuJ/7yqx5Zd4GxsqwE7QWxnmzb+iTyDudu1XIdY59QU0Zm4axF3kzSdtAsol0NLE8VCs57hPmIxb3QYjM9gXgDuujQzE2FRuN3GJxP/fYVPTdFMZKxL3nPjFWhr/SoE/XXJLTCK+FXHuSfxQ+QXXcE7ePb0wrh4tkE+loYnmwVnLcJ8xHLO6DEJntC8ATdkguyX2Ev4HUYunouVdhrETcey4yK2PR5w7ro0Nxazb4go58B9g1quOMuHN8Y1o5XCSaiHsxtoYHayXHfcJ8xNLRQSD2E3ZIbk3FJia1WDp67lUYayru70IiszIWfZ6wQ3JrKuZuHaQbSv1/s6BMiWvxjTFoZ0g0EV8AXmE+YunoIBD7CTskl+Q+wt9AarF09NyrMFYi7j13xlhz/JUGfbrm1jQsAOQaYL+RehOsqfhy1kUrh4tkE+loYnmoVnLcJ8xHLB0dBGIn4p7rMeqSpCp3covF/dxjs6fvABjHYEvU9YEx6pKUtKbSAHL9hYXSnAxFZqXo1WHQzpBsIu6J3RoeqJUc9wnzEYv7IESm4hDVQexE3HN9anb8te62kFss7ue+Mu39FuAJq4Rx3uwbuiTinrs37xoOAXnG4p7HKjT3yKx8+enizSRtAwkn4p7YreFhWslxnzAfsbgXMDLbF4CpuD/7fiP2DN875BaL+7lTcjvFd4JxBpjry7F7xunjS9AvFnd+1jR8he4Kcgyl2ef+KqY8g8UbYdDOkHQivgC8wnzE4t5okdm+AAzF/V8C3hB7gO8dcovF/dxKZLZ89gLG+YWFUs3YrPmoQb9Q3IdrTr8z/CCQX4D9RupV3KL/IhfWNCxkBu0MiSdSn9RW8CCt5LhPmI9Y3AchMlseAmLr5viDXJwRf47vhI5FnIwfG0H7WNzPrSTEjPCtYIxQ3IdYiUzF/NL/CXMRmYq+m8AwG82dQp9fWCjV3BBzQLtE3OdNx41Mgz1g0M40SGhreIhWctwnzMdQ3K/S33isGb4VxJ9jX1AVc+Jv/UpG/ADT/APiRHgj6BdLfQFQxmbNK3QdxNe8fiP1Kh6MMT2pgBgz7CuqIpW8gGb4xhA/wC5QH0WmYRz6xeKeu0fU03i07QlrjE5QFXo/MrTHKzFoZ0goEV8AXmE+QnG/Sl2Zmi/DiNGjTSpr4N5Q8gPqYkp/i28EsfuYxlZXGsehbyzuTbyINQ3fpirE7kn+ub/Iq4pz4k7wtRAnFPfaKHMUmZrDswqxA0xj95HSaO7op+3vkIvILLwzoc9Q8nVyUTu+QTtDMon4AvAK81G3oBmKTMVbNPr/wGLJ3ykksgbapFL9v54WaPzzqhiLEC/A/kEjtIo1DQ4rMWLJYzRlhjS/VBwQ9zs2QQFy8foqiVdCvETq92sq+XMnUgPxAkzXbIQCtMjYON7tvPS9Qz2p5pIYI3wJ+s6wr8iFNY61M2hnSCSR+gndCpJvJcd9w5ykUn9Ap5Ifggwpgxf1JCfl8U/xNxBf212jJszRDCWSc4++IKWPQsnHdRGZmsNATrFsVgAKZi9KpSRAoeR59aQZ1jg2ewF5hpK/UjdhjmYokZzFuetJnl8oeb5VWFORF7lcYwNUxQPq0z/Dl6BvgM3RZ1RFhiJT8WJj0M6QSCK+ACzBnEzF/eu6plhTvXkm2A+0DzJ0Ri6pVEA+sWxXANrghtwGeCPIdYRdoH2QoTPyS2WBhjlovzm+FmKEUl/MtH9k1hSRVg4XSSTiC8ASzElP8i+sdiVlCk7xtTBOIh3N/QpXpv57i1gOUwD0VTkkvzcb3AX5TqWdIl3HlVmZO8buY3fIxdg4Pj4UECuW+nmfEsviSxi0MySQSEebkKRbyfEQMC8T7AfaFWuq3wUE2FTqPwvugjUV4y9CLrG4N+IZSsT97fWm3KOQ/DJ8I8g3wCboO+qKS3Ib4a+8jHuHelLNPf36eCOIOce+IBfWrKyjQTvD4In4ArAW5qbJwtRxwzQM8EoYJxb34duGRzRi7Kk0oC4H4ujfQNXnx0TaKQK3aEDYDN8achphF6hNKueO8a6xAapC+4b0neONIGYfS8Q9rxmKzEJcg3aGwRPxBWAtzE2ATWW7V2jdCDFTMMFrYaxQaC+7r4WOq2NOGDvDG8H4sdQUAEzbBdgEfUfboPmNCDeVliCnUNqZO+USxeSX4Uswzgi7QC7O6TvBN6Jh7DmKzEtuzwuyKwycSDsT9wYSbSXHQ8McDbARajJPj2ii4vEzfCMYK5T8X9MZoBPUlHs0Q1PGTWVDGDeWBgWggPahbJan5jcVRKgMbx1y6mMjFIr72/VVNLdE8jVLZQ0vse+Qi1vT8H8ZXgdjzLCvyIXOn8V9Adg3zFUfW1TBHKXCJuKR9edWeBkvlPzXVD3JpWRojhR1HTfDDwJ5DrA+UkLJSSVXhmbkl8oeIac+Fko+d6o+UjI0R0oqeW4ZfnQYtDNMVCK+AHg8R0crh8sXAI/nOGnlcPkC4PEcJ60cLl8APJ7jpJXD5QuAx3OctHK4fAHweI6TVg6XLwAez3HSyuHyBcDjOU5aOVy+AHg8x0krh8sXAI/nOGnlcPkC4PEcJ60cLl8APJ7jpJXD5QuAx3OctHK4fAHweI6TVg6XLwAez3HSyuHyBcDjOU5aOVy+AHg8x0krh8sXAI/nOGnlcPkC4PEcJ60cLl8APJ7jpJXD9dELwM+fP//BCm6ttYl4PDvAnhpK+bcS31rHnqJtH9O/CThA2f/93/+l379/v+LnWgzaGV8Afj5hBWNrbSxHArn3Md08r+gG+vfff294jow/eg4A65JIeabG1r7dU7QJsGsUylvGdk2fVVo5XL4AHFcBIF/dOD/QCAWoikTy50nEs1dYo0TKMzW2a/YUbX5hoazn1FqbSg2tHC5fAI6nAJBrH/uFAtSUqYic81wZ7tkDrFMi5Zka25U9xf0+docKLtFU8nUNV9tX0crhogDMsK+odTj/reTYJSzGE1Ywtg0nf9+Q5wC7RqvcoDlSAjRAn9EicxRZXwT2AmuViLsAjLALpNxzv49vTCuHiwIQYIns/o9gvoHz30qOXcJiPGEFY7uyWO8BcuxJ/oqha1WgB39Ivhm+BO1DyV9RPqOCKW0t7ukY5j8RdwGIpfxn2G6ttaFsgUGt0FUR4Py3lmNXsBhPWMEYTdAPNEB9pKSSz8/YLnw2o2+AXaOCS+7P8Ero8xPrSU5te4Uuv7BQSuhmp+KAPgGWyPKaRpbvBLgXSrkBFS7bVCqgfU8wVPCN9hn+DPcD7DsaoFByMjRDV5YxZQ3062MX6BnaRVzrST7/oTD/XDNcG/CzXlPuuTbC10Lbn1hPcsa22dhvYnK/J/mYfRRKToYSvmidub6pp28iawoA139hSk9yKRpzjgre5FKFQa3RRRHg/LeaYxewKE9Ygb6q/hfpXFQxYYHO8WfoP5V88ysp907xtdA2lPwzvPKIerTP8Ero08fuUMENfQZ4LfTtichvVPDal3sZdoKUS66P8LXQNpayYNzahVcs7g0lP0gBqiKRlaKh0DeUcj4Ui36iV+ijBaAny8/xietLsRTa9bE7VHBlrR3KGmg7wX4g5Zx2+udnuKfXX/9cQSr5M83xJeifSL6PlLEtC8ATVsetXZhfFwa1SttFgPPfeo5tU7MoekhP0CpTFsni2j+U5U0c2epXnamUxeLKVmzORegzwX6gglPreLVehf5TKcdUPtE/43os5aHOuPYJXwtt/2ABUr7Rdobr9aFgaJF7lKE+OkEFcxRZxsafoX8oy3P3BtobTNvOsS9I4fLbd0C0mWA/UEGGTu3CmAW0/YMFSPlUtOH6BPuBmnJG3zn+CjESOcYCoLRZBDj/neTYJhWLMkYTFiLDtc1Q+DM6QQXczjch9+fYF6TccH2AL0Gbniy/ip3aBgeZfomUm+nBWtuTDaC/5nKNCiJLgeJ6T5bz4XL+PIvQbigYUh7sy/hc78ly/xs04n4qL9BmKMvzNrYvh0HhfihvC8Aj0j6JAO0TAdoOBUNK1Rz/wQK0CE2Xn4t2A+waKa+xuB7Kcj4PSJ9phuv9ABuhf1BBKnkRyPBnaJdIuWZju/DMCvdjKWPc2oYHfhWDOqGtIsD57yzHtmAxnrBFvrEgM3wJ2vUkfxU7Qcqcdme43hsKhgpO7cJBUGgTS7noN9wf4LXQL5FyM93aDTcL/UNZ3tSEyA8E96ZSvjt4fZ5FaHOH9ZFyTpsJrtenUva9RyH3MnwJ2g0FQ0pGm0/4M9wLZTk35Yw2c3wJ2gbYH1RwahfmmPsD7Bopj+gEKTe003uv0HYqZe7fuD/D9fovLJQcjdHjXoYvQbuhYKjgnHYT/BnuJ1Ku2dgeWwFQ2igCnP9Oc2wDFuMJK7i1jsWgbSzlwimfaJ/hei+V8lv3sX276H+wACmRfXllq4N+c+wLUm6tI7910L8ny6/U58SY4HovlOUDeMa9Of7Myv2lA8G9P1iAFC7nRWUdtE2lnJtvtJ3hej2UMr5yyb0Rvhbaz7CvSDmn7QR/ZvUeukAFn2ib4douwP4g5ZHr+me93pPleeKW85nm2Bek3NqFdeFeIsdeAJRdiwDnv/Mcd4XFeMIKxnZlsRahbSjLGzayLweZe7GUi7r6SjcUDCkP1tqeNIS+iZSb6dZuuFnoH0pFzgr351ixvld24XsJ7k2lfKW8si/3uB5gf1DBHGWoij4KkDK2L3NMnFAcua1C+wF2jZQ5bc9wvR5gf5DyfKi5NpUy93OuTXBtOxQMKVe2fKZQNssllur1TqRcs7F9ed4C7sdS9r21G65pwd4OF4VgKuVkNobzv7cct4XFeMIKXjfKOmjbx+5QQWRfNgn3AuwPKuBW/grCvTusjxQu59ebQN+pLM/9J/pneCPoP8F+oIJTu/zWeSgYKniOz/WeLL8intqXftwLZfmwbMLYvhyINXEi+zKfVdAnw06QcmrJiWtD4RZSLrk24tqAn6+RMufaGS5c/4WFknPG9Tmu10NZzuUT9zJ8LbSPpTzEuqive517ifxNBUDZpghw/vea4zawGE9YwdiuLNYitB1gxaZSIruwYbk/lXKOnjcd1/r8fIeUR9TjeoY3gv4D7BoVjK0jx0XoG2C/kbpyT98+vgTtMuwEKee0mXAtFscm5f4TVnCFUmlGQqxEgBihLB+6yL7cq4I+E+wHUi5pP+LaHT/3kXLGtTmubVMpP3qcIuU3Uh6stT15gbahbJAL7adSrvWDXY6VyN9WAJRNiwDnf+85bgqL8YQVpCzGKb4W2k5l4flpa7BXuN/HdDMWRGgoZZ9L+ozwjSBuKuVGVs6IM8ed0O8nNpQSur1990GzWMoNmQrx0W8UIOUb/Wb4K/RJpczpyr68ld4EYoSywaFT6NPHijlOhT7oN1Lu6a/3n6HtBPuBlEuUSvndwDlt9f4ztA2wP6hgbFcObsFL2zvUk5wb2g7wZ7ifyN9YAJRNigDn/yA5bgKL8YQtMmFBzvElaKcLfI0Klha9gHaJlIs/QwNUcGp5yyobQkyNsTh2hgi1fCgLaB+ooQEquKd9H38D7XtSHiJlhgZIebDW9mQF+sRSbmKFZm+Li0JbjZVwP8Nf4XooGxYAhX6plMUnkfIt/Tn9J/gztOtJ+Vyp5PQk59SurAXtp7K8t89oM8eXoN0FNkIFNCufnfuJlHtgbP+mAqA0LQKc/4Pl2BQW4wlbRf9zz8m///778J///Ofkf//731CWX0mVyK7ZrMQbYNdolRva672tIG4s5aYpmKMpOarrugTkPOBHVYAKHlGf8VOpgPgz7Cta5Zx+E3wJ2gdYKuVHB0VzmTJfD/wcMIefyWfEz6Ewp8SJ8FeIEcp2BUBjXqBVPtE/w1+h7Rz7gha5tWsOHW17ks/pCSqY8EwznsnwPCcLz1Nwa1diESeRv7kAKGy2qdQUAc7/QXNsAovxhBXoQVlc/CpYs7Lir0LMVMpXqILINtjcLogbS7lxmnKPhow9xyshdijLh1F5RD36Zvgb6NPHEmk2Z0pkF+aA/qEsjxnZhftV0K8n5St7wQ19B/gStB0Khhah6fr1q2hfxQPSwprhrxAjkb+9ACh1RYDzf/Ac62AxnrCCbyiWt68YBY9oxIJNxQExR9gFKrinTx/fGWKHkuf4X+RCc52oGDvDayF2KsuF68rWfLanT0945Rd3PprLkFgz/BX6hrJFAVDom8jymN/ouxRfoV2A/UEFmkuPthm+Fvr0sRn6jKq4QqN1ceifSJnb2P6tBUBxFQHO/7vI0cXLYhRMWYyUawN+VvUkJ5X8lW7G/Qx3Qv8AW9x0dLNTaRHG6GMDFMoyqWyQ6yLEDGU53pQYqTSAvn1sgEIpScWRC316QmGQkk3GC2VhLPrFUgFth7KwlrSdSgPoN8BC4VUeFcxUxEilAvoNpRwvoW0iC3A/lDyuknJ/Kltg0Lugqghw/t9NjvuEBR4KhpRHFjjAPZ5WeVeHa10R4Py/qxy7gMM+wIRDPsP0z33sFwqQcs69Ce7xtMq7O1wUgVjKzzYfpQD8xnqS/2ouleW3iw/W2p54PB1g0LuDIjAUzgX66wsAh38oGFqHftkUUgDmuMfTOu/2cBVFgPP/bnNsi5ciMJTyW98HNEMTDn8qHk9HGPRu0SLA+Z+Kx+PphP8H+9rQS/WOOWYAAAAASUVORK5CYII=";

        private const float LogoAspect = 256f / 135f;

#pragma warning disable CS0414
        private uint _logoImageId;
#pragma warning restore CS0414

        private void LoadEmbeddedImages()
        {
            var community = CommunityEntity.ServerInstance;
            if (community == null) return;
            _logoImageId = StoreEmbeddedPng("logo", LogoPngBase64, community);
        }

        private uint StoreEmbeddedPng(string label, string base64, CommunityEntity community)
        {
            try
            {
                if (string.IsNullOrEmpty(base64) || base64.StartsWith("__")) return 0;
                var bytes = Convert.FromBase64String(base64);
                return FileStorage.server.Store(bytes, FileStorage.Type.png, community.net.ID);
            }
            catch (Exception e)
            {
                PrintWarning($"Failed to load embedded {label} image: {e.Message}");
                return 0;
            }
        }

        #endregion

        #region UI

        private void SendUi(BasePlayer player, CuiElementContainer elements)
        {
            if (player == null || elements == null) return;
            foreach (var element in elements)
            {
                if (element?.Components == null) continue;
                foreach (var component in element.Components)
                {
                    var rect = component as CuiRectTransformComponent;
                    if (rect == null) continue;
                    if (string.IsNullOrEmpty(rect.OffsetMin)) rect.OffsetMin = "0 0";
                    if (string.IsNullOrEmpty(rect.OffsetMax)) rect.OffsetMax = "0 0";
                }
            }
            CuiHelper.AddUi(player, elements);
        }

        private void OpenBuyUi(BasePlayer player)
        {
            if (player == null) return;
            FetchCommunityGoals();
            if (!_uiStates.ContainsKey(player.userID))
                _uiStates[player.userID] = new BuyUiState { CategoryId = _categories.FirstOrDefault()?.id ?? 0, Page = 0 };
            DestroyUi(player);
            DrawBuyUi(player);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiName);
            if (_uiStates.TryGetValue(player.userID, out var st)) st.OverlayDrawn = false;
        }

        private void FlushAllUi()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, UiName);
                CuiHelper.DestroyUi(p, DebugUiName);
                CuiHelper.DestroyUi(p, TelemetryUiName);
                CuiHelper.DestroyUi(p, CommandRefUiName);
            }
            _uiStates.Clear();
            _commandRefFeedback.Clear();
            foreach (var t in _debugUiRefresh.Values) t?.Destroy();
            _debugUiRefresh.Clear();
            _debugView.Clear();
        }

        private static int BasketQuantity(BuyUiState state, int packageId)
        {
            var bp = state?.Basket?.packages?.FirstOrDefault(x => x.id == packageId);
            if (bp == null) return 0;
            var qty = bp.in_basket?.quantity ?? 1;
            return qty < 1 ? 1 : qty;
        }

        private void BuildPackageCta(BuyUiState state, int packageId,
            out string text, out string command, out string color)
        {
            if (!_config.EnableBasket)
            {
                text = "BUY NOW";
                command = $"tebex.ui buy {packageId}";
                color = ColorGreenCta;
                return;
            }

            var qty = BasketQuantity(state, packageId);
            if (qty > 0)
            {
                text = qty > 1 ? $"IN BASKET ({qty})" : "IN BASKET";
                command = "tebex.ui view basket";
                color = ColorInBasketCta;
                return;
            }

            text = "ADD TO BASKET";
            command = $"tebex.ui add {packageId}";
            color = ColorGreenCta;
        }

        private static readonly string[] UndecodableImageExtensions = { ".webp", ".avif", ".svg", ".gif" };

        private static bool IsDecodableImageUrl(string? url)
        {
            if (url == null || url.Length == 0) return false;

            var lower = url.ToLowerInvariant();
            var path = lower;
            var cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);

            foreach (var ext in UndecodableImageExtensions)
            {
                if (path.EndsWith(ext)) return false;
                var bare = ext.Substring(1);
                if (lower.Contains("format=" + bare) || lower.Contains("fm=" + bare)) return false;
            }
            return true;
        }

        private void DrawBuyUi(BasePlayer player)
        {
            if (!_uiStates.TryGetValue(player.userID, out var state))
            {
                state = new BuyUiState { CategoryId = _categories.FirstOrDefault()?.id ?? 0, Page = 0 };
                _uiStates[player.userID] = state;
            }

            var elements = new CuiElementContainer();

            if (state.OverlayDrawn)
            {
                CuiHelper.DestroyUi(player, UiName + ".Panel");
            }
            else
            {
                CuiHelper.DestroyUi(player, UiName);
                var overlay = new CuiElementContainer();
                overlay.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.85" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true
                }, UiOverlay, UiName);
                SendUi(player, overlay);
                state.OverlayDrawn = true;
            }

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorPanelBg },
                RectTransform = { AnchorMin = "0.12 0.06", AnchorMax = "0.88 0.94" }
            }, UiName, UiName + ".Panel");

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorRedAccent },
                RectTransform = { AnchorMin = "0 0.992", AnchorMax = "1 1" }
            }, UiName + ".Panel", UiName + ".Accent");

            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0.918", AnchorMax = "1 0.992" }
            }, UiName + ".Panel", UiName + ".Header");

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorDivider },
                RectTransform = { AnchorMin = "0 0.915", AnchorMax = "1 0.917" }
            }, UiName + ".Panel", UiName + ".HeaderDivider");

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorFooterBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.075" }
            }, UiName + ".Panel", UiName + ".Footer");
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorDivider },
                RectTransform = { AnchorMin = "0 0.075", AnchorMax = "1 0.077" }
            }, UiName + ".Panel", UiName + ".FooterDivider");

            if (!_config.EnableBasket && state.View == BuyUiView.Basket)
                state.View = BuyUiView.Store;

            if (state.View == BuyUiView.Basket)
            {
                DrawBasketHeader(elements, state);
                DrawBasketView(player, state, elements);
                DrawFooter(elements, state, checkoutContext: true);
            }
            else
            {
                DrawStoreHeader(elements, state);
                if (!state.OpenPackageId.HasValue)
                    DrawStoreSidebar(elements, state);
                DrawStoreBody(elements, state);
                DrawFooter(elements, state, checkoutContext: false);
            }

            if (!string.IsNullOrEmpty(state.LastBasketError))
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = state.LastBasketError, FontSize = 11, Align = TextAnchor.MiddleRight, Color = "0.95 0.35 0.35 1" },
                    RectTransform = { AnchorMin = "0.30 0.905", AnchorMax = "0.78 0.93" }
                }, UiName + ".Panel");
            }

            SendUi(player, elements);
        }

        private void DrawStoreHeader(CuiElementContainer elements, BuyUiState state)
        {
            var title = _serverInfo?.account?.name;
            if (string.IsNullOrEmpty(title)) title = "Tebex";
            elements.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 22, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0", AnchorMax = "0.90 1" }
            }, UiName + ".Header");

            elements.Add(new CuiButton
            {
                Button = { Color = ColorRedAccent, Command = "tebex.ui close" },
                RectTransform = { AnchorMin = $"{1f - PanelPadX - CloseBtnW:0.000} 0.18", AnchorMax = $"{1f - PanelPadX:0.000} 0.82" },
                Text = { Text = "×", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, UiName + ".Header");
        }

        private const float CloseBtnW = 0.045f;

        private void DrawStoreSidebar(CuiElementContainer elements, BuyUiState state)
        {
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0 {BodyYMin:0.000}", AnchorMax = $"{SidebarXMax:0.000} {BodyYMax:0.000}" }
            }, UiName + ".Panel", UiName + ".Sidebar");

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorDivider },
                RectTransform = { AnchorMin = $"{SidebarXMax:0.000} {BodyYMin:0.000}", AnchorMax = $"{SidebarXMax + 0.002f:0.000} {BodyYMax:0.000}" }
            }, UiName + ".Panel", UiName + ".SidebarDivider");

            var cats = _categories ?? new List<Tebex.Headless.Category>();
            const int maxRows = 14;
            const float rowHeight = 0.055f;
            for (int i = 0; i < cats.Count && i < maxRows; i++)
            {
                var c = cats[i];
                var yMax = 0.97f - (i * rowHeight);
                var yMin = yMax - rowHeight + 0.005f;
                var bg = c.id == state.CategoryId ? ColorSidebarRow : "0 0 0 0";
                var rowName = $"{UiName}.Cat{c.id}";
                elements.Add(new CuiPanel
                {
                    Image = { Color = bg },
                    RectTransform = { AnchorMin = $"0 {yMin:0.000}", AnchorMax = $"1 {yMax:0.000}" }
                }, UiName + ".Sidebar", rowName);
                elements.Add(new CuiLabel
                {
                    Text = { Text = Truncate(c.name, 24).ToUpperInvariant(), FontSize = 13, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = $"{PanelPadX / SidebarXMax:0.000} 0", AnchorMax = "1 1" }
                }, rowName);
                elements.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = $"tebex.ui cat {c.id}" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = "" }
                }, rowName);
            }
        }

        private void DrawStoreBody(CuiElementContainer elements, BuyUiState state)
        {
            var bodyXMin = state.OpenPackageId.HasValue ? BodyXMin : GridBodyXMin;
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"{bodyXMin:0.000} {BodyYMin:0.000}", AnchorMax = $"{BodyXMax:0.000} {BodyYMax:0.000}" }
            }, UiName + ".Panel", UiName + ".Body");

            if (state.OpenPackageId.HasValue)
            {
                DrawPackageDetail(elements, null, state, AllPackages().FirstOrDefault(p => p.id == state.OpenPackageId.Value));
            }
            else
            {
                DrawPackageGrid(elements, state);
            }
        }

        private void DrawBasketHeader(CuiElementContainer elements, BuyUiState state)
        {
            var title = _serverInfo?.account?.name;
            if (string.IsNullOrEmpty(title)) title = "Tebex";
            elements.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 22, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0", AnchorMax = "0.90 1" }
            }, UiName + ".Header");

            elements.Add(new CuiButton
            {
                Button = { Color = ColorRedAccent, Command = "tebex.ui close" },
                RectTransform = { AnchorMin = $"{1f - PanelPadX - CloseBtnW:0.000} 0.18", AnchorMax = $"{1f - PanelPadX:0.000} 0.82" },
                Text = { Text = "×", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, UiName + ".Header");
        }

        private void DrawFooter(CuiElementContainer elements, BuyUiState state, bool checkoutContext)
        {
            const float footerBtnW = 0.18f;
            const float footerCtaW = 0.17f;
            var ctaXMin = 1f - PanelPadX - footerCtaW;

            elements.Add(new CuiButton
            {
                Button = { Color = ColorNavBtn, Command = "tebex.ui webstore" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0.20", AnchorMax = $"{PanelPadX + footerBtnW:0.000} 0.80" },
                Text = { Text = "VISIT WEBSTORE", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, UiName + ".Footer");

            if (checkoutContext)
            {
                elements.Add(new CuiButton
                {
                    Button = { Color = ColorBlueCta, Command = "tebex.ui view store" },
                    RectTransform = { AnchorMin = $"{ctaXMin:0.000} 0.20", AnchorMax = $"{1f - PanelPadX:0.000} 0.80" },
                    Text = { Text = "BACK TO STORE", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
                }, UiName + ".Footer");
                return;
            }

            if (!_config.EnableBasket) return;

            int itemCount = state.Basket?.packages?.Sum(bp => Math.Max(1, bp.in_basket?.quantity ?? 1)) ?? 0;
            var sym = _serverInfo?.account?.currency?.symbol ?? "$";
            var iso = _serverInfo?.account?.currency?.iso4217 ?? "";
            float total = state.Basket?.total_price ?? 0f;
            var pillTop = $"{itemCount} ITEM{(itemCount == 1 ? "" : "S")}";
            var pillBot = string.IsNullOrEmpty(iso) ? $"{sym}{total:0.00}" : $"{sym}{total:0.00} {iso}";

            var pillXMax = ctaXMin - 0.01f;
            elements.Add(new CuiLabel
            {
                Text = { Text = pillTop, FontSize = 11, Align = TextAnchor.LowerRight, Color = ColorTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.60 0.50", AnchorMax = $"{pillXMax:0.000} 0.86" }
            }, UiName + ".Footer");
            elements.Add(new CuiLabel
            {
                Text = { Text = pillBot, FontSize = 12, Align = TextAnchor.UpperRight, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.60 0.14", AnchorMax = $"{pillXMax:0.000} 0.50" }
            }, UiName + ".Footer");

            elements.Add(new CuiButton
            {
                Button = { Color = ColorBlueCta, Command = "tebex.ui view basket" },
                RectTransform = { AnchorMin = $"{ctaXMin:0.000} 0.20", AnchorMax = $"{1f - PanelPadX:0.000} 0.80" },
                Text = { Text = "BASKET", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, UiName + ".Footer");
        }

        private void DrawPackageGrid(CuiElementContainer elements, BuyUiState state)
        {
            var category = (_categories ?? new List<Tebex.Headless.Category>()).FirstOrDefault(c => c.id == state.CategoryId)
                           ?? (_categories ?? new List<Tebex.Headless.Category>()).FirstOrDefault();
            if (category == null)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "No packages available.", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, UiName + ".Body");
                return;
            }
            state.CategoryId = category.id;

            var packages = (category.packages ?? new List<Tebex.Headless.Package>()).ToList();

            if (packages.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = $"There are no available products in {category.name}", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = ColorTextDim },
                    RectTransform = { AnchorMin = "0.05 0", AnchorMax = "0.95 1" }
                }, UiName + ".Body");
                return;
            }

            const float RefH = 720f;
            float RefW = RefH * ScreenAspect;

            const float panelWFrac = BuyPanelWFrac, panelHFrac = BuyPanelHFrac;
            const float bodyWFrac = BodyXMax - GridBodyXMin, bodyHFrac = BodyYMax - BodyYMin;
            float viewportPxW = bodyWFrac * panelWFrac * RefW - ScrollbarPx;
            float viewportPxH = bodyHFrac * panelHFrac * RefH;

            const int cols = 3;
            const float gutterX = 0.02f;
            float cellW = (1f - gutterX * (cols + 1)) / cols;
            float cardPxW = cellW * viewportPxW;
            float cardPxH = cardPxW * (0.426f / 0.42f);
            const float rowGapPx = 14f;
            float padTopPx = gutterX * viewportPxW, padBotPx = padTopPx;
            float rowPitchPx = cardPxH + rowGapPx;

            int nRows = (int)Math.Ceiling(packages.Count / (double)cols);
            float contentPx = padTopPx + nRows * cardPxH + Math.Max(0, nRows - 1) * rowGapPx + padBotPx;
            float extra = Math.Max(0f, contentPx - viewportPxH);
            float H = viewportPxH + extra;

            const string scrollName = UiName + ".Grid";
            elements.Add(new CuiElement
            {
                Name = scrollName,
                Parent = UiName + ".Body",
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Horizontal = false,
                        Vertical = true,
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        Inertia = true,
                        DecelerationRate = 0.135f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 0", AnchorMax = "1 1",
                            OffsetMin = $"0 {-extra:0}", OffsetMax = $"{-ScrollbarPx:0} 0"
                        },
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = false, Size = ScrollbarPx, HandleColor = ColorScrollHandle, TrackColor = "0 0 0 0"
                        }
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });

            for (int i = 0; i < packages.Count; i++)
            {
                var p = packages[i];
                int row = i / cols, col = i % cols;
                float xMin = gutterX + col * (cellW + gutterX);
                float xMax = xMin + cellW;
                float topPx = padTopPx + row * rowPitchPx;
                float yMax = 1f - topPx / H;
                float yMin = 1f - (topPx + cardPxH) / H;
                var cardName = $"{UiName}.Pkg{p.id}";

                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorCardBg },
                    RectTransform = { AnchorMin = $"{xMin:0.0000} {yMin:0.0000}", AnchorMax = $"{xMax:0.0000} {yMax:0.0000}" }
                }, scrollName, cardName);

                const string ImgSquareMin = "0.287 0.54", ImgSquareMax = "0.713 0.96";
                if (IsDecodableImageUrl(p.image))
                {
                    elements.Add(new CuiElement
                    {
                        Parent = cardName,
                        Components =
                        {
                            new CuiRawImageComponent { Url = p.image, Sprite = "assets/content/textures/generic/fulltransparent.tga" },
                            new CuiRectTransformComponent { AnchorMin = ImgSquareMin, AnchorMax = ImgSquareMax }
                        }
                    });
                }
                else
                {
                    elements.Add(new CuiPanel
                    {
                        Image = { Color = "0.10 0.10 0.12 1" },
                        RectTransform = { AnchorMin = ImgSquareMin, AnchorMax = ImgSquareMax }
                    }, cardName);
                }

                var displayName = Truncate(p.name ?? "", 48).ToUpperInvariant();
                elements.Add(new CuiLabel
                {
                    Text = { Text = displayName, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.50" }
                }, cardName);

                elements.Add(new CuiLabel
                {
                    Text = { Text = FormatPrice(p), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = ColorPriceGreen, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = "0.05 0.22", AnchorMax = "0.95 0.34" }
                }, cardName);

                BuildPackageCta(state, p.id, out var gridCtaText, out var gridCtaCmd, out var gridCtaColor);
                elements.Add(new CuiButton
                {
                    Button = { Color = gridCtaColor, Command = gridCtaCmd },
                    RectTransform = { AnchorMin = "0.06 0.06", AnchorMax = "0.94 0.20" },
                    Text = { Text = gridCtaText, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
                }, cardName);

                elements.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = $"tebex.ui pkg {p.id}" },
                    RectTransform = { AnchorMin = "0.05 0.35", AnchorMax = "0.95 0.96" },
                    Text = { Text = "" }
                }, cardName);
            }
        }

        private void DrawPackageDetail(CuiElementContainer elements, BasePlayer player, BuyUiState state, Tebex.Headless.Package p)
        {
            if (p == null)
            {
                state.OpenPackageId = null;
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Package not found.", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.4 0.4 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, UiName + ".Body");
                return;
            }

            const float detailBodyWFrac = BodyXMax - BodyXMin, detailBodyHFrac = BodyYMax - BodyYMin;
            const float padX = PanelPadX / detailBodyWFrac;
            const float padY = PanelPadY / detailBodyHFrac;

            const float navYMin = 0.905f, navYMax = 0.99f;
            const float cardYMax  = navYMin - padY;
            const float titleYMax = cardYMax - padY,   titleYMin = titleYMax - 0.075f;
            const float priceYMax = titleYMin - 0.008f, priceYMin = priceYMax - 0.060f;
            const float descYMax  = priceYMin - 0.015f, descYMin  = descYMax  - 0.165f;
            const float ctaYMax   = descYMin - 0.028f,  ctaYMin   = ctaYMax   - 0.067f;
            const float cardYMin  = ctaYMin - padY;
            const float textXMin  = 0.38f,  textXMax = 1f - padX;

            const string detailNav = UiName + ".DetailNav";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorNavBtn },
                RectTransform =
                {
                    AnchorMin = $"{(0f - BodyXMin) / detailBodyWFrac:0.0000} {navYMin:0.000}",
                    AnchorMax = $"{(1f - BodyXMin) / detailBodyWFrac:0.0000} {navYMax:0.000}"
                }
            }, UiName + ".Body", detailNav);

            elements.Add(new CuiLabel
            {
                Text = { Text = "«", FontSize = 17, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0", AnchorMax = $"{PanelPadX + 0.020f:0.000} 1" }
            }, detailNav);
            elements.Add(new CuiLabel
            {
                Text = { Text = "BACK TO STORE", FontSize = 13, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX + 0.023f:0.000} 0", AnchorMax = "0.55 1" }
            }, detailNav);
            elements.Add(new CuiButton
            {
                Button = { Color = "0 0 0 0", Command = "tebex.ui back" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                Text = { Text = "" }
            }, detailNav);

            const string detailCard = UiName + ".DetailCard";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorCardBg },
                RectTransform = { AnchorMin = $"0 {cardYMin:0.0000}", AnchorMax = $"1 {cardYMax:0.0000}" }
            }, UiName + ".Body", detailCard);

            const float panelWFrac = BuyPanelWFrac, panelHFrac = BuyPanelHFrac;
            float detailBodyAspect = (detailBodyWFrac * panelWFrac) / (detailBodyHFrac * panelHFrac) * ScreenAspect;
            SquareInRect(detailBodyAspect, padX, 0.40f, 0.35f, titleYMax,
                out float imX0, out float imY0, out float imX1, out float imY1);
            var detailImgMin = $"{imX0:0.0000} {imY0:0.0000}";
            var detailImgMax = $"{imX1:0.0000} {imY1:0.0000}";
            if (IsDecodableImageUrl(p.image))
            {
                elements.Add(new CuiElement
                {
                    Parent = UiName + ".Body",
                    Components =
                    {
                        new CuiRawImageComponent { Url = p.image, Sprite = "assets/content/textures/generic/fulltransparent.tga" },
                        new CuiRectTransformComponent { AnchorMin = detailImgMin, AnchorMax = detailImgMax }
                    }
                });
            }
            else
            {
                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorPanelBg },
                    RectTransform = { AnchorMin = detailImgMin, AnchorMax = detailImgMax }
                }, UiName + ".Body");
            }

            elements.Add(new CuiLabel
            {
                Text = { Text = Truncate(p.name ?? "", 64).ToUpperInvariant(), FontSize = 22, Align = TextAnchor.UpperLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{textXMin:0.0000} {titleYMin:0.0000}", AnchorMax = $"{textXMax:0.0000} {titleYMax:0.0000}" }
            }, UiName + ".Body");

            elements.Add(new CuiLabel
            {
                Text = { Text = FormatPrice(p), FontSize = 18, Align = TextAnchor.UpperLeft, Color = ColorPriceGreen, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{textXMin:0.0000} {priceYMin:0.0000}", AnchorMax = $"{textXMax:0.0000} {priceYMax:0.0000}" }
            }, UiName + ".Body");

            elements.Add(new CuiLabel
            {
                Text = { Text = StripHtml(p.description ?? ""), FontSize = 13, Align = TextAnchor.UpperLeft, Color = ColorTextDim },
                RectTransform = { AnchorMin = $"{textXMin:0.0000} {descYMin:0.0000}", AnchorMax = $"{textXMax:0.0000} {descYMax:0.0000}" }
            }, UiName + ".Body");

            BuildPackageCta(state, p.id, out var detailCtaText, out var detailCtaCmd, out var detailCtaColor);
            elements.Add(new CuiButton
            {
                Button = { Color = detailCtaColor, Command = detailCtaCmd },
                RectTransform = { AnchorMin = $"{textXMin:0.0000} {ctaYMin:0.0000}", AnchorMax = $"{textXMin + 0.25f:0.0000} {ctaYMax:0.0000}" },
                Text = { Text = detailCtaText, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, UiName + ".Body");
        }

        private readonly Dictionary<string, Tebex.QR.QrCode> _qrCache = new Dictionary<string, Tebex.QR.QrCode>();

        private Tebex.QR.QrCode TryGetQr(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_qrCache.TryGetValue(url, out var cached)) return cached;
            try
            {
                var qr = Tebex.QR.QrCode.Encode(url);
                _qrCache[url] = qr;
                return qr;
            }
            catch (Exception)
            {
                _qrCache[url] = null;
                return null;
            }
        }

        private const float ScreenAspect = 16f / 9f;

        private const float BuyPanelWFrac = 0.88f - 0.12f;
        private const float BuyPanelHFrac = 0.94f - 0.06f;
        private const float BuyPanelPixelAspect = ScreenAspect * (BuyPanelWFrac / BuyPanelHFrac);

        private static void SquareInRect(float parentAspect, float xMin, float yMin, float xMax, float yMax,
            out float sxMin, out float syMin, out float sxMax, out float syMax)
        {
            float availWpx = (xMax - xMin) * parentAspect;
            float availHpx = (yMax - yMin);
            float sidePx = Math.Min(availWpx, availHpx);
            float sqW = sidePx / parentAspect;
            float sqH = sidePx;
            float cx = (xMin + xMax) / 2f;
            float cy = (yMin + yMax) / 2f;
            sxMin = cx - sqW / 2f; sxMax = cx + sqW / 2f;
            syMin = cy - sqH / 2f; syMax = cy + sqH / 2f;
        }

        private void DrawQrCode(CuiElementContainer elements, string parent, string anchorMin, string anchorMax, Tebex.QR.QrCode qr, float parentAspect)
        {
            if (qr == null) return;
            var modules = qr.GetModules();
            int size = modules.GetLength(0);
            const int quietZone = 4;
            int totalUnits = size + quietZone * 2;

            ParseAnchor(anchorMin, out float xMin, out float yMin);
            ParseAnchor(anchorMax, out float xMax, out float yMax);

            SquareInRect(parentAspect, xMin, yMin, xMax, yMax, out float bxMin, out float byMin, out float bxMax, out float byMax);

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorTextWhite },
                RectTransform = { AnchorMin = $"{bxMin:0.0000} {byMin:0.0000}", AnchorMax = $"{bxMax:0.0000} {byMax:0.0000}" }
            }, parent);

            float unitX = (bxMax - bxMin) / totalUnits;
            float unitY = (byMax - byMin) / totalUnits;

            for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (!modules[r, c]) continue;
                float modXMin = bxMin + (c + quietZone) * unitX;
                float modYMax = byMax - (r + quietZone) * unitY;
                float modXMax = modXMin + unitX;
                float modYMin = modYMax - unitY;
                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorBlack },
                    RectTransform =
                    {
                        AnchorMin = $"{modXMin:0.0000} {modYMin:0.0000}",
                        AnchorMax = $"{modXMax:0.0000} {modYMax:0.0000}",
                        OffsetMin = "0 0",
                        OffsetMax = "1 1"
                    }
                }, parent);
            }
        }

        private static void ParseAnchor(string anchor, out float x, out float y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(anchor)) return;
            var parts = anchor.Split(' ');
            if (parts.Length < 2) return;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
        }

        private const float CardXMin = PanelPadX, CardXMax = 1f - PanelPadX;
        private const float CardWFrac = CardXMax - CardXMin;
        private const float BasketCardYMin = 0.479f, BasketCardYMax = BodyYMax - PanelPadY;
        private const float CheckoutCardYMin = 0.100f, CheckoutCardYMax = 0.473f;

        private const float BasketRowFracOfCard = 0.211f;
        private const float BasketRowsXMin = 0.024f, BasketRowsXMax = 0.977f;
        private const float BasketRowsYMin = 0.205f, BasketRowsYMax = 0.851f;

        private void DrawBasketView(BasePlayer player, BuyUiState state, CuiElementContainer elements)
        {
            var packages = state.Basket?.packages ?? new List<Tebex.Headless.BasketPackage>();

            if (packages.Count == 0)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = "Your basket is empty.", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = ColorTextDim },
                    RectTransform = { AnchorMin = "0.20 0.45", AnchorMax = "0.80 0.55" }
                }, UiName + ".Panel");
                return;
            }

            const string basketCardName = UiName + ".BasketCard";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorCardBg },
                RectTransform =
                {
                    AnchorMin = $"{CardXMin:0.0000} {BasketCardYMin:0.0000}",
                    AnchorMax = $"{CardXMax:0.0000} {BasketCardYMax:0.0000}"
                }
            }, UiName + ".Panel", basketCardName);

            elements.Add(new CuiLabel
            {
                Text = { Text = "YOUR BASKET", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 0.98" }
            }, basketCardName);
            elements.Add(new CuiButton
            {
                Button = { Color = ColorNavBtn, Command = "tebex.ui clearbasket" },
                RectTransform = { AnchorMin = "0.828 0.875", AnchorMax = "0.977 0.965" },
                Text = { Text = "CLEAR BASKET", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = ColorTextDim, Font = "robotocondensed-bold.ttf" }
            }, basketCardName);

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorDivider },
                RectTransform = { AnchorMin = "0.024 0.851", AnchorMax = "0.977 0.853" }
            }, basketCardName);

            var sym = _serverInfo?.account?.currency?.symbol ?? "$";
            var iso = _serverInfo?.account?.currency?.iso4217 ?? "";

            const float RefH = 720f;
            const float RefW = RefH * ScreenAspect;
            const float basketCardHFrac = BasketCardYMax - BasketCardYMin;
            float cardPxW = CardWFrac * BuyPanelWFrac * RefW;
            float cardPxH = basketCardHFrac * BuyPanelHFrac * RefH;
            float viewportPxW = (BasketRowsXMax - BasketRowsXMin) * cardPxW;
            float viewportPxH = (BasketRowsYMax - BasketRowsYMin) * cardPxH;
            float rowPxH = BasketRowFracOfCard * cardPxH;

            float contentPx = packages.Count * rowPxH;
            float extra = Math.Max(0f, contentPx - viewportPxH);
            float H = viewportPxH + extra;
            bool overflows = extra > 0.5f;

            float rowAspect = viewportPxW / rowPxH;
            const float thumbYMin = 0.225f, thumbYMax = 0.775f, thumbXMin = 0.033f;
            float thumbXMax = thumbXMin + (thumbYMax - thumbYMin) / rowAspect;
            string thumbAnchorMin = $"{thumbXMin:0.0000} {thumbYMin:0.0000}";
            string thumbAnchorMax = $"{thumbXMax:0.0000} {thumbYMax:0.0000}";

            const string basketScrollName = basketCardName + ".Rows";
            elements.Add(new CuiElement
            {
                Name = basketScrollName,
                Parent = basketCardName,
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Horizontal = false,
                        Vertical = true,
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        Inertia = true,
                        DecelerationRate = 0.135f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 0", AnchorMax = "1 1",
                            OffsetMin = $"0 {-extra:0}", OffsetMax = "0 0"
                        },
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = false, Size = 5f,
                            HandleColor = overflows ? ColorScrollHandle : "0 0 0 0",
                            TrackColor = "0 0 0 0"
                        }
                    },
                    new CuiRectTransformComponent { AnchorMin = "0.024 0.205", AnchorMax = "0.977 0.851" }
                }
            });

            for (int i = 0; i < packages.Count; i++)
            {
                var bp = packages[i];
                float topPx = i * rowPxH;
                float yMax = 1f - topPx / H;
                float yMin = 1f - (topPx + rowPxH) / H;
                var rowName = $"{basketCardName}.Row{i}";

                elements.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0" },
                    RectTransform = { AnchorMin = $"0 {yMin:0.0000}", AnchorMax = $"1 {yMax:0.0000}" }
                }, basketScrollName, rowName);

                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorDivider },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.02" }
                }, rowName);

                var imgUrl = AllPackages().FirstOrDefault(p => p.id == bp.id)?.image;
                if (IsDecodableImageUrl(imgUrl))
                {
                    elements.Add(new CuiElement
                    {
                        Parent = rowName,
                        Components =
                        {
                            new CuiRawImageComponent { Url = imgUrl, Sprite = "assets/content/textures/generic/fulltransparent.tga" },
                            new CuiRectTransformComponent { AnchorMin = thumbAnchorMin, AnchorMax = thumbAnchorMax }
                        }
                    });
                }
                else
                {
                    elements.Add(new CuiPanel
                    {
                        Image = { Color = ColorPanelBg },
                        RectTransform = { AnchorMin = thumbAnchorMin, AnchorMax = thumbAnchorMax }
                    }, rowName);
                }

                var nameText = Truncate(bp.name ?? "", 48).ToUpperInvariant();
                elements.Add(new CuiLabel
                {
                    Text = { Text = nameText, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = "0.15 0", AnchorMax = "0.85 1" }
                }, rowName);

                int qty = bp.in_basket?.quantity ?? 1;
                if (qty < 1) qty = 1;
                if (qty > 1)
                {
                    elements.Add(new CuiLabel
                    {
                        Text = { Text = $"×{qty}", FontSize = 12, Align = TextAnchor.MiddleRight, Color = ColorTextDim },
                        RectTransform = { AnchorMin = "0.70 0", AnchorMax = "0.775 1" }
                    }, rowName);
                }

                float lineTotal = (bp.in_basket?.price ?? 0f) * qty;
                var priceStr = string.IsNullOrEmpty(iso) ? $"{sym}{lineTotal:0.00}" : $"{sym}{lineTotal:0.00} {iso}";
                elements.Add(new CuiLabel
                {
                    Text = { Text = priceStr, FontSize = 13, Align = TextAnchor.MiddleRight, Color = ColorTextDim },
                    RectTransform = { AnchorMin = "0.78 0", AnchorMax = "0.905 1" }
                }, rowName);

                elements.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = $"tebex.ui remove {bp.id}" },
                    RectTransform = { AnchorMin = "0.925 0.28", AnchorMax = "0.975 0.72" },
                    Text = { Text = "×", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = ColorTextDim }
                }, rowName);
            }

            elements.Add(new CuiLabel
            {
                Text = { Text = "Total:", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.024 0.100", AnchorMax = "0.45 0.190" }
            }, basketCardName);
            elements.Add(new CuiLabel
            {
                Text = { Text = "Final price may vary based on your local taxes.", FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColorTextDim },
                RectTransform = { AnchorMin = "0.024 0.045", AnchorMax = "0.60 0.125" }
            }, basketCardName);

            float total = state.Basket?.total_price ?? 0f;
            var totalStr = string.IsNullOrEmpty(iso) ? $"{sym}{total:0.00}" : $"{sym}{total:0.00} {iso}";
            elements.Add(new CuiLabel
            {
                Text = { Text = totalStr, FontSize = 20, Align = TextAnchor.MiddleRight, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.45 0.090", AnchorMax = "0.977 0.200" }
            }, basketCardName);

            const string checkoutCardName = UiName + ".CheckoutCard";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorCardBg },
                RectTransform =
                {
                    AnchorMin = $"{CardXMin:0.0000} {CheckoutCardYMin:0.0000}",
                    AnchorMax = $"{CardXMax:0.0000} {CheckoutCardYMax:0.0000}"
                }
            }, UiName + ".Panel", checkoutCardName);

            elements.Add(new CuiLabel
            {
                Text = { Text = "CHECKOUT", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 0.99" }
            }, checkoutCardName);

            const float checkoutCardHFrac = CheckoutCardYMax - CheckoutCardYMin;
            float checkoutCardAspect = (CardWFrac / checkoutCardHFrac) * BuyPanelPixelAspect;

            const string QrBandMin = "0.22 0.07", QrBandMax = "0.43 0.80";

            var checkoutUrl = state.Basket?.links?.checkout;
            var qr = TryGetQr(checkoutUrl);
            if (qr != null)
            {
                DrawQrCode(elements, checkoutCardName, QrBandMin, QrBandMax, qr, checkoutCardAspect);
            }
            else
            {
                SquareInRect(checkoutCardAspect, 0.22f, 0.07f, 0.43f, 0.80f,
                    out float fbXMin, out float fbYMin, out float fbXMax, out float fbYMax);
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.05 0.05 0.07 1" },
                    RectTransform = { AnchorMin = $"{fbXMin:0.0000} {fbYMin:0.0000}", AnchorMax = $"{fbXMax:0.0000} {fbYMax:0.0000}" }
                }, checkoutCardName, checkoutCardName + ".QrFallback");
                elements.Add(new CuiLabel
                {
                    Text = { Text = "QR unavailable\n(URL too long)", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextDim },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, checkoutCardName + ".QrFallback");
            }

            elements.Add(new CuiLabel
            {
                Text = { Text = "Scan the QR code to checkout.", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.47 0.600", AnchorMax = "0.775 0.700" }
            }, checkoutCardName);
            elements.Add(new CuiLabel
            {
                Text = { Text = "You can also click the button below\nto receive a clickable link.", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite },
                RectTransform = { AnchorMin = "0.47 0.440", AnchorMax = "0.775 0.600" }
            }, checkoutCardName);

            elements.Add(new CuiButton
            {
                Button = { Color = ColorGreenCta, Command = "tebex.ui checkout" },
                RectTransform = { AnchorMin = "0.490 0.200", AnchorMax = "0.754 0.320" },
                Text = { Text = "GET CHECKOUT LINK", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, checkoutCardName);
        }

        private void OpenDebugUi(BasePlayer player)
        {
            if (player == null) return;
            _debugView[player.userID] = DebugView.Main;
            CuiHelper.DestroyUi(player, TelemetryUiName);
            CuiHelper.DestroyUi(player, CommandRefUiName);
            DrawDebugUi(player);
            if (_debugUiRefresh.TryGetValue(player.userID, out var existing)) existing?.Destroy();
            _debugUiRefresh[player.userID] = null;
            ScheduleDebugUiRefresh(player);
        }

        private void ScheduleDebugUiRefresh(BasePlayer player)
        {
            var uid = player.userID;
            if (!_debugUiRefresh.ContainsKey(uid)) return;
            _debugUiRefresh[uid] = timer.In(1f, () =>
            {
                if (!_debugUiRefresh.ContainsKey(uid)) return;
                if (player == null || !player.IsConnected) { DestroyDebugUi(player); return; }
                try
                {
                    var view = _debugView.TryGetValue(uid, out var v) ? v : DebugView.Main;
                    if (view == DebugView.Telemetry) RefreshTelemetryUiData(player);
                    else RefreshDebugUiData(player);
                }
                catch (Exception e) { PrintWarning($"Debug UI refresh failed: {e.Message}"); }
                ScheduleDebugUiRefresh(player);
            });
        }

        private void DestroyDebugUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, DebugUiName);
            CuiHelper.DestroyUi(player, TelemetryUiName);
            CuiHelper.DestroyUi(player, CommandRefUiName);
            _commandRefFeedback.Remove(player.userID);
            _debugView.Remove(player.userID);
            _debugActionFeedback.Remove(player.userID);
            if (_debugUiRefresh.TryGetValue(player.userID, out var t))
            {
                t?.Destroy();
                _debugUiRefresh.Remove(player.userID);
            }
        }

        private void DrawDebugUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, DebugUiName);
            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, UiOverlay, DebugUiName);

            var panel = DebugUiName + ".Panel";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthPanelBg },
                RectTransform = { AnchorMin = HealthPanelAnchorMin, AnchorMax = HealthPanelAnchorMax }
            }, DebugUiName, panel);

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthAccent },
                RectTransform = { AnchorMin = $"0 {HcAccentYMin:0.000}", AnchorMax = "1 1" }
            }, panel);
            elements.Add(new CuiLabel
            {
                Text = { Text = "Plugin Health Check", FontSize = 25, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0.930", AnchorMax = $"0.70 {HcHeaderBtnYMax:0.000}" }
            }, panel);

            float closeX0 = 1f - PanelPadX - HcHeaderBtnW;
            float refreshX1 = closeX0 - 0.010f;
            elements.Add(new CuiButton
            {
                Button = { Color = ColorHealthIconBtn, Command = "tebex.dbg refresh" },
                RectTransform = { AnchorMin = $"{refreshX1 - HcHeaderBtnW * 2f:0.000} {HcHeaderBtnYMin:0.000}", AnchorMax = $"{refreshX1:0.000} {HcHeaderBtnYMax:0.000}" },
                Text = { Text = "REFRESH", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, panel);
            elements.Add(new CuiButton
            {
                Button = { Color = ColorHealthAccent, Command = "tebex.dbg close" },
                RectTransform = { AnchorMin = $"{closeX0:0.000} {HcHeaderBtnYMin:0.000}", AnchorMax = $"{1f - PanelPadX:0.000} {HcHeaderBtnYMax:0.000}" },
                Text = { Text = "×", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, panel);

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthDivider },
                RectTransform = { AnchorMin = $"0 {HcDividerYMin:0.000}", AnchorMax = $"1 {HcDividerYMax:0.000}" }
            }, panel);

            var serverName = _serverInfo?.server?.name ?? "-";
            var accountName = _serverInfo?.account?.name ?? "-";
            elements.Add(new CuiLabel
            {
                Text = { Text = $"{Truncate(serverName, 40).ToUpperInvariant()}  |  {Truncate(accountName, 40).ToUpperInvariant()}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorHealthTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} {HcSubtitleYMin:0.000}", AnchorMax = $"{1f - PanelPadX:0.000} {HcSubtitleYMax:0.000}" }
            }, panel);

            foreach (var spec in HealthCards) DrawHealthCard(elements, spec);

            DrawDebugButton(elements, "Force Queue Check", "tebex.dbg forcecheck",     HealthTileStyle.Primary,     0, 0, "Run deliverables now");
            DrawDebugButton(elements, "Refresh Store",     "tebex.dbg refreshlisting", HealthTileStyle.Primary,     0, 1, "Reload package listing");
            DrawDebugButton(elements, "View Telemetry",    "tebex.dbg viewtelemetry",  HealthTileStyle.Primary,     1, 0, "Open logs & joins page");
            DrawDebugButton(elements, "Toggle Debug Log",  "tebex.dbg toggledebug",    HealthTileStyle.Neutral,     1, 1, "Verbose server logs");
            DrawDebugButton(elements, "Clear Telemetry",   "tebex.dbg cleartelemetry", HealthTileStyle.Destructive, 2, 0, "Flush queue log");
            DrawDebugButton(elements, "Clear Joins",       "tebex.dbg clearjoins",     HealthTileStyle.Destructive, 2, 1, "Flush join events");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Actions post results to your chat and the Telemetry & Joins page", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = ColorHealthTextDim },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} {HcFooterYMin:0.000}", AnchorMax = $"0.55 {HcFooterYMax:0.000}" }
            }, panel);

            AddDebugUiData(elements, player.userID);

            SendUi(player, elements);
        }

        private const int DebugHookRowCount = 6;

        private const int HealthPillCount = 3;

        private static readonly (string Key, string Title, int Col, int Band, int Rows, string[]? Cols)[] HealthCards =
        {
            ("Timers",    "TIMERS",           0, 0, 4,                 new[] { "EVERY", "LAST", "NEXT" }),
            ("Hooks",     "HOOKS",            1, 0, DebugHookRowCount, new[] { "COUNT", "AVG", "MAX" }),
            ("Telemetry", "TELEMETRY QUEUED", 0, 1, 3,                 null),
            ("Joins",     "JOINS / COMMANDS", 1, 1, 3,                 null),
        };

        private static readonly (float YMin, float YMax)[] HealthCardBands =
        {
            (0.535f, 0.793f),
            (0.348f, 0.509f),
        };

        private string HealthCardName(string key) => $"{DebugUiName}.Card.{key}";
        private string HealthRowName(string key, int index) => $"{HealthCardName(key)}.Row{index}";
        private string HealthPillName(int index) => $"{DebugUiName}.Pill{index}";

        private static void HealthRowBounds(int band, int rows, int index, out float yMin, out float yMax)
        {
            float cardH = HealthCardBands[band].YMax - HealthCardBands[band].YMin;
            float top = 1f - HcCardHeaderH / cardH;
            float bottom = HcCardPadB / cardH;
            float rowH = (top - bottom) / rows;
            yMax = top - index * rowH;
            yMin = yMax - rowH;
        }

        private void DrawHealthCard(CuiElementContainer elements,
            (string Key, string Title, int Col, int Band, int Rows, string[]? Cols) spec)
        {
            var name = HealthCardName(spec.Key);
            float x0 = spec.Col == 0 ? HcCol0X0 : HcCol1X0;
            float x1 = spec.Col == 0 ? HcCol0X1 : HcCol1X1;
            var band = HealthCardBands[spec.Band];
            float cardH = band.YMax - band.YMin;

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorCardBg },
                RectTransform = { AnchorMin = $"{x0:0.000} {band.YMin:0.000}", AnchorMax = $"{x1:0.000} {band.YMax:0.000}" }
            }, DebugUiName + ".Panel", name);

            float hdrYMin = 1f - HcCardHeaderH / cardH;
            elements.Add(new CuiLabel
            {
                Text = { Text = spec.Title, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColorHealthTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcLabelX0:0.000} {hdrYMin:0.000}", AnchorMax = $"{HcLabelX1:0.000} 1" }
            }, name);

            if (spec.Cols == null) return;
            for (int i = 0; i < spec.Cols.Length && i < HcColX.Length; i++)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = spec.Cols[i], FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColorHealthTextDim, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = $"{HcColX[i]:0.000} {hdrYMin:0.000}", AnchorMax = $"{HealthColXEnd(i):0.000} 1" }
                }, name);
            }
        }

        private static float HealthColXEnd(int i) => i + 1 < HcColX.Length ? HcColX[i + 1] - 0.01f : HcColXEnd;

        private void AddHealthPill(CuiElementContainer elements, int i, string label, bool ok)
        {
            float x0 = PanelPadX + i * (HcPillUnitW + HcPillGap);
            var name = HealthPillName(i);
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"{x0:0.000} {HcPillYMin:0.000}", AnchorMax = $"{x0 + HcPillUnitW:0.000} {HcPillYMax:0.000}" }
            }, DebugUiName + ".Panel", name);

            float labelX1 = (HcPillUnitW - HcPillMarkW - HcPillInnerGap) / HcPillUnitW;
            float markX0 = (HcPillUnitW - HcPillMarkW) / HcPillUnitW;

            var labelBox = name + ".Label";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorCardBg },
                RectTransform = { AnchorMin = "0 0", AnchorMax = $"{labelX1:0.000} 1" }
            }, name, labelBox);
            elements.Add(new CuiLabel
            {
                Text = { Text = label.ToUpperInvariant(), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.04 0", AnchorMax = "0.96 1" }
            }, labelBox);

            var markBox = name + ".Mark";
            elements.Add(new CuiPanel
            {
                Image = { Color = ok ? ColorCardBg : ColorHealthBadPill },
                RectTransform = { AnchorMin = $"{markX0:0.000} 0", AnchorMax = "1 1" }
            }, name, markBox);
            elements.Add(new CuiLabel
            {
                Text = { Text = ok ? "OK" : "ERROR", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = ok ? ColorHealthGreen : ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0.04 0", AnchorMax = "0.96 1" }
            }, markBox);
        }

        private void AddTableRow(CuiElementContainer elements, string cardKey, int band, int rows, int index,
            string label, string c0, string c1, string c2, string? c2Color)
        {
            var name = HealthRowName(cardKey, index);
            HealthRowBounds(band, rows, index, out var yMin, out var yMax);
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0 {yMin:0.000}", AnchorMax = $"1 {yMax:0.000}" }
            }, HealthCardName(cardKey), name);
            elements.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcLabelX0:0.000} 0", AnchorMax = $"{HcLabelX1:0.000} 1" }
            }, name);
            var vals = new[] { c0, c1, c2 };
            var colors = new[] { ColorTextWhite, ColorTextWhite, c2Color ?? ColorHealthGreen };
            for (int i = 0; i < 3; i++)
            {
                elements.Add(new CuiLabel
                {
                    Text = { Text = vals[i] ?? "", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = colors[i], Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = $"{HcColX[i]:0.000} 0", AnchorMax = $"{HealthColXEnd(i):0.000} 1" }
                }, name);
            }
        }

        private void AddKvRow(CuiElementContainer elements, string cardKey, int band, int rows, int index,
            string label, string value, string valueColor)
        {
            var name = HealthRowName(cardKey, index);
            HealthRowBounds(band, rows, index, out var yMin, out var yMax);
            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = $"0 {yMin:0.000}", AnchorMax = $"1 {yMax:0.000}" }
            }, HealthCardName(cardKey), name);
            elements.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcLabelX0:0.000} 0", AnchorMax = $"{HcKvValueX0:0.000} 1" }
            }, name);
            elements.Add(new CuiLabel
            {
                Text = { Text = value, FontSize = 13, Align = TextAnchor.MiddleRight, Color = valueColor, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcKvValueX0:0.000} 0", AnchorMax = $"{HcKvValueX1:0.000} 1" }
            }, name);
        }

        private void AddDebugUiData(CuiElementContainer elements, ulong userId)
        {
            AddHealthPill(elements, 0, "Secret Valid", _secretValid);
            AddHealthPill(elements, 1, "Debug Log",    _config.Debug);
            AddHealthPill(elements, 2, "Telemetry",    _config.AutoReportLogs);

            AddTableRow(elements, "Timers", 0, 4, 0, "DELIVERABLE CHECK",
                $"{Math.Max(_nextCheckSeconds, 30)}s", FormatRelative(_lastQueueTickAt), FormatCountdown(_nextQueueTickAt), null);
            AddTableRow(elements, "Timers", 0, 4, 1, "JOIN/LEAVE EVENTS",
                $"{Math.Max(_config.JoinFlushSeconds, 15)}s", FormatRelative(_lastJoinFlushTickAt), FormatCountdown(_nextJoinFlushTickAt), null);
            AddTableRow(elements, "Timers", 0, 4, 2, "TELEMETRY FLUSH",
                $"{Math.Max(_config.TelemetryFlushSeconds, 30)}s", FormatRelative(_lastTelemetryTickAt), FormatCountdown(_nextTelemetryTickAt), null);
            AddTableRow(elements, "Timers", 0, 4, 3, "STORE REFRESH",
                $"{Math.Max(_config.ListingRefreshSeconds, 30)}s", FormatRelative(_lastListingTickAt), FormatCountdown(_nextListingTickAt), null);

            var hookOrder = new[]
            {
                "OnPlayerConnected",
                "OnPlayerDisconnected",
                "CmdTebex",
                "CmdBuy",
                "tebex.ui",
                "tebex.dbg",
            };
            for (int hi = 0; hi < hookOrder.Length && hi < DebugHookRowCount; hi++)
            {
                var hookName = hookOrder[hi];
                if (_hookStats.TryGetValue(hookName, out var hs) && hs.Count > 0)
                    AddTableRow(elements, "Hooks", 0, DebugHookRowCount, hi, hookName.ToUpperInvariant(),
                        $"{hs.Count}", $"{hs.AvgMs:0.0}ms", $"{hs.MaxMs:0.0}ms",
                        hs.MaxMs > HookSlowThresholdMs ? ColorHealthOrange : ColorHealthGreen);
                else
                    AddTableRow(elements, "Hooks", 0, DebugHookRowCount, hi, hookName.ToUpperInvariant(),
                        "-", "-", "-", ColorHealthTextDim);
            }

            int errCount = 0, warnCount = 0, infoCount = 0;
            foreach (var ev in _telemetryIndex.Values)
            {
                if (string.Equals(ev.Level, "ERROR", StringComparison.OrdinalIgnoreCase)) errCount += ev.Count;
                else if (string.Equals(ev.Level, "WARN", StringComparison.OrdinalIgnoreCase)) warnCount += ev.Count;
                else infoCount += ev.Count;
            }
            AddKvRow(elements, "Telemetry", 1, 3, 0, "ERROR", errCount.ToString(),  errCount  > 0 ? ColorHealthRed    : ColorTextWhite);
            AddKvRow(elements, "Telemetry", 1, 3, 1, "WARN",  warnCount.ToString(), warnCount > 0 ? ColorHealthOrange : ColorTextWhite);
            AddKvRow(elements, "Telemetry", 1, 3, 2, "OTHER", infoCount.ToString(), ColorTextWhite);

            AddKvRow(elements, "Joins", 1, 3, 0, "PENDING JOINS",      _pendingJoinEvents.Count.ToString(),  ColorTextWhite);
            AddKvRow(elements, "Joins", 1, 3, 1, "IN-FLIGHT COMMANDS", _inflightCommandIds.Count.ToString(), ColorTextWhite);
            AddKvRow(elements, "Joins", 1, 3, 2, "DELAYED COMMANDS",   _delayedCommands.Count.ToString(),    ColorTextWhite);

            var feedback = _debugActionFeedback.TryGetValue(userId, out var fb) && !string.IsNullOrEmpty(fb)
                ? fb
                : string.Empty;
            elements.Add(new CuiLabel
            {
                Text = { Text = feedback, FontSize = 10, Align = TextAnchor.MiddleRight, Color = ColorHealthTextDim },
                RectTransform = { AnchorMin = $"0.55 {HcFooterYMin:0.000}", AnchorMax = $"{1f - PanelPadX:0.000} {HcFooterYMax:0.000}" }
            }, DebugUiName + ".Panel", DebugUiName + ".ActionFeedback");
        }

        private void RefreshDebugUiData(BasePlayer player)
        {
            if (player == null) return;
            for (int i = 0; i < HealthPillCount; i++) CuiHelper.DestroyUi(player, HealthPillName(i));
            foreach (var spec in HealthCards)
                for (int i = 0; i < spec.Rows; i++)
                    CuiHelper.DestroyUi(player, HealthRowName(spec.Key, i));
            CuiHelper.DestroyUi(player, DebugUiName + ".ActionFeedback");

            var elements = new CuiElementContainer();
            AddDebugUiData(elements, player.userID);
            SendUi(player, elements);
        }

        private static readonly (string Section, string Syntax, string Description)[] CommandReferenceTable =
        {
            ("CONFIGURATION", "{admin} secret <key>",             "Set the webstore secret key"),
            ("CONFIGURATION", "{admin} info",                     "Show server and account information"),
            ("CONFIGURATION", "{admin} reload",                   "Reload configuration and listings"),

            ("QUEUE & DELIVERY", "{admin} forcecheck",            "Run a queue check right now"),
            ("QUEUE & DELIVERY", "{admin} forcecheck <user>",     "Check online commands for one cached user"),
            ("QUEUE & DELIVERY", "{admin} replay <user|all>",     "Re-apply tracked permissions after a wipe"),

            ("STORE & CHECKOUT", "{buy}",                         "Open the in-game store"),
            ("STORE & CHECKOUT", "{admin} checkout <pkgId>",      "Get an instant checkout link for yourself"),
            ("STORE & CHECKOUT", "{admin} sendlink <pkg> <user>", "Send a checkout link to a player"),
            ("STORE & CHECKOUT", "{admin} goals",                 "Show community goal progress"),

            ("USER MANAGEMENT", "{admin} ban <name> <reason> [ip]", "Ban a player from the webstore"),
            ("USER MANAGEMENT", "{admin} lookup <username>",       "Not available in this build"),

            ("DEBUG", "{admin} debug",                            "Open the Plugin Health Check page"),
            ("DEBUG", "{admin} debug <true|false>",               "Turn verbose server logging on or off"),
            ("DEBUG", "{admin} selftest",                         "Run the permission ledger self-check"),
            ("DEBUG", "{admin} help",                             "Open this command reference"),
        };

        private string FormatCommandSyntax(string syntax)
            => syntax.Replace("{admin}", "/" + _config.AdminCommand)
                     .Replace("{buy}", "/" + _config.BuyCommand);

        private static List<(string Name, int Count)> CommandReferenceSections()
        {
            var result = new List<(string Name, int Count)>();
            var seen = new Dictionary<string, int>();
            foreach (var row in CommandReferenceTable)
            {
                if (seen.TryGetValue(row.Section, out var at))
                {
                    result[at] = (row.Section, result[at].Count + 1);
                    continue;
                }
                seen[row.Section] = result.Count;
                result.Add((row.Section, 1));
            }
            return result;
        }

        private void OpenCommandReferenceUi(BasePlayer player)
        {
            if (player == null) return;
            DestroyDebugUi(player);
            DrawCommandReferenceUi(player);
        }

        private void DrawCommandReferenceUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, CommandRefUiName);
            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, UiOverlay, CommandRefUiName);

            var panel = CommandRefUiName + ".Panel";
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthPanelBg },
                RectTransform = { AnchorMin = HealthPanelAnchorMin, AnchorMax = HealthPanelAnchorMax }
            }, CommandRefUiName, panel);

            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthAccent },
                RectTransform = { AnchorMin = $"0 {HcAccentYMin:0.000}", AnchorMax = "1 1" }
            }, panel);
            elements.Add(new CuiLabel
            {
                Text = { Text = "TEBEX", FontSize = 25, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} 0.930", AnchorMax = $"0.70 {HcHeaderBtnYMax:0.000}" }
            }, panel);
            elements.Add(new CuiLabel
            {
                Text = { Text = "COMMAND REFERENCE", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ColorCmdRefSubtitle, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} {HcSubtitleYMin:0.000}", AnchorMax = $"0.70 {HcSubtitleYMax:0.000}" }
            }, panel);
            elements.Add(new CuiButton
            {
                Button = { Color = ColorHealthAccent, Command = "tebex.dbg close" },
                RectTransform = { AnchorMin = $"{1f - PanelPadX - HcHeaderBtnW:0.000} {HcHeaderBtnYMin:0.000}", AnchorMax = $"{1f - PanelPadX:0.000} {HcHeaderBtnYMax:0.000}" },
                Text = { Text = "×", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, panel);
            elements.Add(new CuiPanel
            {
                Image = { Color = ColorHealthDivider },
                RectTransform = { AnchorMin = $"0 {HcDividerYMin:0.000}", AnchorMax = $"1 {HcDividerYMax:0.000}" }
            }, panel);

            DrawCommandReferenceRows(elements, panel);

            elements.Add(new CuiLabel
            {
                Text = { Text = "Click a command to send it to your F1 console", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = ColorCmdRefSubtitle },
                RectTransform = { AnchorMin = $"{PanelPadX:0.000} {HcFooterYMin:0.000}", AnchorMax = $"0.55 {HcFooterYMax:0.000}" }
            }, panel);
            AddCommandReferenceFeedback(elements, player.userID);

            SendUi(player, elements);
        }

        private void DrawCommandReferenceRows(CuiElementContainer elements, string panel)
        {
            float viewportPxH = (CrBodyYMax - CrBodyYMin) * BuyPanelHFrac * CrRefH;

            var sections = CommandReferenceSections();
            float contentPx = CrPadTopPx + CrPadBotPx;
            foreach (var s in sections)
                contentPx += CrSectionGapPx + CrSectionPx + s.Count * (CrRowPx + CrRowGapPx);

            float extra = Math.Max(0f, contentPx - viewportPxH);
            float H = viewportPxH + extra;

            const string scrollName = CommandRefUiName + ".List";
            elements.Add(new CuiElement
            {
                Name = scrollName,
                Parent = panel,
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Horizontal = false,
                        Vertical = true,
                        MovementType = UnityEngine.UI.ScrollRect.MovementType.Clamped,
                        Inertia = true,
                        DecelerationRate = 0.135f,
                        ScrollSensitivity = 24f,
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 0", AnchorMax = "1 1",
                            OffsetMin = $"0 {-extra:0}", OffsetMax = $"{-ScrollbarPx:0} 0"
                        },
                        VerticalScrollbar = new CuiScrollbar
                        {
                            AutoHide = false, Size = ScrollbarPx, HandleColor = ColorScrollHandle, TrackColor = "0 0 0 0"
                        }
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = $"{PanelPadX:0.000} {CrBodyYMin:0.000}",
                        AnchorMax = $"{1f - PanelPadX:0.000} {CrBodyYMax:0.000}"
                    }
                }
            });

            float topPx = CrPadTopPx;
            int index = 0;
            foreach (var section in sections)
            {
                topPx += CrSectionGapPx;
                var headerName = $"{scrollName}.Sec{index}";
                elements.Add(new CuiPanel
                {
                    Image = { Color = ColorCmdRefSection },
                    RectTransform =
                    {
                        AnchorMin = $"0 {1f - (topPx + CrSectionPx) / H:0.0000}",
                        AnchorMax = $"1 {1f - topPx / H:0.0000}"
                    }
                }, scrollName, headerName);
                elements.Add(new CuiLabel
                {
                    Text = { Text = section.Name, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = ColorCmdRefSectionTx, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = $"{CrTextX0:0.000} 0", AnchorMax = "0.80 1" }
                }, headerName);
                elements.Add(new CuiLabel
                {
                    Text = { Text = section.Count.ToString(), FontSize = 11, Align = TextAnchor.MiddleRight, Color = ColorHealthTextDim, Font = "robotocondensed-bold.ttf" },
                    RectTransform = { AnchorMin = "0.80 0", AnchorMax = $"{CrTextX1:0.000} 1" }
                }, headerName);
                topPx += CrSectionPx;

                for (int i = 0; i < section.Count; i++, index++)
                {
                    var row = CommandReferenceTable[index];
                    var rowName = $"{scrollName}.Row{index}";
                    elements.Add(new CuiPanel
                    {
                        Image = { Color = ColorCmdRefRow },
                        RectTransform =
                        {
                            AnchorMin = $"0 {1f - (topPx + CrRowPx) / H:0.0000}",
                            AnchorMax = $"1 {1f - topPx / H:0.0000}"
                        }
                    }, scrollName, rowName);
                    elements.Add(new CuiLabel
                    {
                        Text = { Text = FormatCommandSyntax(row.Syntax), FontSize = 13, Align = TextAnchor.MiddleLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                        RectTransform = { AnchorMin = $"{CrRowTextX0:0.000} 0", AnchorMax = $"{CrSyntaxX1:0.000} 1" }
                    }, rowName);
                    elements.Add(new CuiLabel
                    {
                        Text = { Text = row.Description, FontSize = 11, Align = TextAnchor.MiddleRight, Color = ColorHealthTextDim },
                        RectTransform = { AnchorMin = $"{CrSyntaxX1:0.000} 0", AnchorMax = $"{CrTextX1:0.000} 1" }
                    }, rowName);
                    elements.Add(new CuiButton
                    {
                        Button = { Color = "0 0 0 0", Command = $"tebex.dbg cmd {index}" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                        Text = { Text = "" }
                    }, rowName);
                    topPx += CrRowPx + CrRowGapPx;
                }
            }
        }

        private void AddCommandReferenceFeedback(CuiElementContainer elements, ulong userId)
        {
            var text = _commandRefFeedback.TryGetValue(userId, out var fb) && !string.IsNullOrEmpty(fb) ? fb : string.Empty;
            elements.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 10, Align = TextAnchor.MiddleRight, Color = ColorHealthTextDim },
                RectTransform = { AnchorMin = $"0.55 {HcFooterYMin:0.000}", AnchorMax = $"{1f - PanelPadX:0.000} {HcFooterYMax:0.000}" }
            }, CommandRefUiName + ".Panel", CommandRefUiName + ".Feedback");
        }

        private void SendCommandReferenceRow(BasePlayer player, int index)
        {
            if (player == null) return;
            if (index < 0 || index >= CommandReferenceTable.Length) return;
            var syntax = FormatCommandSyntax(CommandReferenceTable[index].Syntax);
            player.ConsoleMessage(syntax);
            _commandRefFeedback[player.userID] = $"Sent {syntax} to your F1 console.";
            CuiHelper.DestroyUi(player, CommandRefUiName + ".Feedback");
            var elements = new CuiElementContainer();
            AddCommandReferenceFeedback(elements, player.userID);
            SendUi(player, elements);
        }

        private const int TelemetryUiMaxLinesPerSection = 12;

        private static readonly (string Name, float YMin, float YMax)[] TelemetrySectionBounds =
        {
            ("Queued",  0.62f, 0.92f),
            ("Sent",    0.32f, 0.61f),
            ("Joins",   0.02f, 0.31f),
        };

        private string TelemetryDataName(string section) => $"{TelemetryUiName}.{section}Data";

        private string TelemetryStampName() => TelemetryUiName + ".Stamp";

        private void DrawTelemetryUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, TelemetryUiName);
            var elements = new CuiElementContainer();

            elements.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.85" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, UiOverlay, TelemetryUiName);

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.12 0.12 0.14 1" },
                RectTransform = { AnchorMin = "0.10 0.08", AnchorMax = "0.90 0.92" }
            }, TelemetryUiName, TelemetryUiName + ".Panel");

            elements.Add(new CuiPanel
            {
                Image = { Color = "0.18 0.18 0.22 1" },
                RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 1" }
            }, TelemetryUiName + ".Panel", TelemetryUiName + ".Header");

            elements.Add(new CuiLabel
            {
                Text = { Text = "Tebex - Telemetry & Joins", FontSize = 18, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.7 1" }
            }, TelemetryUiName + ".Header");

            elements.Add(new CuiButton
            {
                Button = { Color = "0.55 0.55 0.2 1", Command = "tebex.dbg refresh" },
                RectTransform = { AnchorMin = "0.72 0.15", AnchorMax = "0.81 0.85" },
                Text = { Text = "Refresh", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, TelemetryUiName + ".Header");

            elements.Add(new CuiButton
            {
                Button = { Color = "0.55 0.55 0.2 1", Command = "tebex.dbg back" },
                RectTransform = { AnchorMin = "0.83 0.15", AnchorMax = "0.92 0.85" },
                Text = { Text = "< Back", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, TelemetryUiName + ".Header");

            elements.Add(new CuiButton
            {
                Button = { Color = "0.7 0.2 0.2 1", Command = "tebex.dbg close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "X", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, TelemetryUiName + ".Header");

            foreach (var s in TelemetrySectionBounds)
            {
                elements.Add(new CuiPanel
                {
                    Image = { Color = "0.08 0.08 0.1 1" },
                    RectTransform = { AnchorMin = $"0 {s.YMin:0.000}", AnchorMax = $"1 {s.YMax:0.000}" }
                }, TelemetryUiName + ".Panel", TelemetryUiName + "." + s.Name);

                var titleText = s.Name switch
                {
                    "Queued" => "Queued Telemetry",
                    "Sent"   => "Last 50 Sent Telemetry",
                    "Joins"  => "Pending Join Events",
                    _ => s.Name
                };
                elements.Add(new CuiLabel
                {
                    Text = { Text = titleText, FontSize = 13, Align = TextAnchor.MiddleLeft, Color = "0.6 0.8 1 1" },
                    RectTransform = { AnchorMin = "0.02 0.86", AnchorMax = "0.98 0.99" }
                }, TelemetryUiName + "." + s.Name);
            }

            AddTelemetryUiData(elements);

            SendUi(player, elements);
        }

        private void AddTelemetryUiData(CuiElementContainer elements)
        {
            var queuedJson = FormatJsonList(_telemetryIndex.Values, _telemetryIndex.Count, TelemetryUiMaxLinesPerSection);
            var sentJson = FormatJsonList(
                _telemetrySentHistory.Reverse(),
                _telemetrySentHistory.Count,
                TelemetryUiMaxLinesPerSection);
            var joinsJson = FormatJsonList(_pendingJoinEvents, _pendingJoinEvents.Count, TelemetryUiMaxLinesPerSection);

            AddTelemetryDataLabel(elements, "Queued", queuedJson);
            AddTelemetryDataLabel(elements, "Sent",   sentJson);
            AddTelemetryDataLabel(elements, "Joins",  joinsJson);

            elements.Add(new CuiLabel
            {
                Text = { Text = $"UPDATED {DateTime.Now:HH:mm:ss}", FontSize = 10, Align = TextAnchor.MiddleRight, Color = "0.6 0.6 0.65 1" },
                RectTransform = { AnchorMin = "0.40 0.93", AnchorMax = "0.70 1" }
            }, TelemetryUiName + ".Panel", TelemetryStampName());
        }

        private void AddTelemetryDataLabel(CuiElementContainer elements, string section, string text)
        {
            float yMin = 0.02f, yMax = 0.92f;
            for (int i = 0; i < TelemetrySectionBounds.Length; i++)
            {
                if (TelemetrySectionBounds[i].Name == section)
                {
                    yMin = TelemetrySectionBounds[i].YMin + 0.01f;
                    yMax = TelemetrySectionBounds[i].YMax - 0.10f;
                    break;
                }
            }
            elements.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 10, Align = TextAnchor.UpperLeft, Color = "0.85 0.92 1 1" },
                RectTransform = { AnchorMin = $"0.02 {yMin:0.000}", AnchorMax = $"0.98 {yMax:0.000}" }
            }, TelemetryUiName + ".Panel", TelemetryDataName(section));
        }

        private void RefreshTelemetryUiData(BasePlayer player)
        {
            if (player == null) return;
            foreach (var s in TelemetrySectionBounds)
                CuiHelper.DestroyUi(player, TelemetryDataName(s.Name));
            CuiHelper.DestroyUi(player, TelemetryStampName());

            var elements = new CuiElementContainer();
            AddTelemetryUiData(elements);
            SendUi(player, elements);
        }

        private string FormatJsonList<T>(IEnumerable<T> items, int totalCount, int maxLines)
        {
            var listed = items.Take(maxLines)
                .Select(item => JsonConvert.SerializeObject(item, Formatting.None, JsonSettings))
                .ToList();
            if (listed.Count == 0) return "(empty)";
            var sb = new StringBuilder();
            for (int i = 0; i < listed.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(listed[i]);
            }
            if (totalCount > maxLines)
            {
                sb.Append('\n');
                sb.Append($"... and {totalCount - maxLines} more");
            }
            return sb.ToString();
        }

        private enum HealthTileStyle { Primary, Neutral, Destructive }

        private void DrawDebugButton(CuiElementContainer elements, string label, string command,
            HealthTileStyle style, int row, int col, string subtitle = null)
        {
            float xMin = col == 0 ? HcCol0X0 : HcCol1X0;
            float xMax = col == 0 ? HcCol0X1 : HcCol1X1;
            float yMax = HcTilesTop - row * (HcTileH + HcTileGap);
            float yMin = yMax - HcTileH;

            string bg, bar;
            switch (style)
            {
                case HealthTileStyle.Neutral:     bg = ColorHealthTileGreyBg; bar = ColorHealthTileGreyBar; break;
                case HealthTileStyle.Destructive: bg = ColorHealthTileRedBg;  bar = ColorHealthTileRedBar;  break;
                default:                          bg = ColorHealthTileBlueBg; bar = ColorHealthTileBlueBar; break;
            }

            elements.Add(new CuiPanel
            {
                Image = { Color = bar },
                RectTransform = { AnchorMin = $"{xMin:0.000} {yMin:0.000}", AnchorMax = $"{xMin + HcTileBarW:0.000} {yMax:0.000}" }
            }, DebugUiName + ".Panel");

            var tileName = $"{DebugUiName}.Tile{row}{col}";
            elements.Add(new CuiButton
            {
                Button = { Color = bg, Command = command },
                RectTransform = { AnchorMin = $"{xMin + HcTileBarW:0.000} {yMin:0.000}", AnchorMax = $"{xMax:0.000} {yMax:0.000}" },
                Text = { Text = string.IsNullOrEmpty(subtitle) ? label.ToUpperInvariant() : "", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" }
            }, DebugUiName + ".Panel", tileName);

            if (string.IsNullOrEmpty(subtitle)) return;
            elements.Add(new CuiLabel
            {
                Text = { Text = label.ToUpperInvariant(), FontSize = 13, Align = TextAnchor.LowerLeft, Color = ColorTextWhite, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcTileTextX0:0.000} 0.46", AnchorMax = "0.98 0.88" }
            }, tileName);
            elements.Add(new CuiLabel
            {
                Text = { Text = subtitle.ToUpperInvariant(), FontSize = 10, Align = TextAnchor.UpperLeft, Color = ColorHealthTextDim, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = $"{HcTileTextX0:0.000} 0.10", AnchorMax = "0.98 0.44" }
            }, tileName);
        }

        private static string FormatRelative(DateTime t)
        {
            if (t == DateTime.MinValue) return "never";
            var d = DateTime.UtcNow - t;
            if (d.TotalSeconds < 0) return "future?";
            if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s ago";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m {(int)(d.TotalSeconds % 60)}s ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours}h {(int)(d.TotalMinutes % 60)}m ago";
            return $"{(int)d.TotalDays}d ago";
        }

        private static string FormatCountdown(DateTime t)
        {
            if (t == DateTime.MinValue) return "-";
            var d = t - DateTime.UtcNow;
            if (d.TotalSeconds <= 0) return "now";
            if (d.TotalSeconds < 60) return $"in {(int)d.TotalSeconds}s";
            if (d.TotalMinutes < 60) return $"in {(int)d.TotalMinutes}m {(int)(d.TotalSeconds % 60)}s";
            if (d.TotalHours < 24) return $"in {(int)d.TotalHours}h {(int)(d.TotalMinutes % 60)}m";
            return $"in {(int)d.TotalDays}d";
        }

        [ConsoleCommand("tebex.dbg")]
        private void CcDbg(ConsoleSystem.Arg arg) => MeasureHook("tebex.dbg", () =>
        {
            var player = arg.Player();
            if (player == null) return;
            var ip = player.IPlayer;
            if (ip == null) return;
            if (!ip.IsAdmin && !permission.UserHasPermission(ip.Id, PermAdmin)) return;
            if (arg.Args == null || arg.Args.Length == 0) { OpenDebugUi(player); return; }

            var op = ((string)arg.Args[0]).ToLowerInvariant();
            switch (op)
            {
                case "close":
                    DestroyDebugUi(player);
                    return;
                case "cmd":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var cmdIndex))
                        SendCommandReferenceRow(player, cmdIndex);
                    return;
                case "refresh":
                    RefreshActiveDebugView(player);
                    return;
                case "forcecheck":
                    if (_secretValid)
                    {
                        ip.Reply("Forcing queue check...");
                        CheckQueue(n => ip.Reply($"Queue check complete. Executed {n} command(s)."));
                    }
                    else
                    {
                        ip.Reply("Secret key not set; cannot force queue check.");
                    }
                    _debugActionFeedback[player.userID] = $"Force Queue Check fired at {DateTime.Now:HH:mm:ss}, result in your chat (F1).";
                    RefreshDebugUiData(player);
                    return;
                case "refreshlisting":
                    RefreshListing();
                    FetchCommunityGoals();
                    _debugActionFeedback[player.userID] = "Store listing refreshed - package listing reloaded.";
                    RefreshDebugUiData(player);
                    return;
                case "cleartelemetry":
                case "flushtelemetry":
                    FlushTelemetry(true);
                    _debugActionFeedback[player.userID] = "Telemetry flushed - see View Telemetry page.";
                    RefreshDebugUiData(player);
                    return;
                case "clearjoins":
                case "flushjoins":
                    FlushJoinEvents(true);
                    _debugActionFeedback[player.userID] = "Join events flushed - see View Telemetry page.";
                    RefreshDebugUiData(player);
                    return;
                case "toggledebug":
                    _config.Debug = !_config.Debug;
                    SaveConfig();
                    ip.Reply($"Debug logging is now {(_config.Debug ? "ON" : "OFF")}.");
                    _debugActionFeedback[player.userID] = $"Debug logging now {(_config.Debug ? "ON" : "OFF")}.";
                    RefreshDebugUiData(player);
                    return;
                case "viewtelemetry":
                    _debugView[player.userID] = DebugView.Telemetry;
                    CuiHelper.DestroyUi(player, DebugUiName);
                    DrawTelemetryUi(player);
                    return;
                case "back":
                    _debugView[player.userID] = DebugView.Main;
                    CuiHelper.DestroyUi(player, TelemetryUiName);
                    DrawDebugUi(player);
                    return;
            }
            RefreshActiveDebugView(player);
        });

        private void RefreshActiveDebugView(BasePlayer player)
        {
            if (_debugView.TryGetValue(player.userID, out var view) && view == DebugView.Telemetry)
                RefreshTelemetryUiData(player);
            else
                RefreshDebugUiData(player);
        }

        [ConsoleCommand("tebex.ui")]
        private void CcUi(ConsoleSystem.Arg arg) => MeasureHook("tebex.ui", () =>
        {
            var player = arg.Player();
            if (player == null) return;
            if (arg.Args == null || arg.Args.Length == 0) return;

            if (!_uiStates.TryGetValue(player.userID, out var state))
            {
                state = new BuyUiState();
                _uiStates[player.userID] = state;
            }

            var ip = player.IPlayer;
            var op = ((string)arg.Args[0]).ToLowerInvariant();
            switch (op)
            {
                case "close":
                    DestroyUi(player);
                    return;
                case "view":
                    if (arg.Args.Length >= 2)
                    {
                        var which = ((string)arg.Args[1]).ToLowerInvariant();
                        if (which == "basket" && _config.EnableBasket)
                        {
                            state.View = BuyUiView.Basket;
                            state.LastBasketError = string.Empty;
                            if (!string.IsNullOrEmpty(state.BasketIdent))
                            {
                                RefreshBasket(player, (ok, err) =>
                                {
                                    if (!ok) state.LastBasketError = err ?? "";
                                    DrawBuyUi(player);
                                });
                                return;
                            }
                        }
                        else
                        {
                            state.View = BuyUiView.Store;
                            state.OpenPackageId = null;
                        }
                    }
                    break;
                case "cat":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var cid))
                    {
                        state.CategoryId = cid;
                        state.Page = 0;
                        state.OpenPackageId = null;
                    }
                    break;
                case "page":
                    if (arg.Args.Length >= 2)
                    {
                        state.Page += arg.Args[1] == "next" ? 1 : -1;
                        if (state.Page < 0) state.Page = 0;
                    }
                    break;
                case "pkg":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var pid))
                        state.OpenPackageId = pid;
                    break;
                case "back":
                    state.OpenPackageId = null;
                    break;
                case "add":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var apid))
                    {
                        if (state.InflightAdds.Contains(apid)) return;
                        state.LastBasketError = string.Empty;
                        AddToBasket(player, apid, (ok, err) =>
                        {
                            if (!ok) state.LastBasketError = err ?? "Failed to add to basket.";
                            DrawBuyUi(player);
                        });
                        return;
                    }
                    break;
                case "remove":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var rpid))
                    {
                        state.LastBasketError = string.Empty;
                        RemoveFromBasket(player, rpid, (ok, err) =>
                        {
                            if (!ok) state.LastBasketError = err ?? "Failed to remove from basket.";
                            DrawBuyUi(player);
                        });
                        return;
                    }
                    break;
                case "checkout":
                {
                    var pkgs = state.Basket?.packages;
                    if (pkgs == null || pkgs.Count == 0)
                    {
                        ip?.Reply("Your basket is empty.");
                        return;
                    }
                    var url = state.Basket?.links?.checkout;
                    if (string.IsNullOrEmpty(url))
                    {
                        ip?.Reply("Checkout URL not available. Try reopening the basket.");
                        return;
                    }
                    DestroyUi(player);
                    SendCheckoutLink(ip, url, 0);
                    return;
                }
                case "webstore":
                {
                    var webUrl = ResolveWebstoreUrl();
                    DestroyUi(player);
                    player.ConsoleMessage(webUrl);
                    player.ChatMessage("The webstore link has been sent to your F1 console. Press F1, then click the timestamp to copy the link.");
                    return;
                }
                case "clearbasket":
                {
                    state.BasketIdent = string.Empty;
                    state.Basket = null;
                    state.LastBasketError = string.Empty;
                    state.InflightAdds.Clear();
                    state.View = BuyUiView.Store;
                    state.OpenPackageId = null;
                    DrawBuyUi(player);
                    return;
                }
                case "buy":
                    if (arg.Args.Length >= 2 && int.TryParse(arg.Args[1], out var bpid))
                    {
                        DestroyUi(player);
                        CreateCheckout(bpid, player.displayName, (ok, url, err) =>
                        {
                            var ipInner = player.IPlayer;
                            if (!ok) { ipInner?.Reply($"Checkout failed: {err}"); return; }
                            SendCheckoutLink(ipInner, url, bpid);
                        });
                        return;
                    }
                    break;
            }
            DrawBuyUi(player);
        });

        private string FormatPrice(Tebex.Headless.Package p)
        {
            var sym = _serverInfo?.account?.currency?.symbol ?? "$";
            if (p.discount > 0f && p.discount < p.base_price)
            {
                return $"<color=#999999><s>{sym}{p.base_price:0.00}</s></color>  {sym}{p.total_price:0.00}";
            }
            return $"{sym}{p.total_price:0.00}";
        }

        private string ResolveWebstoreUrl()
        {
            var domain = _serverInfo?.account?.domain;
            if (string.IsNullOrEmpty(domain)) return "https://www.tebex.io";
            return domain.StartsWith("http://") || domain.StartsWith("https://")
                ? domain
                : "https://" + domain;
        }

        private void SendCheckoutLink(IPlayer player, string url, int packageId)
        {
            if (player == null) return;
            var forPackage = packageId > 0 ? $" for package #{packageId}" : "";
            var chatMsg = $"A checkout link{forPackage} has been sent to your F1 console. Click the timestamp to copy the link.";

            if (player.Object is BasePlayer bp)
            {
                bp.ConsoleMessage(url);
                bp.ChatMessage(chatMsg);
            }
            else
            {
                player.Reply(url);
            }
        }

        #endregion

        #region Self Test

        private void HandleSelfTest(IPlayer player)
        {
            if (!RequireAdmin(player)) return;

            var passed = 0;
            var failures = new List<string>();

            void Check(string name, bool ok, string detail = "")
            {
                if (ok) { passed++; return; }
                failures.Add(detail.Length == 0 ? name : $"{name}: {detail}");
            }

            void Parse(string name, string command, bool expectMatch,
                bool expectGrant = false, string expectType = "", string expectValue = "")
            {
                var matched = TryParsePermissionGrant(command, out var grant, out var type, out var value);
                if (matched != expectMatch)
                {
                    Check(name, false, $"matched={matched}, expected {expectMatch}  [{command}]");
                    return;
                }
                if (!expectMatch) { Check(name, true); return; }
                Check(name, grant == expectGrant && type == expectType && value == expectValue,
                    $"got grant={grant} type='{type}' value='{value}', " +
                    $"expected grant={expectGrant} type='{expectType}' value='{expectValue}'  [{command}]");
            }

            Parse("grant user", "oxide.grant user 76561198000000001 vip", true, true, "permission", "vip");
            Parse("revoke user", "oxide.revoke user 76561198000000001 vip", true, false, "permission", "vip");
            Parse("o.grant alias", "o.grant user 76561198000000001 vip", true, true, "permission", "vip");
            Parse("o.revoke alias", "o.revoke user 76561198000000001 vip", true, false, "permission", "vip");
            Parse("usergroup add", "oxide.usergroup add 76561198000000001 vips", true, true, "group", "vips");
            Parse("usergroup remove", "oxide.usergroup remove 76561198000000001 vips", true, false, "group", "vips");
            Parse("o.usergroup alias", "o.usergroup add 76561198000000001 vips", true, true, "group", "vips");
            Parse("uppercase verb", "OXIDE.GRANT USER 76561198000000001 VIP", true, true, "permission", "VIP");
            Parse("extra whitespace", "  oxide.grant   user   76561198000000001   vip  ", true, true, "permission", "vip");
            Parse("tab separated", "oxide.grant\tuser\t76561198000000001\tvip", true, true, "permission", "vip");
            Parse("grant group ignored", "oxide.grant group vips someperm", false);
            Parse("revoke group ignored", "oxide.revoke group vips someperm", false);
            Parse("usergroup set ignored", "oxide.usergroup set 76561198000000001 vips", false);
            Parse("group create ignored", "oxide.group add vips", false);
            Parse("too few tokens", "oxide.grant user 76561198000000001", false);
            Parse("unrelated command", "inventory.giveto 76561198000000001 rifle.ak 1", false);
            Parse("empty command", "", false);
            Parse("whitespace command", "   ", false);
            Parse("null command", null!, false);
            Parse("quoted username", "oxide.grant user \"John Doe\" vip", true, true, "permission", "vip");
            Parse("quoted group value", "oxide.usergroup add \"John Doe\" \"vip plus\"", true, true, "group", "vip plus");
            Parse("trailing junk tolerated", "oxide.grant user 76561198000000001 vip 30d", true, true, "permission", "vip");

            Check("tokenizer keeps quoted spaces",
                TokenizeCommand("a \"b c\" d").Count == 3 && TokenizeCommand("a \"b c\" d")[1] == "b c");
            Check("LooksLikePermissionCommand positive", LooksLikePermissionCommand("oxide.grant user x y"));
            Check("LooksLikePermissionCommand negative", !LooksLikePermissionCommand("inventory.giveto x y 1"));

            var customConfig = new List<CustomPermissionCommand>
            {
                new() { Label = "vip", GrantCommand = "addvip", RevokeCommand = "delvip" },
                new() { Label = "elite", GrantCommand = "addelite", RevokeCommand = "" },
                new() { Label = "gold", GrantCommand = "addgold {id}", RevokeCommand = "delgold {id}" },
                new() { Label = "", GrantCommand = "addnothing", RevokeCommand = "delnothing" },
            };

            void Custom(string name, string command, string? expectLabel, bool expectGrant = false)
            {
                var match = MatchCustomCommand(customConfig, command, out var grant);
                if (expectLabel == null)
                {
                    Check(name, match == null, $"expected no match, got '{match?.Label}'  [{command}]");
                    return;
                }
                Check(name, match != null && match.Label == expectLabel && grant == expectGrant,
                    $"got label='{match?.Label}' grant={grant}, expected label='{expectLabel}' grant={expectGrant}  [{command}]");
            }

            Custom("custom grant", "addvip 76561198000000001", "vip", true);
            Custom("custom revoke", "delvip 76561198000000001", "vip", false);
            Custom("custom case-insensitive", "ADDVIP 76561198000000001", "vip", true);
            Custom("custom grant-only pair", "addelite 76561198000000001", "elite", true);
            Custom("custom template config", "addgold 76561198000000001", "gold", true);
            Custom("custom template revoke", "delgold 76561198000000001", "gold", false);
            Custom("custom unlabelled skipped", "addnothing 76561198000000001", null);
            Custom("custom no match", "givekit 76561198000000001 starter", null);
            Custom("custom empty command", "", null);
            Check("custom null config tolerated", MatchCustomCommand(null!, "addvip 1", out _) == null);

            const string idA = "76561198000000001", idB = "76561198000000002";
            var ledger = new Dictionary<string, PermissionLedgerRecord>();

            bool Apply(bool grant, string id, string user, string type, string value, string raw = "raw") =>
                ApplyLedgerMutation(ledger, id, user, grant, type, value, 42, raw);

            Check("grant creates record", Apply(true, idA, "Alice", "permission", "vip")
                && ledger.Count == 1 && ledger[idA].Entries.Count == 1 && ledger[idA].Username == "Alice"
                && ledger[idA].Uuid == idA);
            Check("regrant dedups", Apply(true, idA, "Alice", "permission", "vip")
                && ledger[idA].Entries.Count == 1);
            Check("second perm appends", Apply(true, idA, "Alice", "permission", "kit.vip")
                && ledger[idA].Entries.Count == 2);
            Check("group is a distinct entry", Apply(true, idA, "Alice", "group", "vip")
                && ledger[idA].Entries.Count == 3);
            Check("revoke removes one", Apply(false, idA, "Alice", "permission", "kit.vip")
                && ledger[idA].Entries.Count == 2 && ledger.ContainsKey(idA));
            Check("revoke is case-insensitive", Apply(false, idA, "Alice", "PERMISSION", "VIP")
                && ledger[idA].Entries.Count == 1);
            Check("revoke of last drops record", Apply(false, idA, "Alice", "group", "vip")
                && !ledger.ContainsKey(idA));
            Check("revoke for unknown player is a no-op",
                !Apply(false, idB, "Bob", "permission", "vip") && !ledger.ContainsKey(idB));
            Check("revoke of untracked perm reports no change",
                Apply(true, idB, "Bob", "permission", "vip") &&
                !Apply(false, idB, "Bob", "permission", "never.granted") &&
                ledger[idB].Entries.Count == 1);
            Check("username updates on later command",
                Apply(true, idB, "Bobby", "permission", "extra") && ledger[idB].Username == "Bobby");
            Check("empty username does not clear a known one",
                Apply(true, idB, "", "permission", "another") && ledger[idB].Username == "Bobby");
            Check("empty value rejected", !Apply(true, idB, "Bobby", "permission", ""));
            Check("empty uuid rejected", !Apply(true, "", "Bobby", "permission", "vip"));
            Check("null ledger tolerated",
                !ApplyLedgerMutation(null!, idA, "Alice", true, "permission", "vip", 0, "raw"));
            Check("custom label nets out",
                Apply(true, idB, "Bobby", "custom", "vip") &&
                Apply(false, idB, "Bobby", "custom", "vip") &&
                ledger[idB].Entries.All(e => !string.Equals(e.Type, "custom", StringComparison.OrdinalIgnoreCase)));
            Check("custom revoke leaves a same-named permission intact",
                ledger[idB].Entries.Any(e => e.Type == "permission" && e.Value == "vip"));

            var replayRec = new PermissionLedgerRecord { Uuid = idA, Username = "Alice" };
            replayRec.Entries.Add(new PermissionLedgerEntry { Type = "permission", Value = "vip" });
            replayRec.Entries.Add(new PermissionLedgerEntry { Type = "group", Value = "vips" });
            replayRec.Entries.Add(new PermissionLedgerEntry { Type = "group", Value = "vip plus" });
            replayRec.Entries.Add(new PermissionLedgerEntry { Type = "custom", Value = "vip", RawCommand = $"addvip {idA}" });
            replayRec.Entries.Add(new PermissionLedgerEntry { Type = "custom", Value = "ghost", RawCommand = "" });

            var replayCommands = BuildReplayCommands(replayRec);
            Check("replay emits one command per replayable entry", replayCommands.Count == 4,
                $"got {replayCommands.Count}: {string.Join(" | ", replayCommands)}");
            Check("replay rebuilds permission grant",
                replayCommands.Contains($"oxide.grant user {idA} vip"), string.Join(" | ", replayCommands));
            Check("replay rebuilds group add",
                replayCommands.Contains($"oxide.usergroup add {idA} vips"), string.Join(" | ", replayCommands));
            Check("replay quotes a group name with spaces",
                replayCommands.Contains($"oxide.usergroup add {idA} \"vip plus\""), string.Join(" | ", replayCommands));
            Check("replay reuses the stored custom command",
                replayCommands.Contains($"addvip {idA}"), string.Join(" | ", replayCommands));
            var renamedRec = new PermissionLedgerRecord { Uuid = idA, Username = "Alice" };
            renamedRec.Entries.Add(new PermissionLedgerEntry
            {
                Type = "permission", Value = "vip", RawCommand = "oxide.grant user OldNickname vip"
            });
            Check("replay ignores RawCommand for oxide entries",
                BuildReplayCommands(renamedRec).SequenceEqual(new[] { $"oxide.grant user {idA} vip" }));
            Check("replay of an empty record emits nothing",
                BuildReplayCommands(new PermissionLedgerRecord { Uuid = idA }).Count == 0);
            Check("replay of a null record emits nothing", BuildReplayCommands(null!).Count == 0);

            var loaded = new Dictionary<string, PermissionLedgerRecord>
            {
                [idA] = new() { Username = "Alice" },
                [idB] = new() { Uuid = idB, Username = "Bob", Entries = null! },
                ["76561198000000003"] = new() { Uuid = "76561198000000003" },
            };
            loaded[idA].Entries.Add(new PermissionLedgerEntry { Type = "permission", Value = "vip" });
            loaded[idA].Entries.Add(new PermissionLedgerEntry { Type = "permission", Value = "" });
            var repaired = NormalizeLedger(loaded);
            Check("normalise backfills Uuid from the key",
                loaded.ContainsKey(idA) && loaded[idA].Uuid == idA);
            Check("normalise makes the repaired record replayable",
                loaded.ContainsKey(idA) &&
                BuildReplayCommands(loaded[idA]).SequenceEqual(new[] { $"oxide.grant user {idA} vip" }));
            Check("normalise drops empty records", !loaded.ContainsKey(idB) && !loaded.ContainsKey("76561198000000003"));
            Check("normalise reports repairs", repaired.Count > 0, $"repairs={repaired.Count}");
            Check("normalise repairs name the player",
                repaired.All(r => r.Contains("7656")), string.Join(" | ", repaired));
            Check("normalise reports the dropped entitlement",
                repaired.Any(r => r.Contains("unreplayable")), string.Join(" | ", repaired));
            Check("normalise tolerates null", NormalizeLedger(null!).Count == 0);
            Check("normalise of a healthy ledger reports nothing",
                NormalizeLedger(new Dictionary<string, PermissionLedgerRecord>
                {
                    [idA] = new()
                    {
                        Uuid = idA, Username = "Alice",
                        Entries = { new PermissionLedgerEntry { Type = "permission", Value = "vip" } },
                    },
                }).Count == 0);

            var lookup = new Dictionary<string, PermissionLedgerRecord>
            {
                [idA] = new() { Uuid = idA, Username = "Alice" },
            };
            Check("lookup by steamid", FindLedgerRecord(lookup, idA) != null);
            Check("lookup by username", FindLedgerRecord(lookup, "Alice") != null);
            Check("lookup by username ignores case", FindLedgerRecord(lookup, "alice") != null);
            Check("lookup miss returns null", FindLedgerRecord(lookup, "Nobody") == null);
            Check("lookup empty arg returns null", FindLedgerRecord(lookup, "") == null);
            Check("lookup null ledger returns null", FindLedgerRecord(null!, idA) == null);

            var total = passed + failures.Count;
            if (failures.Count == 0)
            {
                player.Reply($"Permission ledger self-test PASSED, {passed}/{total} checks. Live ledger untouched.");
                Puts($"[Tebex] selftest PASSED: {passed}/{total} checks.");
                return;
            }

            player.Reply($"Permission ledger self-test FAILED, {failures.Count} of {total} checks failed:");
            PrintWarning($"[Tebex] selftest FAILED: {failures.Count}/{total} checks.");
            foreach (var failure in failures)
            {
                player.Reply($"  - {failure}");
                PrintWarning($"[Tebex] selftest FAIL {failure}");
            }
        }

        #endregion

        #region Localization

        private static readonly Dictionary<string, string> LangDefaults = new()
        {
            ["NoPermission"] = "You do not have permission to use that command.",
            ["SecretMissing"] = "Tebex is not configured. An admin must run `tebex secret <key>`.",
            ["StoreUnavailable"] = "The store is currently unavailable. Please try again later.",
            ["HelpHeader"] = "Tebex commands:"
        };

        private string Lang(string key, string playerId = null) => lang.GetMessage(key, this, playerId);

        #endregion

        #region Utilities

        private static string GetAnonymizedPlayerIp(BasePlayer player)
        {
            const string unknown = "127.0.0.x";
            if (player == null) return unknown;

            var ip = player.net?.connection?.ipaddress;
            if (ip == null || ip.Length == 0) return unknown;

            var a = ip.Split('.');
            if (a.Length < 4) return unknown;
            return $"{a[0]}.{a[1]}.{a[2]}.x";
        }

        private static string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= len ? s : s.Substring(0, len) + "…";
        }

        private static string StripHtml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (var c in s)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        #endregion
    }
}

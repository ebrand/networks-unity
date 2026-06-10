// Minimal 16-bit GRAYSCALE PNG encoder — the exact format DemTerrainWorld.TryDecodeGray16 reads back
// (colorType 0, bitDepth 16, non-interlaced, filter None, big-endian samples). Input is TOP-DOWN
// (row 0 = top/north); the decoder flips to bottom-up. Used by the in-app DEM downloader to write
// DemChunkSource tiles. No external deps — DeflateStream + manual zlib wrapper + CRC32/Adler32.

using System;
using System.IO;
using System.IO.Compression;

namespace NetworkDesigner.Terrain
{
    public static class DemPng16
    {
        // gray: w*h samples, row-major, TOP-DOWN. Returns PNG bytes.
        public static byte[] Encode(ushort[] gray, int w, int h)
        {
            int stride = w * 2;
            var filtered = new byte[h * (stride + 1)];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                filtered[p++] = 0;                 // per-scanline filter: None
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    ushort v = gray[row + x];
                    filtered[p++] = (byte)(v >> 8); // big-endian 16-bit
                    filtered[p++] = (byte)(v & 0xFF);
                }
            }

            byte[] zlib = ZlibCompress(filtered);

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);   // PNG signature
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 16;   // bit depth
            ihdr[9] = 0;    // colour type 0 = grayscale
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // deflate / filter 0 / no interlace
            WriteChunk(ms, "IHDR", ihdr);
            WriteChunk(ms, "IDAT", zlib);
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static byte[] ZlibCompress(byte[] data)
        {
            using var outMs = new MemoryStream();
            outMs.WriteByte(0x78); outMs.WriteByte(0x9C);          // zlib header (deflate, 32k window)
            using (var ds = new DeflateStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, data.Length);                    // raw DEFLATE (disposed → flushed here)
            uint adler = Adler32(data);
            outMs.WriteByte((byte)(adler >> 24)); outMs.WriteByte((byte)(adler >> 16));
            outMs.WriteByte((byte)(adler >> 8)); outMs.WriteByte((byte)adler);
            return outMs.ToArray();
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(t, data);
            var c = new byte[4]; WriteBE(c, 0, unchecked((int)crc)); s.Write(c, 0, 4);
        }

        static void WriteBE(byte[] b, int o, int v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521u; b = (b + a) % 65521u; }
            return (b << 16) | a;
        }

        static uint[] _crc;
        static uint Crc32(byte[] type, byte[] data)
        {
            if (_crc == null)
            {
                _crc = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crc[n] = c;
                }
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < type.Length; i++) crc = _crc[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < data.Length; i++) crc = _crc[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace BioimageVR
{
    // nifti reader, uint8 einkanal oder rgb24, gzip und byte order
    public static class NiftiVolumeLoader
    {
        public class Volume
        {
            public Texture3D Texture;
            // hellster wert je block, siehe ComputeBricks
            public Texture3D BrickTexture;
            public int SizeX;
            public int SizeY;
            public int SizeZ;

            // voxel abstand aus pixdim, gegen verzerrung bei nicht kubischen volumen
            public Vector3 VoxelSize;

            // rohe voxel, x schnell y z langsam, bei rgb 3 bytes pro voxel
            public byte[] Voxels;

            public bool IsColor;

            // true wenn r g b rohe kanaele sind, siehe LIVE_CHANNELS_MARKER
            public bool IsLiveChannels;

            // segmentierung, indizierung wie Voxels, null ohne _labels datei
            public ushort[] Labels;
            public Texture3D LabelTexture;
            public int MaxLabel;
            // anzahl verschiedener ids ausser 0, ids koennen luecken haben
            public int LabelCount;
            // werte pro zelle, index ist die id, siehe CellStats
            public CellInfo[] Cells;
            // verdacht auf zwei ineinandergepresste zellen, null bis die analyse fertig ist
            public SplitHint[] SplitHints;
        }

        // label maske ohne textur, von jedem thread aus erzeugbar
        public class RawLabelData
        {
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public ushort[] Labels;
            public int MaxLabel;
            public int LabelCount;
        }

        // ergebnis von ParseFile, reine cpu daten, von jedem thread erzeugbar
        public class RawVolumeData
        {
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public Vector3 VoxelSize;
            public byte[] Voxels;
            public bool IsColor;
            public bool IsLiveChannels;
        }

        private const int HeaderSize = 348;
        private const short DT_UINT8 = 2;
        private const short DT_UINT16 = 512;
        private const short DT_RGB24 = 128;
        private const int DescripOffset = 148;
        private const int DescripLength = 80;

        // muss exakt zu preprocessing/convert_to_volume.py: LIVE_CHANNELS_MARKER passen
        private static readonly byte[] LiveChannelsMarker = Encoding.ASCII.GetBytes("biovr:live_channels");

        // laedt und baut die textur direkt, blockiert bis fertig
        public static Volume Load(string path) => BuildTexture(ParseFile(path));

        // liest und parst die datei ohne unity api, sicher im background thread
        public static RawVolumeData ParseFile(string path)
        {
            byte[] bytes = ReadHeader(path, out bool swap, out short[] dim, out short datatype, out int dataOffset);
            if (datatype != DT_UINT8 && datatype != DT_RGB24)
            {
                throw new NotSupportedException(
                    $"NIfTI datatype code {datatype} is not supported. This loader only reads uint8 (2) or " +
                    "RGB24 (128) volumes, which is what preprocessing/convert_to_volume.py always produces.");
            }

            bool isColor = datatype == DT_RGB24;
            bool isLiveChannels = isColor && HasMarker(bytes, DescripOffset, DescripLength, LiveChannelsMarker);
            int bytesPerVoxel = isColor ? 3 : 1;

            float[] pixdim = new float[8];
            for (int i = 0; i < 8; i++)
                pixdim[i] = ReadFloat32(bytes, 76 + i * 4, swap);

            int sizeX = dim[1];
            int sizeY = dim[2];
            int sizeZ = dim[0] >= 3 ? dim[3] : 1;
            int voxelCount = sizeX * sizeY * sizeZ;
            int byteCount = voxelCount * bytesPerVoxel;

            if (dataOffset + byteCount > bytes.Length)
                throw new InvalidDataException($"'{path}' header declares {voxelCount} voxels but the file is too short.");

            byte[] voxels = new byte[byteCount];
            Array.Copy(bytes, dataOffset, voxels, 0, byteCount);

            return new RawVolumeData
            {
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
                Voxels = voxels,
                IsColor = isColor,
                IsLiveChannels = isLiveChannels,
                VoxelSize = new Vector3(
                    pixdim[1] > 0 ? pixdim[1] : 1f,
                    pixdim[2] > 0 ? pixdim[2] : 1f,
                    pixdim[3] > 0 ? pixdim[3] : 1f)
            };
        }

        // label maske lesen, uint16 oder uint8, siehe preprocessing/convert_labels.py
        public static RawLabelData ParseLabels(string path)
        {
            byte[] bytes = ReadHeader(path, out bool swap, out short[] dim, out short datatype, out int dataOffset);
            if (datatype != DT_UINT16 && datatype != DT_UINT8)
                throw new NotSupportedException($"Label datatype {datatype} not supported, expected uint16 (512) or uint8 (2).");

            int sizeX = dim[1];
            int sizeY = dim[2];
            int sizeZ = dim[0] >= 3 ? dim[3] : 1;
            int count = sizeX * sizeY * sizeZ;
            int bytesPerVoxel = datatype == DT_UINT16 ? 2 : 1;
            if (dataOffset + count * bytesPerVoxel > bytes.Length)
                throw new InvalidDataException($"'{path}' header declares {count} labels but the file is too short.");

            var labels = new ushort[count];
            var seen = new bool[65536];
            int max = 0;
            int distinct = 0;
            for (int i = 0; i < count; i++)
            {
                int o = dataOffset + i * bytesPerVoxel;
                int v = bytesPerVoxel == 1 ? bytes[o]
                    : swap ? (bytes[o] << 8) | bytes[o + 1] : bytes[o] | (bytes[o + 1] << 8);
                labels[i] = (ushort)v;
                if (v > max) max = v;
                if (v > 0 && !seen[v])
                {
                    seen[v] = true;
                    distinct++;
                }
            }

            return new RawLabelData { SizeX = sizeX, SizeY = sizeY, SizeZ = sizeZ, Labels = labels, MaxLabel = max, LabelCount = distinct };
        }

        // labels als lo und hi byte in rg8, point filter damit ids nicht vermischen
        public static Texture3D BuildLabelTexture(RawLabelData data)
        {
            int count = data.Labels.Length;
            byte[] rg = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                rg[i * 2] = (byte)(data.Labels[i] & 0xFF);
                rg[i * 2 + 1] = (byte)(data.Labels[i] >> 8);
            }

            var texture = new Texture3D(data.SizeX, data.SizeY, data.SizeZ, TextureFormat.RG16, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            texture.SetPixelData(rg, 0);
            texture.Apply(false, true);
            return texture;
        }

        // hellster wert je block, shader ueberspringt damit leere bereiche
        public const int BrickSize = 8;
        private const int BrickMargin = 2;

        public class BrickMap
        {
            public int CountX;
            public int CountY;
            public int CountZ;
            public byte[] Max;
        }

        // reine cpu arbeit, laeuft im ladethread
        public static BrickMap ComputeBricks(RawVolumeData data)
        {
            int sx = data.SizeX, sy = data.SizeY, sz = data.SizeZ;
            int bx = (sx + BrickSize - 1) / BrickSize;
            int by = (sy + BrickSize - 1) / BrickSize;
            int bz = (sz + BrickSize - 1) / BrickSize;

            // direkt gerechnet, bei 8er bloecken mit 2er rand schnell genug
            var max = new byte[bx * by * bz];
            byte[] v = data.Voxels;
            bool color = data.IsColor;
            for (int k = 0; k < bz; k++)
            for (int j = 0; j < by; j++)
            for (int i = 0; i < bx; i++)
            {
                int x0 = Math.Max(i * BrickSize - BrickMargin, 0), x1 = Math.Min((i + 1) * BrickSize + BrickMargin, sx);
                int y0 = Math.Max(j * BrickSize - BrickMargin, 0), y1 = Math.Min((j + 1) * BrickSize + BrickMargin, sy);
                int z0 = Math.Max(k * BrickSize - BrickMargin, 0), z1 = Math.Min((k + 1) * BrickSize + BrickMargin, sz);
                int m = 0;
                for (int z = z0; z < z1 && m < 255; z++)
                for (int y = y0; y < y1; y++)
                {
                    int row = sx * (y + sy * z);
                    for (int x = x0; x < x1; x++)
                    {
                        int idx = row + x;
                        int value = color ? Math.Max(v[idx * 3], Math.Max(v[idx * 3 + 1], v[idx * 3 + 2])) : v[idx];
                        if (value > m) m = value;
                    }
                }
                max[i + bx * (j + by * k)] = (byte)m;
            }
            return new BrickMap { CountX = bx, CountY = by, CountZ = bz, Max = max };
        }

        public static Texture3D BuildBrickTexture(BrickMap bricks)
        {
            var texture = new Texture3D(bricks.CountX, bricks.CountY, bricks.CountZ, TextureFormat.R8, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            texture.SetPixelData(bricks.Max, 0);
            texture.Apply(false, true);
            return texture;
        }

        // gemeinsamer header teil fuer volumen und labels
        private static byte[] ReadHeader(string path, out bool swap, out short[] dim, out short datatype, out int dataOffset)
        {
            byte[] raw = File.ReadAllBytes(path);
            byte[] bytes = LooksGzipped(raw) ? Decompress(raw) : raw;

            if (bytes.Length < HeaderSize)
                throw new InvalidDataException($"'{path}' is too small to contain a NIfTI-1 header.");

            swap = DetermineByteSwap(bytes, path);

            dim = new short[8];
            for (int i = 0; i < 8; i++)
                dim[i] = ReadInt16(bytes, 40 + i * 2, swap);

            datatype = ReadInt16(bytes, 70, swap);

            float voxOffset = ReadFloat32(bytes, 108, swap);
            dataOffset = voxOffset >= HeaderSize ? (int)voxOffset : 352;
            return bytes;
        }

        // baut die gpu textur, nur auf dem main thread
        public static Volume BuildTexture(RawVolumeData data)
        {
            Texture3D texture = data.IsColor
                ? BuildRgbaTexture(data.SizeX, data.SizeY, data.SizeZ, data.Voxels)
                : BuildGrayscaleTexture(data.SizeX, data.SizeY, data.SizeZ, data.Voxels);

            return new Volume
            {
                Texture = texture,
                SizeX = data.SizeX,
                SizeY = data.SizeY,
                SizeZ = data.SizeZ,
                Voxels = data.Voxels,
                IsColor = data.IsColor,
                IsLiveChannels = data.IsLiveChannels,
                VoxelSize = data.VoxelSize
            };
        }

        private static Texture3D BuildGrayscaleTexture(int sizeX, int sizeY, int sizeZ, byte[] voxels)
        {
            // nifti layout passt schon zu texture3d, keine umsortierung noetig
            var texture = new Texture3D(sizeX, sizeY, sizeZ, TextureFormat.R8, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixelData(voxels, 0);
            // keine cpu kopie in unity, Volume.Voxels haelt schon eine
            texture.Apply(false, true);
            return texture;
        }

        // rgb24 auf RGBA32 aufblasen, das format geht ueberall
        private static Texture3D BuildRgbaTexture(int sizeX, int sizeY, int sizeZ, byte[] rgbVoxels)
        {
            int voxelCount = sizeX * sizeY * sizeZ;
            byte[] rgba = new byte[voxelCount * 4];
            for (int i = 0; i < voxelCount; i++)
            {
                rgba[i * 4 + 0] = rgbVoxels[i * 3 + 0];
                rgba[i * 4 + 1] = rgbVoxels[i * 3 + 1];
                rgba[i * 4 + 2] = rgbVoxels[i * 3 + 2];
                rgba[i * 4 + 3] = 255;
            }

            var texture = new Texture3D(sizeX, sizeY, sizeZ, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixelData(rgba, 0);
            // keine cpu kopie in unity, Volume.Voxels haelt schon eine
            texture.Apply(false, true);
            return texture;
        }

        // beginnt das header feld mit dem marker, descrip ist nullgepolsterter freitext
        private static bool HasMarker(byte[] bytes, int offset, int length, byte[] marker)
        {
            if (offset + length > bytes.Length || marker.Length > length) return false;
            for (int i = 0; i < marker.Length; i++)
                if (bytes[offset + i] != marker[i]) return false;
            return true;
        }

        private static bool LooksGzipped(byte[] bytes)
        {
            return bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        }

        private static byte[] Decompress(byte[] gzipped)
        {
            using var input = new MemoryStream(gzipped);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        private static bool DetermineByteSwap(byte[] bytes, string path)
        {
            if (ReadInt32(bytes, 0, false) == HeaderSize) return false;
            if (ReadInt32(bytes, 0, true) == HeaderSize) return true;
            throw new InvalidDataException($"'{path}' is not a valid NIfTI-1 file (sizeof_hdr != 348 in either byte order).");
        }

        private static short ReadInt16(byte[] b, int offset, bool swap)
        {
            return swap
                ? (short)((b[offset] << 8) | b[offset + 1])
                : (short)(b[offset] | (b[offset + 1] << 8));
        }

        private static int ReadInt32(byte[] b, int offset, bool swap)
        {
            return swap
                ? (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]
                : b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);
        }

        private static float ReadFloat32(byte[] b, int offset, bool swap)
        {
            byte[] tmp = new byte[4];
            Array.Copy(b, offset, tmp, 0, 4);
            if (swap) Array.Reverse(tmp);
            return BitConverter.ToSingle(tmp, 0);
        }
    }
}

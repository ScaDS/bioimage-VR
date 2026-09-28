using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace BioimageVR
{
    // nifti reader, uint8 einkanal oder rgb24, mit gzip und byte order erkennung
    // mehr datentypen spaeter phase
    public static class NiftiVolumeLoader
    {
        public class Volume
        {
            public Texture3D Texture;
            public int SizeX;
            public int SizeY;
            public int SizeZ;

            // voxel abstand aus pixdim, gegen verzerrung bei nicht kubischen volumen
            public Vector3 VoxelSize;

            // rohe voxel, x schnell y z langsam - bei rgb 3 bytes pro voxel (r,g,b)
            // gebraucht fuer slice views und koordinaten mapping ohne readback
            public byte[] Voxels;

            public bool IsColor;

            // true wenn r,g,b drei ROHE, unabhaengige kanal-intensitaeten sind statt
            // einer schon fertig gemischten farbe - siehe LIVE_CHANNELS_MARKER
            public bool IsLiveChannels;

            // segmentierung, null wenn keine _labels datei daneben liegt
            // gleiche indizierung wie Voxels, 0 hintergrund
            public ushort[] Labels;
            public Texture3D LabelTexture;
            public int MaxLabel;
            // anzahl verschiedener ids ausser 0, ids koennen luecken haben
            public int LabelCount;
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

        // ergebnis von ParseFile, reine cpu daten ohne texture3d, kann also von jedem
        // thread erzeugt werden. BuildTexture macht dann den unity-api teil
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

        // laedt und baut die textur direkt, blockiert bis fertig. fuer den
        // hauptpfad stattdessen ParseFile (thread) + BuildTexture (main thread) nutzen
        public static Volume Load(string path) => BuildTexture(ParseFile(path));

        // liest und parst die datei, reine cpu arbeit ohne unity api aufrufe,
        // deshalb sicher von einem background thread aus aufrufbar
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

        // labels als lo und hi byte in rg8, point filter damit ids nicht vermischt werden
        // shader setzt die id wieder zusammen, siehe SampleLabel
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

        // baut die gpu textur aus geparsten daten, muss auf dem main thread laufen
        // (unity texture api ist nicht threadsicher)
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
            // kein mipmap-update noetig (mipChain=false), und Volume.Voxels haelt schon
            // eine cpu kopie fuer slice views, unity braucht also keine eigene mehr
            texture.Apply(false, true);
            return texture;
        }

        // rgb24 hat nur 3 bytes/voxel, Texture3D braucht aber ein von der gpu
        // unterstuetztes format - RGBA32 ist am zuverlaessigsten plattformuebergreifend,
        // deshalb hier interleaved r,g,b auf r,g,b,a=255 pro voxel aufblasen
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
            // kein mipmap-update noetig (mipChain=false), und Volume.Voxels haelt schon
            // eine cpu kopie fuer slice views, unity braucht also keine eigene mehr
            texture.Apply(false, true);
            return texture;
        }

        // prueft ob 'field' (offset/length im header) mit 'marker' beginnt - descrip ist
        // ein 80-byte freitext-feld, nullterminiert/nullgepolstert, deshalb praefix-check
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

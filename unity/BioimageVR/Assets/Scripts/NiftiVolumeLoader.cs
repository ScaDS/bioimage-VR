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
        }

        private const int HeaderSize = 348;
        private const short DT_UINT8 = 2;
        private const short DT_RGB24 = 128;
        private const int DescripOffset = 148;
        private const int DescripLength = 80;

        // muss exakt zu preprocessing/convert_to_volume.py: LIVE_CHANNELS_MARKER passen
        private static readonly byte[] LiveChannelsMarker = Encoding.ASCII.GetBytes("biovr:live_channels");

        public static Volume Load(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            byte[] bytes = LooksGzipped(raw) ? Decompress(raw) : raw;

            if (bytes.Length < HeaderSize)
                throw new InvalidDataException($"'{path}' is too small to contain a NIfTI-1 header.");

            bool swap = DetermineByteSwap(bytes, path);

            short[] dim = new short[8];
            for (int i = 0; i < 8; i++)
                dim[i] = ReadInt16(bytes, 40 + i * 2, swap);

            short datatype = ReadInt16(bytes, 70, swap);
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

            float voxOffset = ReadFloat32(bytes, 108, swap);

            int sizeX = dim[1];
            int sizeY = dim[2];
            int sizeZ = dim[0] >= 3 ? dim[3] : 1;
            int voxelCount = sizeX * sizeY * sizeZ;
            int byteCount = voxelCount * bytesPerVoxel;

            int dataOffset = voxOffset >= HeaderSize ? (int)voxOffset : 352;
            if (dataOffset + byteCount > bytes.Length)
                throw new InvalidDataException($"'{path}' header declares {voxelCount} voxels but the file is too short.");

            byte[] voxels = new byte[byteCount];
            Array.Copy(bytes, dataOffset, voxels, 0, byteCount);

            Texture3D texture = isColor
                ? BuildRgbaTexture(sizeX, sizeY, sizeZ, voxels)
                : BuildGrayscaleTexture(sizeX, sizeY, sizeZ, voxels);

            return new Volume
            {
                Texture = texture,
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

        private static Texture3D BuildGrayscaleTexture(int sizeX, int sizeY, int sizeZ, byte[] voxels)
        {
            // nifti layout passt schon zu texture3d, keine umsortierung noetig
            var texture = new Texture3D(sizeX, sizeY, sizeZ, TextureFormat.R8, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixelData(voxels, 0);
            texture.Apply();
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
            texture.Apply();
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

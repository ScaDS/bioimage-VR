using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace BioimageVR
{
    // nifti reader, nur uint8 einkanal, mit gzip und byte order erkennung
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

            // rohe voxel, x schnell y z langsam
            // gebraucht fuer slice views und koordinaten mapping ohne readback
            public byte[] Voxels;
        }

        private const int HeaderSize = 348;
        private const short DT_UINT8 = 2;

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
            if (datatype != DT_UINT8)
            {
                throw new NotSupportedException(
                    $"NIfTI datatype code {datatype} is not supported. This loader only reads uint8 volumes " +
                    "(datatype 2), which is what preprocessing/convert_to_volume.py always produces.");
            }

            float[] pixdim = new float[8];
            for (int i = 0; i < 8; i++)
                pixdim[i] = ReadFloat32(bytes, 76 + i * 4, swap);

            float voxOffset = ReadFloat32(bytes, 108, swap);

            int sizeX = dim[1];
            int sizeY = dim[2];
            int sizeZ = dim[0] >= 3 ? dim[3] : 1;
            int voxelCount = sizeX * sizeY * sizeZ;

            int dataOffset = voxOffset >= HeaderSize ? (int)voxOffset : 352;
            if (dataOffset + voxelCount > bytes.Length)
                throw new InvalidDataException($"'{path}' header declares {voxelCount} voxels but the file is too short.");

            byte[] voxels = new byte[voxelCount];
            Array.Copy(bytes, dataOffset, voxels, 0, voxelCount);

            // nifti layout passt schon zu texture3d, keine umsortierung noetig
            Texture3D texture = new Texture3D(sizeX, sizeY, sizeZ, TextureFormat.R8, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.SetPixelData(voxels, 0);
            texture.Apply();

            return new Volume
            {
                Texture = texture,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = sizeZ,
                Voxels = voxels,
                VoxelSize = new Vector3(
                    pixdim[1] > 0 ? pixdim[1] : 1f,
                    pixdim[2] > 0 ? pixdim[2] : 1f,
                    pixdim[3] > 0 ? pixdim[3] : 1f)
            };
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

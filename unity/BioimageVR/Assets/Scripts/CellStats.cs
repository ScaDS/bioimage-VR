using UnityEngine;

namespace BioimageVR
{
    // werte pro zelle, index ist die label id, luecken haben VoxelCount 0
    public struct CellInfo
    {
        public int Id;
        public int VoxelCount;
        public Vector3Int Min;
        public Vector3Int Max;
        // schwerpunkt in voxel koordinaten
        public Vector3 Centroid;
        public float MeanIntensity;

        public bool Exists => VoxelCount > 0;
    }

    public static class CellStats
    {
        // ein durchlauf ueber alle voxel, laeuft im ladethread von VolumeView
        public static CellInfo[] Compute(NiftiVolumeLoader.RawLabelData labels, byte[] voxels, bool isColor)
        {
            var cells = new CellInfo[labels.MaxLabel + 1];
            var sums = new double[cells.Length];
            var cx = new double[cells.Length];
            var cy = new double[cells.Length];
            var cz = new double[cells.Length];
            for (int id = 0; id < cells.Length; id++)
            {
                cells[id].Id = id;
                cells[id].Min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
                cells[id].Max = new Vector3Int(-1, -1, -1);
            }

            ushort[] ids = labels.Labels;
            int i = 0;
            for (int z = 0; z < labels.SizeZ; z++)
            for (int y = 0; y < labels.SizeY; y++)
            for (int x = 0; x < labels.SizeX; x++, i++)
            {
                int id = ids[i];
                if (id == 0) continue;
                ref CellInfo c = ref cells[id];
                c.VoxelCount++;
                if (x < c.Min.x) c.Min.x = x;
                if (y < c.Min.y) c.Min.y = y;
                if (z < c.Min.z) c.Min.z = z;
                if (x > c.Max.x) c.Max.x = x;
                if (y > c.Max.y) c.Max.y = y;
                if (z > c.Max.z) c.Max.z = z;
                cx[id] += x;
                cy[id] += y;
                cz[id] += z;
                sums[id] += isColor
                    ? Mathf.Max(voxels[i * 3], Mathf.Max(voxels[i * 3 + 1], voxels[i * 3 + 2])) / 255f
                    : voxels[i] / 255f;
            }

            for (int id = 1; id < cells.Length; id++)
            {
                int n = cells[id].VoxelCount;
                if (n == 0) continue;
                cells[id].Centroid = new Vector3((float)(cx[id] / n), (float)(cy[id] / n), (float)(cz[id] / n));
                cells[id].MeanIntensity = (float)(sums[id] / n);
            }
            return cells;
        }

        // eine zelle neu vermessen, nur im rahmen min bis max, z.b. nach dem teilen
        public static CellInfo ComputeCell(NiftiVolumeLoader.Volume vol, int id, Vector3Int min, Vector3Int max)
        {
            var cell = new CellInfo
            {
                Id = id,
                Min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue),
                Max = new Vector3Int(-1, -1, -1),
            };
            double cx = 0, cy = 0, cz = 0, sum = 0;
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            {
                int row = vol.SizeX * (y + vol.SizeY * z);
                for (int x = min.x; x <= max.x; x++)
                {
                    int i = row + x;
                    if (vol.Labels[i] != id) continue;
                    cell.VoxelCount++;
                    cell.Min = Vector3Int.Min(cell.Min, new Vector3Int(x, y, z));
                    cell.Max = Vector3Int.Max(cell.Max, new Vector3Int(x, y, z));
                    cx += x;
                    cy += y;
                    cz += z;
                    sum += vol.IsColor
                        ? Mathf.Max(vol.Voxels[i * 3], Mathf.Max(vol.Voxels[i * 3 + 1], vol.Voxels[i * 3 + 2])) / 255f
                        : vol.Voxels[i] / 255f;
                }
            }
            int n = cell.VoxelCount;
            if (n > 0)
            {
                cell.Centroid = new Vector3((float)(cx / n), (float)(cy / n), (float)(cz / n));
                cell.MeanIntensity = (float)(sum / n);
            }
            return cell;
        }

        public static float Volume(CellInfo c, Vector3 voxelSize) =>
            c.VoxelCount * voxelSize.x * voxelSize.y * voxelSize.z;

        // durchmesser einer kugel mit gleichem volumen
        public static float EquivalentDiameter(CellInfo c, Vector3 voxelSize) =>
            Mathf.Pow(6f * Volume(c, voxelSize) / Mathf.PI, 1f / 3f);

        // kurze zeile fuer schild und zellliste, unit wie VolumeView.Unit
        public static string Describe(CellInfo c, Vector3 voxelSize, string unit) =>
            $"{Units.Volume(Volume(c, voxelSize), unit)} · Ø {Units.Length(EquivalentDiameter(c, voxelSize), unit, "0.0")}";
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BioimageVR
{
    // hinweis ob eine zelle eher zwei ineinandergepresste ist und wo man teilt
    public struct SplitHint
    {
        public bool Found;
        // 1 minus engstelle durch kleineren gipfel, 0 keine einschnuerung, 1 zwei getrennte teile
        public float Score;
        public float RadiusA;
        public float RadiusB;
        public float Neck;
        // alles in voxel koordinaten
        public Vector3 PeakA;
        public Vector3 PeakB;
        public Vector3 NeckPoint;

        public bool Likely => Found && Score >= CellShapeAnalysis.LikelyScore;

        public string Describe() => !Found ? ""
            : Score >= 0.99f ? "two separate parts share this number"
            : $"{(Likely ? "Probably" : "Possibly")} 2 cells · {Score * 100f:0} % neck";
    }

    // distanztransformation je zelle, schmale engstelle zwischen zwei gipfeln heisst einschnuerung
    public static class CellShapeAnalysis
    {
        public const float NeckRatio = 0.75f;
        public const float LikelyScore = 0.5f;
        private const float MinPeakFraction = 0.5f;
        private const int MaxPeaks = 40;
        private const int MinVoxels = 200;
        // gross aber endlich, parabel rechnung braucht dann keine sonderfaelle
        private const float Inside = 1e20f;

        public static SplitHint[] AnalyzeAll(NiftiVolumeLoader.Volume vol)
        {
            var hints = new SplitHint[vol.Cells.Length];
            for (int id = 1; id < vol.Cells.Length; id++)
                if (vol.Cells[id].Exists) hints[id] = Analyze(vol, vol.Cells[id]);
            return hints;
        }

        public static SplitHint Analyze(NiftiVolumeLoader.Volume vol, CellInfo cell)
        {
            if (!cell.Exists || cell.VoxelCount < MinVoxels) return default;

            // rahmen mit einem voxel rand, aussen ist hintergrund
            Vector3Int origin = cell.Min - Vector3Int.one;
            int nx = cell.Max.x - cell.Min.x + 3, ny = cell.Max.y - cell.Min.y + 3, nz = cell.Max.z - cell.Min.z + 3;
            var dist = new float[nx * ny * nz];
            for (int z = 1; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
            {
                int row = vol.SizeX * (origin.y + y + vol.SizeY * (origin.z + z));
                for (int x = 1; x < nx - 1; x++)
                    if (vol.Labels[row + origin.x + x] == cell.Id) dist[x + nx * (y + ny * z)] = Inside;
            }

            Vector3 spacing = vol.VoxelSize;
            SquaredDistance(dist, nx, ny, nz, spacing);
            float max = 0f;
            for (int i = 0; i < dist.Length; i++)
            {
                dist[i] = Mathf.Sqrt(dist[i]);
                if (dist[i] > max) max = dist[i];
            }

            List<(Vector3Int p, float r)> peaks = FindPeaks(dist, nx, ny, nz, MinPeakFraction * max);
            float bestKey = 0f;
            var hint = new SplitHint();
            float step = Mathf.Min(spacing.x, Mathf.Min(spacing.y, spacing.z)) * 0.5f;
            for (int i = 0; i < peaks.Count; i++)
            for (int j = i + 1; j < peaks.Count; j++)
            {
                Vector3 a = peaks[i].p, b = peaks[j].p;
                float length = Vector3.Scale(b - a, spacing).magnitude;
                int samples = Mathf.CeilToInt(length / step) + 2;
                float neck = float.MaxValue;
                Vector3 neckPoint = a;
                for (int s = 0; s < samples; s++)
                {
                    Vector3 q = Vector3.Lerp(a, b, s / (float)(samples - 1));
                    float d = Sample(dist, nx, ny, nz, q);
                    if (d < neck)
                    {
                        neck = d;
                        neckPoint = q;
                    }
                }

                float smaller = Mathf.Min(peaks[i].r, peaks[j].r);
                float ratio = neck / smaller;
                if (ratio >= NeckRatio) continue;
                float score = 1f - ratio;
                // dicke blobs mit deutlicher engstelle bevorzugen
                float key = score * smaller;
                if (key <= bestKey) continue;
                bestKey = key;
                hint = new SplitHint
                {
                    Found = true,
                    Score = score,
                    RadiusA = peaks[i].r,
                    RadiusB = peaks[j].r,
                    Neck = neck,
                    PeakA = a + origin,
                    PeakB = b + origin,
                    NeckPoint = neckPoint + origin,
                };
            }
            return hint;
        }

        // lokale maxima in 26er nachbarschaft, die dicksten zuerst
        private static List<(Vector3Int, float)> FindPeaks(float[] d, int nx, int ny, int nz, float minValue)
        {
            var peaks = new List<(Vector3Int, float)>();
            for (int z = 1; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
            for (int x = 1; x < nx - 1; x++)
            {
                float v = d[x + nx * (y + ny * z)];
                if (v < minValue) continue;
                bool isMax = true;
                for (int dz = -1; dz <= 1 && isMax; dz++)
                for (int dy = -1; dy <= 1 && isMax; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (d[x + dx + nx * (y + dy + ny * (z + dz))] > v)
                    {
                        isMax = false;
                        break;
                    }
                }
                if (isMax) peaks.Add((new Vector3Int(x, y, z), v));
            }
            peaks.Sort((p, q) => q.Item2.CompareTo(p.Item2));
            if (peaks.Count > MaxPeaks) peaks.RemoveRange(MaxPeaks, peaks.Count - MaxPeaks);
            return peaks;
        }

        private static float Sample(float[] d, int nx, int ny, int nz, Vector3 q)
        {
            int x0 = Mathf.Clamp((int)q.x, 0, nx - 2), y0 = Mathf.Clamp((int)q.y, 0, ny - 2), z0 = Mathf.Clamp((int)q.z, 0, nz - 2);
            float fx = Mathf.Clamp01(q.x - x0), fy = Mathf.Clamp01(q.y - y0), fz = Mathf.Clamp01(q.z - z0);
            float At(int x, int y, int z) => d[x + nx * (y + ny * z)];
            float c00 = Mathf.Lerp(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
            float c10 = Mathf.Lerp(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
            float c01 = Mathf.Lerp(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
            float c11 = Mathf.Lerp(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        // quadrierter abstand zum hintergrund nach felzenszwalb huttenlocher, voxelgroesse als gewicht
        private static void SquaredDistance(float[] f, int nx, int ny, int nz, Vector3 spacing)
        {
            int longest = Math.Max(nx, Math.Max(ny, nz));
            var line = new float[longest];
            var result = new float[longest];
            var v = new int[longest];
            var zBounds = new float[longest + 1];

            for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            {
                int start = nx * (y + ny * z);
                for (int x = 0; x < nx; x++) line[x] = f[start + x];
                Transform1D(line, result, nx, spacing.x, v, zBounds);
                for (int x = 0; x < nx; x++) f[start + x] = result[x];
            }
            for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                for (int y = 0; y < ny; y++) line[y] = f[x + nx * (y + ny * z)];
                Transform1D(line, result, ny, spacing.y, v, zBounds);
                for (int y = 0; y < ny; y++) f[x + nx * (y + ny * z)] = result[y];
            }
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                for (int z = 0; z < nz; z++) line[z] = f[x + nx * (y + ny * z)];
                Transform1D(line, result, nz, spacing.z, v, zBounds);
                for (int z = 0; z < nz; z++) f[x + nx * (y + ny * z)] = result[z];
            }
        }

        // untere huelle der parabeln, positionen in um statt voxeln
        private static void Transform1D(float[] f, float[] d, int n, float w, int[] v, float[] zb)
        {
            int k = 0;
            v[0] = 0;
            zb[0] = float.NegativeInfinity;
            zb[1] = float.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double xq = q * (double)w;
                float s;
                while (true)
                {
                    // double, sonst frisst die 1e20 fuer innen voxel die kleinen abstaende auf
                    double xp = v[k] * (double)w;
                    s = (float)(((f[q] + xq * xq) - (f[v[k]] + xp * xp)) / (2.0 * (xq - xp)));
                    if (s > zb[k] || k == 0) break;
                    k--;
                }
                if (s <= zb[k])
                {
                    // auch die erste parabel ist ueberall schlechter, ersetzen
                    v[0] = q;
                    zb[0] = float.NegativeInfinity;
                    zb[1] = float.PositiveInfinity;
                    continue;
                }
                k++;
                v[k] = q;
                zb[k] = s;
                zb[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (int q = 0; q < n; q++)
            {
                float x = q * w;
                while (zb[k + 1] < x) k++;
                float dx = x - v[k] * w;
                d[q] = dx * dx + f[v[k]];
            }
        }
    }
}

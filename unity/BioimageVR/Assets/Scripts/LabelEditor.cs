using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using UnityEngine.Rendering;

namespace BioimageVR
{
    // segmentierung korrigieren, stufe 3
    // loeschen macht eine zelle zu hintergrund, verbinden legt zwei labels zusammen
    // teilen schneidet eine zelle entlang der ebene aus SplitPlaneTool in zwei
    // jede aenderung landet auf der undo liste, speichern schreibt lokal und schickt an den pc
    // textur wird nur im geaenderten bereich neu hochgeladen, sonst 35 mb pro klick
    [RequireComponent(typeof(VolumeView))]
    public class LabelEditor : MonoBehaviour
    {
        [SerializeField] private VrBridge vrBridge;
        [SerializeField] private SplitPlaneTool splitPlaneTool;
        [SerializeField] private int maxUndoSteps = 30;

        private VolumeView volumeView;

        // eine aenderung, genug um sie exakt zurueckzudrehen
        private class Edit
        {
            public string Description;
            public int[] Indices;
            public ushort[] OldValues;
            public CellInfo[] OldCells;
            public Vector3Int Min;
            public Vector3Int Max;
            // beim teilen entstandene zelle, beim rueckgaengig wieder leeren
            public int NewCellId;
        }

        private readonly LinkedList<Edit> undoStack = new LinkedList<Edit>();

        // zelle die mit der naechsten angeklickten verbunden wird, 0 wenn keine
        public int MergeSource { get; private set; }
        // zelle die gerade geteilt wird, die ebene zeigt SplitPlaneTool
        public int SplitSource { get; private set; }
        public int UnsavedChanges { get; private set; }
        public bool CanUndo => undoStack.Count > 0;
        public string LastMessage { get; private set; } = "";

        public event Action Changed;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
        }

        private void OnEnable() => volumeView.OnVolumeLoaded += HandleVolumeLoaded;

        private void OnDisable() => volumeView.OnVolumeLoaded -= HandleVolumeLoaded;

        // neues bild, alte aenderungen gehoeren nicht mehr dazu
        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            undoStack.Clear();
            MergeSource = 0;
            SplitSource = 0;
            UnsavedChanges = 0;
            SetMessage("");
        }

        private NiftiVolumeLoader.Volume Vol => volumeView.LoadedVolume;
        public bool Available => Vol?.Labels != null && Vol.Cells != null;

        // verbinden

        public void StartMerge()
        {
            int id = volumeView.SelectedLabel;
            if (!Available || id <= 0) return;
            SplitSource = 0;
            MergeSource = id;
            SetMessage($"Point at the second cell and press A · it will join Cell {id}");
        }

        public void CancelMerge()
        {
            if (MergeSource == 0) return;
            MergeSource = 0;
            SetMessage("Merge cancelled");
        }

        // teilen

        public void StartSplit()
        {
            int id = volumeView.SelectedLabel;
            if (!Available || id <= 0) return;
            MergeSource = 0;
            SplitSource = id;
            if (splitPlaneTool != null) splitPlaneTool.ResetPlane();
            SetMessage($"Point at Cell {id} · turn your wrist to tilt the plane · press A to split");
        }

        public void CancelSplit()
        {
            if (SplitSource == 0) return;
            SplitSource = 0;
            SetMessage("Split cancelled");
        }

        // CellInspector fragt vor jeder auswahl hier nach, true heisst klick verbraucht
        public bool TryHandleClick(int clickedId)
        {
            if (SplitSource > 0)
            {
                int source = SplitSource;
                if (splitPlaneTool == null || !splitPlaneTool.TryGetPlane(out Vector3 point, out Vector3 normal))
                {
                    SetMessage($"Point at Cell {source} first, then the plane appears");
                    return true;
                }
                SplitSource = 0;
                Split(source, point, normal);
                return true;
            }
            if (MergeSource == 0) return false;
            int target = MergeSource;
            MergeSource = 0;
            if (clickedId <= 0 || clickedId == target)
            {
                SetMessage("Merge cancelled");
                return true;
            }
            Merge(target, clickedId);
            return true;
        }

        // source verschwindet, ihre voxel bekommen die id von target
        public void Merge(int target, int source)
        {
            if (!Available || target <= 0 || source <= 0 || target == source) return;
            CellInfo t = Vol.Cells[target];
            CellInfo s = Vol.Cells[source];
            if (!t.Exists || !s.Exists) return;

            Edit edit = Replace(source, (ushort)target, $"Cell {source} merged into Cell {target}");
            if (edit == null) return;
            edit.OldCells = new[] { t, s };

            int n = t.VoxelCount + s.VoxelCount;
            Vol.Cells[target] = new CellInfo
            {
                Id = target,
                VoxelCount = n,
                Min = Vector3Int.Min(t.Min, s.Min),
                Max = Vector3Int.Max(t.Max, s.Max),
                Centroid = (t.Centroid * t.VoxelCount + s.Centroid * s.VoxelCount) / n,
                MeanIntensity = (t.MeanIntensity * t.VoxelCount + s.MeanIntensity * s.VoxelCount) / n,
            };
            Vol.Cells[source] = Empty(source);
            Vol.LabelCount--;

            Commit(edit);
            volumeView.RefreshSplitHints(target, source);
            volumeView.SelectedLabel = target;
        }

        // ebene aus weltkoordinaten, SplitPlaneTool
        public void Split(int id, Vector3 pointWorld, Vector3 normalWorld)
        {
            if (!Available) return;
            // ebene in voxel koordinaten, normale mit der transponierten matrix wegen der
            // nicht gleichmaessigen skalierung des wuerfels
            Vector3 pointLocal = transform.InverseTransformPoint(pointWorld);
            Vector3 normalLocal = transform.localToWorldMatrix.transpose.MultiplyVector(normalWorld);
            var size = new Vector3(Vol.SizeX, Vol.SizeY, Vol.SizeZ);
            Vector3 pointVoxel = Vector3.Scale(pointLocal + Vector3.one * 0.5f, size) - Vector3.one * 0.5f;
            var normalVoxel = new Vector3(normalLocal.x / Vol.SizeX, normalLocal.y / Vol.SizeY, normalLocal.z / Vol.SizeZ);
            SplitVoxel(id, pointVoxel, normalVoxel);
        }

        // vorschlag aus CellShapeAnalysis, ebene durch die engstelle senkrecht zur
        // verbindung der beiden blob mitten, senkrecht in um gemessen
        public void SplitAtHint(int id)
        {
            SplitHint hint = volumeView.GetSplitHint(id);
            if (!Available || !hint.Found) return;
            Vector3 spacing = Vol.VoxelSize;
            Vector3 axisPhysical = Vector3.Scale(hint.PeakB - hint.PeakA, spacing);
            MergeSource = 0;
            SplitSource = 0;
            SplitVoxel(id, hint.NeckPoint, Vector3.Scale(axisPhysical, spacing));
        }

        // alle voxel auf der seite in normalenrichtung bekommen eine neue id
        private void SplitVoxel(int id, Vector3 pointVoxel, Vector3 normalVoxel)
        {
            if (!Available || id <= 0 || !Vol.Cells[id].Exists) return;
            if (Vol.MaxLabel >= ushort.MaxValue)
            {
                SetMessage("No free cell number left");
                return;
            }

            CellInfo cell = Vol.Cells[id];
            int newId = Vol.MaxLabel + 1;
            ushort[] labels = Vol.Labels;
            var indices = new List<int>();
            for (int z = cell.Min.z; z <= cell.Max.z; z++)
            for (int y = cell.Min.y; y <= cell.Max.y; y++)
            {
                int row = Vol.SizeX * (y + Vol.SizeY * z);
                for (int x = cell.Min.x; x <= cell.Max.x; x++)
                {
                    int i = row + x;
                    if (labels[i] != id) continue;
                    if (Vector3.Dot(new Vector3(x, y, z) - pointVoxel, normalVoxel) <= 0f) continue;
                    indices.Add(i);
                }
            }

            if (indices.Count == 0 || indices.Count == cell.VoxelCount)
            {
                // ebene liegt neben der zelle, modus bleibt an zum neu ausrichten
                SplitSource = id;
                SetMessage("The plane misses the cell · aim again");
                return;
            }

            foreach (int i in indices) labels[i] = (ushort)newId;
            var old = new ushort[indices.Count];
            for (int i = 0; i < old.Length; i++) old[i] = (ushort)id;

            if (newId >= Vol.Cells.Length)
            {
                int oldLength = Vol.Cells.Length;
                CellInfo[] grown = Vol.Cells;
                Array.Resize(ref grown, Mathf.Max(newId + 1, oldLength * 2));
                for (int i = oldLength; i < grown.Length; i++) grown[i] = Empty(i);
                Vol.Cells = grown;
            }
            Vol.MaxLabel = newId;
            Vol.Cells[id] = CellStats.ComputeCell(Vol, id, cell.Min, cell.Max);
            Vol.Cells[newId] = CellStats.ComputeCell(Vol, newId, cell.Min, cell.Max);
            Vol.LabelCount++;

            Commit(new Edit
            {
                Description = $"Cell {id} split · the new half is Cell {newId}",
                Indices = indices.ToArray(),
                OldValues = old,
                OldCells = new[] { cell },
                Min = cell.Min,
                Max = cell.Max,
                NewCellId = newId,
            });
            volumeView.RefreshSplitHints(id, newId);
            volumeView.SelectedLabel = id;
        }

        // loeschen

        public void DeleteSelected()
        {
            int id = volumeView.SelectedLabel;
            if (!Available || id <= 0 || !Vol.Cells[id].Exists) return;

            Edit edit = Replace(id, 0, $"Cell {id} deleted");
            if (edit == null) return;
            edit.OldCells = new[] { Vol.Cells[id] };
            Vol.Cells[id] = Empty(id);
            Vol.LabelCount--;

            Commit(edit);
            volumeView.RefreshSplitHints(id);
            volumeView.SelectedLabel = 0;
        }

        // rueckgaengig

        public void Undo()
        {
            if (!Available || undoStack.Count == 0) return;
            Edit edit = undoStack.Last.Value;
            undoStack.RemoveLast();

            ushort[] labels = Vol.Labels;
            for (int i = 0; i < edit.Indices.Length; i++) labels[edit.Indices[i]] = edit.OldValues[i];
            foreach (CellInfo cell in edit.OldCells)
            {
                if (!Vol.Cells[cell.Id].Exists && cell.Exists) Vol.LabelCount++;
                Vol.Cells[cell.Id] = cell;
            }
            if (edit.NewCellId > 0)
            {
                if (Vol.Cells[edit.NewCellId].Exists) Vol.LabelCount--;
                Vol.Cells[edit.NewCellId] = Empty(edit.NewCellId);
            }
            UploadRegion(edit.Min, edit.Max);
            var affected = new List<int>();
            foreach (CellInfo cell in edit.OldCells) affected.Add(cell.Id);
            if (edit.NewCellId > 0) affected.Add(edit.NewCellId);
            volumeView.RefreshSplitHints(affected.ToArray());

            UnsavedChanges = Mathf.Max(0, UnsavedChanges - 1);
            MergeSource = 0;
            SplitSource = 0;
            volumeView.NotifyLabelsChanged();
            volumeView.SelectedLabel = edit.OldCells[0].Id;
            SetMessage($"Undone: {edit.Description}");
        }

        // alle voxel einer id im begrenzungsrahmen der zelle ersetzen
        private Edit Replace(int fromId, ushort toId, string description)
        {
            CellInfo cell = Vol.Cells[fromId];
            int sx = Vol.SizeX, sy = Vol.SizeY;
            ushort[] labels = Vol.Labels;
            var indices = new List<int>(cell.VoxelCount);

            for (int z = cell.Min.z; z <= cell.Max.z; z++)
            for (int y = cell.Min.y; y <= cell.Max.y; y++)
            {
                int row = sx * (y + sy * z);
                for (int x = cell.Min.x; x <= cell.Max.x; x++)
                {
                    int i = row + x;
                    if (labels[i] != fromId) continue;
                    indices.Add(i);
                    labels[i] = toId;
                }
            }
            if (indices.Count == 0) return null;

            var old = new ushort[indices.Count];
            for (int i = 0; i < old.Length; i++) old[i] = (ushort)fromId;
            return new Edit
            {
                Description = description,
                Indices = indices.ToArray(),
                OldValues = old,
                Min = cell.Min,
                Max = cell.Max,
            };
        }

        private void Commit(Edit edit)
        {
            UploadRegion(edit.Min, edit.Max);
            undoStack.AddLast(edit);
            while (undoStack.Count > maxUndoSteps) undoStack.RemoveFirst();
            UnsavedChanges++;
            volumeView.NotifyLabelsChanged();
            SetMessage(edit.Description);
        }

        private static CellInfo Empty(int id) => new CellInfo
        {
            Id = id,
            Min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue),
            Max = new Vector3Int(-1, -1, -1),
        };

        // textur

        // kleine 3d textur nur fuer den rahmen, dann schichtweise per CopyTexture hinein
        // wo die gpu keine 3d kopien kann, die ganze textur neu bauen
        private void UploadRegion(Vector3Int min, Vector3Int max)
        {
            Texture3D target = Vol.LabelTexture;
            if (target == null) return;
            int w = max.x - min.x + 1, h = max.y - min.y + 1, d = max.z - min.z + 1;
            if (w <= 0 || h <= 0 || d <= 0) return;

            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Copy3D) == 0)
            {
                RebuildTexture();
                return;
            }

            var rg = new byte[w * h * d * 2];
            ushort[] labels = Vol.Labels;
            int k = 0;
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            {
                int row = Vol.SizeX * (y + Vol.SizeY * z);
                for (int x = min.x; x <= max.x; x++)
                {
                    ushort v = labels[row + x];
                    rg[k++] = (byte)(v & 0xFF);
                    rg[k++] = (byte)(v >> 8);
                }
            }

            var patch = new Texture3D(w, h, d, TextureFormat.RG16, false) { filterMode = FilterMode.Point };
            patch.SetPixelData(rg, 0);
            patch.Apply(false);
            for (int z = 0; z < d; z++)
                Graphics.CopyTexture(patch, z, 0, 0, 0, w, h, target, min.z + z, 0, min.x, min.y);
            Destroy(patch);
        }

        private void RebuildTexture()
        {
            var raw = new NiftiVolumeLoader.RawLabelData
            {
                SizeX = Vol.SizeX,
                SizeY = Vol.SizeY,
                SizeZ = Vol.SizeZ,
                Labels = Vol.Labels,
                MaxLabel = Vol.MaxLabel,
            };
            Texture3D old = Vol.LabelTexture;
            Vol.LabelTexture = NiftiVolumeLoader.BuildLabelTexture(raw);
            volumeView.RebindLabelTexture();
            Destroy(old);
        }

        // speichern

        // lokal ueberschreiben, damit die korrektur beim naechsten oeffnen noch da ist
        // das original bleibt einmalig als _labels_v001 daneben liegen
        // danach als neue version an den pc, napari holt sie mit From VR
        public void Save()
        {
            if (!Available) return;
            string volumePath = volumeView.ResolvedVolumePath;
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string labelsPath = Path.Combine(dir, $"{stem}_labels.nii.gz");
            string backupPath = Path.Combine(dir, $"{stem}_labels_v001.nii.gz");

            byte[] nifti;
            try
            {
                nifti = EncodeNifti(Vol.Labels, Vol.SizeX, Vol.SizeY, Vol.SizeZ);
                if (File.Exists(labelsPath) && !File.Exists(backupPath)) File.Copy(labelsPath, backupPath);
                File.WriteAllBytes(labelsPath, nifti);
            }
            catch (Exception e)
            {
                SetMessage($"Saving failed: {e.Message}");
                return;
            }

            UnsavedChanges = 0;
            SetMessage("Saved on the headset · sending to the PC ...");
            if (vrBridge == null)
            {
                SetMessage("Saved on the headset · no PC connected");
                return;
            }
            vrBridge.UploadLabels(stem, nifti, error => SetMessage(error == null
                ? "Saved here and on the PC · get it in napari with From VR"
                : $"Saved on the headset · PC not reachable: {error}"));
        }

        // minimaler nifti-1 header, uint16, gleiches layout wie convert_labels.py
        private static byte[] EncodeNifti(ushort[] labels, int sx, int sy, int sz)
        {
            const int headerSize = 348;
            const int dataOffset = 352;
            var header = new byte[dataOffset];
            void Int32At(int offset, int value) => BitConverter.GetBytes(value).CopyTo(header, offset);
            void Int16At(int offset, short value) => BitConverter.GetBytes(value).CopyTo(header, offset);
            void FloatAt(int offset, float value) => BitConverter.GetBytes(value).CopyTo(header, offset);

            Int32At(0, headerSize);
            short[] dim = { 3, (short)sx, (short)sy, (short)sz, 1, 1, 1, 1 };
            for (int i = 0; i < 8; i++) Int16At(40 + i * 2, dim[i]);
            Int16At(70, 512);
            Int16At(72, 16);
            for (int i = 0; i < 8; i++) FloatAt(76 + i * 4, 1f);
            FloatAt(108, dataOffset);
            header[344] = (byte)'n';
            header[345] = (byte)'+';
            header[346] = (byte)'1';

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest))
            {
                gzip.Write(header, 0, header.Length);
                var data = new byte[labels.Length * 2];
                Buffer.BlockCopy(labels, 0, data, 0, data.Length);
                gzip.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        private void SetMessage(string message)
        {
            LastMessage = message;
            Changed?.Invoke();
        }
    }
}

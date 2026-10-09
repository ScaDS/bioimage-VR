using System;
using System.IO;
using UnityEngine;

namespace BioimageVR
{
    // vorberechneter flugweg durch einen hohlraum, z.b. nase bis luftroehre
    // kommt aus <name>_flypath.json neben dem volumen, siehe preprocessing/find_fly_path.py
    // faehrt wie auf einer schiene: das volumen rutscht so, dass der wegpunkt beim nutzer bleibt
    // und dreht sich beim fahren so, dass der gang nach vorn zeigt
    // die groesse passt sich unterwegs an, enge stellen werden so weit vergroessert dass der
    // nutzer hineinpasst, und der kopf bleibt im freien teil des gangs: lehnt man sich zu
    // weit zur wand, rutscht das volumen mit statt dass man im gewebe landet
    // am ende des wegs, vorn wie hinten, fliegt man wieder hinaus: das volumen schrumpft und
    // geht zurueck an den platz von vor dem einsteigen, man sieht es wieder ganz von aussen
    // gesteuert von VolumeFly im modus Path, hier liegen nur weg, stationen und die fahrt
    [RequireComponent(typeof(VolumeView))]
    public class FlyPath : MonoBehaviour
    {
        // so breit soll der gang im raum wirken, meter, startwert, linker stick aendert es
        [SerializeField] private float comfortableWidth = 0.7f;
        // so weit darf der kopf von der gangmitte weg, anteil am freien radius
        [SerializeField] private float headRoom = 0.5f;
        // wie zuegig das volumen nachrutscht wenn der kopf zu weit aussen ist, pro sekunde
        [SerializeField] private float headFollow = 3f;
        // wie schnell die groesse nachzieht, faktor pro sekunde
        [SerializeField] private float scaleRate = 1.6f;
        // anflug beim einsteigen, drehen in grad pro sekunde und wie zuegig der start herankommt
        [SerializeField] private float entryAlignSpeed = 110f;
        [SerializeField] private float entryApproach = 1.6f;
        // wie schnell das volumen beim hinausfliegen schrumpft, faktor pro sekunde
        [SerializeField] private float exitScaleRate = 2.2f;
        // wie schnell sich das volumen beim fahren nach dem gang ausrichtet, grad pro sekunde
        [SerializeField] private float alignSpeed = 70f;
        [SerializeField] private float lineWidth = 0.004f;
        // tempo der automatischen fahrt zur naechsten station, meter pro sekunde im raum
        // langsam genug um sich unterwegs umzuschauen
        [SerializeField] private float autoSpeed = 0.18f;

        [Serializable] private class PointJson { public float x, y, z, radius; }
        [Serializable] private class StationJson { public int index; public string kind; public string label; public float distance; }
        [Serializable] private class PathJson
        {
            public string unit;
            public float length;
            public float threshold;
            public PointJson[] points;
            public StationJson[] stations;
        }

        private VolumeView volumeView;
        private VolumeZoom zoom;
        private Vector3[] local;
        private float[] radius;
        private StationJson[] stations = Array.Empty<StationJson>();
        private string unit = "";
        private float totalLength;
        // schwelle mit der der weg gerechnet wurde, 0 wenn die datei sie nicht nennt
        private float pathThreshold;
        // schwelle des nutzers vor der fahrt, kommt danach zurueck, unter 0 heisst nichts gemerkt
        private float userThreshold = -1f;
        private VolumeContrastControl contrast;

        private LineRenderer line;
        private Vector3[] lineBuffer;
        private WorldTooltip stationLabel;

        // position auf dem weg als punktindex mit bruchteil
        private float s;
        // dahin soll der gang zeigen, waagerecht, beim einsteigen aus der blickrichtung
        private Vector3 forward = Vector3.forward;
        private bool riding;
        // anflug laeuft noch, sticks warten solange
        private bool entering;
        private float enteringSince;
        // rueckflug nach draussen, lage und groesse von vor dem einsteigen
        private bool leaving;
        private float leavingSince;
        private Vector3 startPosition;
        private Quaternion startRotation;
        private float startScale;
        // weg zu ende gefahren und wieder draussen, VolumeFly schaltet dann zurueck auf Move
        public event Action Exited;
        private float targetWidth;
        private float medianRadius;
        // ziel der automatischen fahrt als punktindex, unter 0 heisst keine
        private float autoTarget = -1f;
        private int autoStation = -1;

        public bool AutoRiding => riding && autoTarget >= 0f;
        // tempo stufe aus der leiste, gilt fuer die automatische fahrt, siehe VolumeFly.SpeedFactor
        public float SpeedFactor { get; set; } = 1f;
        public bool Entering => riding && entering;
        public bool Leaving => riding && leaving;

        public bool HasPath => local != null && local.Length >= 2;
        public int StationCount => stations.Length;

        // fuer die karte, siehe FlyMap
        public bool Riding => riding;
        public Vector3[] LocalPoints => local;
        public Vector3 CurrentLocal => LocalAt(s);
        public int StationPointIndex(int station) => stations[station].index;

        private void Awake()
        {
            volumeView = GetComponent<VolumeView>();
            zoom = GetComponent<VolumeZoom>();
        }

        private void OnEnable()
        {
            volumeView.OnVolumeLoaded += HandleVolumeLoaded;
            if (volumeView.LoadedVolume != null) HandleVolumeLoaded(volumeView.LoadedVolume);
        }

        private void OnDisable()
        {
            volumeView.OnVolumeLoaded -= HandleVolumeLoaded;
        }

        private void OnDestroy()
        {
            if (line != null) Destroy(line.gameObject);
            if (stationLabel != null) Destroy(stationLabel.gameObject);
        }

        private void HandleVolumeLoaded(NiftiVolumeLoader.Volume volume)
        {
            local = null;
            stations = Array.Empty<StationJson>();
            riding = false;
            leaving = false;
            autoTarget = -1f;
            // neues bild startet mit dem standard, nichts zurueckzustellen
            userThreshold = -1f;
            if (line != null) line.gameObject.SetActive(false);
            if (stationLabel != null) stationLabel.Hide();

            string volumePath = volumeView.ResolvedVolumePath;
            if (string.IsNullOrEmpty(volumePath)) return;
            string dir = Path.GetDirectoryName(volumePath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(volumePath));
            string path = Path.Combine(dir, $"{stem}_flypath.json");
            if (!File.Exists(path)) return;

            try
            {
                var parsed = JsonUtility.FromJson<PathJson>(File.ReadAllText(path));
                if (parsed?.points == null || parsed.points.Length < 2) return;
                local = new Vector3[parsed.points.Length];
                radius = new float[parsed.points.Length];
                for (int i = 0; i < local.Length; i++)
                {
                    PointJson p = parsed.points[i];
                    // voxelmitte, deshalb der halbe voxel
                    local[i] = volumeView.VoxelToLocal(new Vector3(p.x + 0.5f, p.y + 0.5f, p.z + 0.5f));
                    radius[i] = p.radius;
                }
                float[] sorted = (float[])radius.Clone();
                Array.Sort(sorted);
                medianRadius = sorted[sorted.Length / 2];
                stations = parsed.stations ?? Array.Empty<StationJson>();
                unit = parsed.unit == "um" ? "µm" : parsed.unit ?? "";
                totalLength = parsed.length;
                pathThreshold = parsed.threshold;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FlyPath: '{path}' nicht lesbar: {e.Message}");
                local = null;
            }
        }

        // weg

        private Vector3 LocalAt(float t)
        {
            t = Mathf.Clamp(t, 0f, local.Length - 1);
            int i = Mathf.Min((int)t, local.Length - 2);
            return Vector3.Lerp(local[i], local[i + 1], t - i);
        }

        private Vector3 WorldAt(float t) => transform.TransformPoint(LocalAt(t));

        // richtung des gangs, ueber ein paar punkte gemittelt damit es nicht zittert
        private Vector3 TangentAt(float t)
        {
            Vector3 d = WorldAt(t + 4f) - WorldAt(t - 4f);
            return d.sqrMagnitude > 1e-10f ? d.normalized : forward;
        }

        // meter im raum pro wegpunkt an dieser stelle
        private float WorldSpacing(float t)
        {
            float a = Mathf.Max(t - 1f, 0f);
            float b = Mathf.Min(t + 1f, local.Length - 1);
            return Mathf.Max((WorldAt(b) - WorldAt(a)).magnitude / Mathf.Max(b - a, 1e-3f), 1e-5f);
        }

        // freier radius in der einheit des volumens, geglaettet damit die groesse nicht pumpt
        private float RadiusAt(float t, int window)
        {
            int center = Mathf.Clamp(Mathf.RoundToInt(t), 0, radius.Length - 1);
            int lo = Mathf.Max(center - window, 0);
            int hi = Mathf.Min(center + window, radius.Length - 1);
            float sum = 0f;
            for (int i = lo; i <= hi; i++) sum += radius[i];
            return sum / (hi - lo + 1);
        }

        // meter im raum pro einheit des volumens, z.b. pro mm
        private float WorldPerUnit()
        {
            NiftiVolumeLoader.Volume vol = volumeView.LoadedVolume;
            return vol == null ? 0f : transform.localScale.x / (vol.SizeX * vol.VoxelSize.x);
        }

        // enge stellen auf die wunschbreite vergroessern, weite stellen bleiben weit
        // true wenn die groesse schon passt
        private bool FitScale(float maxFactor)
        {
            if (zoom == null) return true;
            float current = 2f * Mathf.Min(RadiusAt(s, 5), medianRadius) * WorldPerUnit();
            if (current < 1e-6f) return true;
            float wanted = targetWidth / current;
            if (Mathf.Abs(wanted - 1f) < 1e-3f) return true;
            zoom.ScaleAround(WorldAt(s), Mathf.Clamp(wanted, 1f / maxFactor, maxFactor));
            return Mathf.Abs(wanted - 1f) < 0.02f;
        }

        // kopf im freien teil des gangs halten, das volumen rutscht nach
        // weich nachziehen statt hart begrenzen: der freie radius schwankt von punkt zu punkt,
        // an jeder kleinen wandnase ruckte sonst das ganze bild
        private void KeepHeadInside(Vector3 head, float dt)
        {
            Vector3 offset = head - WorldAt(s);
            float allowed = headRoom * RadiusAt(s, 5) * WorldPerUnit();
            float distance = offset.magnitude;
            if (distance <= allowed || distance < 1e-5f) return;
            float excess = distance - allowed;
            transform.position += offset / distance * (excess * (1f - Mathf.Exp(-headFollow * dt)));
        }

        // linker stick, gang breiter oder schmaler
        public void ChangeWidth(float factor)
        {
            targetWidth = Mathf.Clamp(targetWidth * factor, 0.25f, 4f);
        }

        // fahrt

        // einsteigen: drinnen an der naechsten stelle des wegs, von aussen am anfang des wegs
        // kein sprung, das volumen dreht sich, kommt heran und waechst, siehe StepEntry
        public void Begin(Camera cam, bool cameraInside)
        {
            if (!HasPath) return;
            forward = Flat(cam.transform.forward, cam.transform.up);
            s = cameraInside ? Nearest(cam.transform.position) : 0f;
            targetWidth = comfortableWidth;
            // weg gilt nur fuer seine schwelle. bei niedrigerer zaehlt im surface modus rauschen
            // als wand, man fliegt in einen massiven block. fuer die fahrt umstellen, danach zurueck
            if (contrast == null) contrast = FindFirstObjectByType<VolumeContrastControl>();
            if (contrast != null && pathThreshold > 0f)
            {
                if (userThreshold < 0f) userThreshold = contrast.CurrentThreshold;
                contrast.SetThresholdExternal(pathThreshold);
            }
            // steckt man schon drin, gibt es keine sinnvolle aussenansicht zum zurueckkehren
            if (!cameraInside || startScale <= 0f)
            {
                startPosition = transform.position;
                startRotation = transform.rotation;
                startScale = transform.localScale.x;
            }
            riding = true;
            leaving = false;
            entering = true;
            enteringSince = Time.time;
            autoTarget = -1f;
        }

        private void RestoreThreshold()
        {
            if (contrast != null && userThreshold >= 0f) contrast.SetThresholdExternal(userThreshold);
            userThreshold = -1f;
        }

        public void End()
        {
            RestoreThreshold();
            riding = false;
            entering = false;
            leaving = false;
            autoTarget = -1f;
        }

        // rueckflug: schrumpfen um die eigene mitte und zurueck an den alten platz
        private void StepExit(float dt)
        {
            float wanted = startScale / Mathf.Max(transform.localScale.x, 1e-6f);
            if (zoom != null && Mathf.Abs(wanted - 1f) > 1e-3f)
            {
                float limit = 1f + exitScaleRate * dt;
                zoom.ScaleAround(transform.position, Mathf.Clamp(wanted, 1f / limit, limit));
            }
            float k = 1f - Mathf.Exp(-1.4f * dt);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, startPosition, k),
                Quaternion.Slerp(transform.rotation, startRotation, k));

            bool home = Mathf.Abs(wanted - 1f) < 0.02f
                        && (transform.position - startPosition).sqrMagnitude < 0.0004f
                        && Quaternion.Angle(transform.rotation, startRotation) < 1f;
            if (!home && Time.time - leavingSince < 10f) return;
            leaving = false;
            riding = false;
            RestoreThreshold();
            Exited?.Invoke();
        }

        private void StartLeaving()
        {
            autoTarget = -1f;
            leaving = true;
            leavingSince = Time.time;
        }

        // anflug: erst ausrichten solange das volumen noch klein ist, dann wachsen,
        // sonst schwenkt ein riesiges volumen durch den nutzer. danach langsam zum eingang
        private void StepEntry(Vector3 head, float dt)
        {
            float angle = Vector3.Angle(TangentAt(s), forward);
            Align(entryAlignSpeed * dt);
            Vector3 offset = head - WorldAt(s);
            transform.position += offset * (1f - Mathf.Exp(-entryApproach * dt));
            bool sized = angle < 25f && FitScale(1f + scaleRate * dt);

            bool arrived = angle < 2f && sized && offset.magnitude < 0.05f;
            if (!arrived && Time.time - enteringSince < 10f) return;
            entering = false;
            int entrance = Array.FindIndex(stations, station => station.kind == "entrance");
            if (entrance >= 0 && stations[entrance].index > s + 1f)
            {
                autoRamp = 0f;
                autoStation = entrance;
                autoTarget = Mathf.Clamp(stations[entrance].index, 0, local.Length - 1);
            }
        }

        // stick vor und zurueck, strecke in metern im raum, uebernimmt von der automatischen fahrt
        public void Ride(float meters, float dt)
        {
            if (entering || leaving) return;
            autoTarget = -1f;
            Advance(meters, dt);
        }

        private void Update()
        {
            if (!riding || !HasPath) return;
            float dt = Time.deltaTime;
            if (entering)
            {
                if (Camera.main != null) StepEntry(Camera.main.transform.position, dt);
                return;
            }
            if (leaving)
            {
                StepExit(dt);
                return;
            }
            FitScale(1f + scaleRate * dt);
            if (Camera.main != null) KeepHeadInside(Camera.main.transform.position, dt);

            // automatische fahrt, sanft anfahren und vor dem ziel abbremsen
            if (!AutoRiding) return;
            float remaining = Mathf.Abs(autoTarget - s) * WorldSpacing(s);
            autoRamp = Mathf.MoveTowards(autoRamp, 1f, dt / 1.5f);
            float speed = autoSpeed * SpeedFactor * autoRamp * Mathf.Clamp(remaining / 0.4f, 0.15f, 1f);
            float before = s;
            float direction = Mathf.Sign(autoTarget - s);
            Advance(direction * speed * dt, dt);
            if ((autoTarget - s) * direction <= 0.02f || Mathf.Approximately(s, before)) autoTarget = -1f;
        }

        private float autoRamp;

        private void Advance(float meters, float dt)
        {
            if (!HasPath || meters == 0f) return;
            Vector3 before = WorldAt(s);
            float last = local.Length - 1;
            s = Mathf.Clamp(s + meters / WorldSpacing(s), 0f, last);
            transform.position -= WorldAt(s) - before;
            Align(alignSpeed * dt);
            // an einem der beiden enden angekommen, wieder hinaus zur aussenansicht
            if ((meters > 0f && s >= last - 1e-3f) || (meters < 0f && s <= 1e-3f)) StartLeaving();
        }

        // nutzer dreht sich, der gang soll danach in die neue richtung zeigen
        public void Turn(Vector3 head, float degrees)
        {
            if (!HasPath || entering || leaving) return;
            transform.RotateAround(head, Vector3.up, degrees);
            forward = Quaternion.AngleAxis(degrees, Vector3.up) * forward;
        }

        // langsam zur naechsten oder vorigen station fahren statt zu springen
        // nochmal druecken waehrend der fahrt nimmt die station danach
        public void RideToStation(int direction)
        {
            if (!HasPath || entering || leaving) return;
            float from = AutoRiding ? autoTarget : s;
            int target = -1;
            if (direction > 0)
            {
                for (int i = 0; i < stations.Length; i++)
                    if (stations[i].index > from + 1f) { target = i; break; }
            }
            else
            {
                for (int i = stations.Length - 1; i >= 0; i--)
                    if (stations[i].index < from - 1f) { target = i; break; }
            }
            if (!AutoRiding) autoRamp = 0f;
            autoStation = target;
            // keine station mehr in der richtung, dann bis ans ende des wegs und hinaus
            autoTarget = target >= 0 ? Mathf.Clamp(stations[target].index, 0, local.Length - 1)
                : direction > 0 ? local.Length - 1 : 0f;
        }

        // volumen um den aktuellen wegpunkt drehen bis der gang nach vorn zeigt
        private void Align(float maxDegrees)
        {
            Quaternion.FromToRotation(TangentAt(s), forward).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle < 0.05f || float.IsNaN(axis.x)) return;
            transform.RotateAround(WorldAt(s), axis, Mathf.Min(angle, maxDegrees));
        }

        private float Nearest(Vector3 world)
        {
            Vector3 p = transform.InverseTransformPoint(world);
            int best = 0;
            float bestDist = float.MaxValue;
            for (int i = 0; i < local.Length; i++)
            {
                float d = (local[i] - p).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            if (v.sqrMagnitude < 1e-4f) { v = fallback; v.y = 0f; }
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        // anzeige

        // fuer die hinweiszeile, wo man ist und was als naechstes kommt
        public string Describe()
        {
            if (!HasPath) return "";
            float done = totalLength * s / (local.Length - 1);
            string text = $"{done:0} of {totalLength:0} {unit}";
            if (entering) return "flying to the start of the path";
            if (AutoRiding && autoStation < 0) return text + " · riding to the end of the path, then back out";
            if (AutoRiding && autoStation >= 0 && autoStation < stations.Length)
            {
                float left = Mathf.Abs(stations[autoStation].distance - done);
                return text + $" · riding to {stations[autoStation].label}, {left:0} {unit} to go";
            }
            int next = NextStation();
            if (next >= 0)
            {
                float ahead = stations[next].distance - done;
                text += $" · next: {stations[next].label} in {Mathf.Max(ahead, 0f):0} {unit}";
            }
            return text;
        }

        private int NextStation()
        {
            for (int i = 0; i < stations.Length; i++)
                if (stations[i].index > s + 1f) return i;
            return -1;
        }

        private void LateUpdate()
        {
            if (!HasPath)
            {
                if (line != null && line.gameObject.activeSelf) line.gameObject.SetActive(false);
                return;
            }

            // eigenes root objekt in weltkoordinaten, sonst verzerrt die volumen skalierung die breite
            if (line == null)
            {
                var go = new GameObject("FlyPathLine", typeof(LineRenderer));
                line = go.GetComponent<LineRenderer>();
                line.useWorldSpace = true;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null) line.material = new Material(shader);
                Color c = UITheme.AccentSecondary;
                line.startColor = line.endColor = new Color(c.r, c.g, c.b, 0.7f);
                line.numCornerVertices = 2;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (!line.gameObject.activeSelf) line.gameObject.SetActive(true);
            if (lineBuffer == null || lineBuffer.Length != local.Length) lineBuffer = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++) lineBuffer[i] = transform.TransformPoint(local[i]);
            line.positionCount = lineBuffer.Length;
            line.SetPositions(lineBuffer);
            line.widthMultiplier = lineWidth;

            // schild an der naechsten station, nur waehrend der fahrt
            int next = !riding ? -1 : AutoRiding ? autoStation : NextStation();
            if (next < 0)
            {
                if (stationLabel != null) stationLabel.Hide();
                return;
            }
            if (stationLabel == null) stationLabel = WorldTooltip.Create("FlyStationTooltip");
            stationLabel.Show(WorldTooltip.Title(stations[next].label), WorldAt(stations[next].index));
        }
    }
}

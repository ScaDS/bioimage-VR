using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace BioimageVR.EditorSetup
{
    // tmp schriften aus Assets/Fonts bauen, nach Resources/Fonts & Materials, dort findet UITheme.Font sie
    // beim ersten lauf fehlen die tmp grundressourcen (shader, einstellungen), die werden dann
    // importiert und man muss einmal neu starten, danach laeuft alles in einem rutsch
    // menu: BioimageVR Setup Fonts, laeuft auch am anfang von Setup Side Panel mit
    public static class SetupFonts
    {
        private const string SourceDir = "Assets/Fonts";
        // dort sucht tmp auch fuer <font="..."> tags im text
        private const string TargetDir = "Assets/Resources/Fonts & Materials";
        private const string EssentialsFallback = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset";

        [MenuItem("BioimageVR/Setup Fonts")]
        public static void RunFromMenu()
        {
            if (Run()) Debug.Log("BioimageVR: Schriften bereit unter " + TargetDir);
        }

        // true wenn alle schriften da sind, false wenn erst tmp importiert werden musste
        public static bool Run()
        {
            if (Shader.Find("TextMeshPro/Distance Field") == null || TMP_Settings.instance == null)
            {
                TMP_PackageResourceImporter.ImportResources(true, false, false);
                Debug.LogWarning("BioimageVR: TextMeshPro Grundressourcen werden importiert. Danach den Menuepunkt bitte noch einmal ausfuehren.");
                return false;
            }

            Directory.CreateDirectory(TargetDir);
            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(EssentialsFallback);

            foreach (UITheme.FontRole role in System.Enum.GetValues(typeof(UITheme.FontRole)))
            {
                string assetName = UITheme.FontAssetName(role);
                string ttf = $"{SourceDir}/{assetName.Replace(" SDF", "")}.ttf";
                string path = $"{TargetDir}/{assetName}.asset";
                var font = AssetDatabase.LoadAssetAtPath<Font>(ttf);
                if (font == null)
                {
                    Debug.LogError($"BioimageVR: Schrift fehlt: {ttf}");
                    continue;
                }
                var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (asset == null)
                {
                    // dynamisch, zeichen kommen bei bedarf dazu, auch umlaute und µ ³ Ø
                    asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                        AtlasPopulationMode.Dynamic);
                    asset.name = assetName;
                    AssetDatabase.CreateAsset(asset, path);
                    asset.atlasTextures[0].name = assetName + " Atlas";
                    AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
                    asset.material.name = assetName + " Material";
                    AssetDatabase.AddObjectToAsset(asset.material, asset);
                    if (role == UITheme.FontRole.Icons) asset.TryAddCharacters(Icons.All);
                }

                // ersatzschrift fuer zeichen die figtree oder plex nicht haben, bei neuen assets ist die liste noch null
                if (role != UITheme.FontRole.Icons && fallback != null)
                {
                    if (asset.fallbackFontAssetTable == null) asset.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
                    if (!asset.fallbackFontAssetTable.Contains(fallback)) asset.fallbackFontAssetTable.Add(fallback);
                }
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();

            // standardschrift fuer alles was tmp ohne eigene angabe baut
            var regular = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>($"{TargetDir}/{UITheme.FontAssetName(UITheme.FontRole.Regular)}.asset");
            if (regular != null && TMP_Settings.instance != null)
            {
                var so = new SerializedObject(TMP_Settings.instance);
                SerializedProperty prop = so.FindProperty("m_defaultFontAsset");
                if (prop != null)
                {
                    prop.objectReferenceValue = regular;
                    so.ApplyModifiedProperties();
                }
            }
            return true;
        }
    }
}

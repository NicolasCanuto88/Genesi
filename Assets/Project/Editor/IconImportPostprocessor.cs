using UnityEditor;
using UnityEngine;

/// <summary>
/// IconImportPostprocessor — thread Icone UI, lotto pilota (Q8-a).
///
/// Applica le impostazioni di import a tutti i PNG sotto
/// Assets/Project/Art/UI/Icons/ (ricorsivo). I master SVG stanno in
/// Icons/_Source~/: Unity ignora le cartelle che finiscono con "~", quindi qui
/// non arrivano mai.
///
/// Regole (ratificate nel workshop di stile):
///   - Sprite singolo, mesh FullRect, niente physics shape (Q7-a: PNG a runtime).
///   - Mipmap attive: sui canvas World Space le icone si rimpiccioliscono con la
///     distanza; senza mip sfarfallano. I PNG sono esportati a multipli della
///     griglia (M 192, S 64, radar 96), così i livelli di mip cadono su pixel
///     interi della griglia.
///   - Bilineare (la camera da seduti è ferma: niente popping tra livelli), clamp.
///   - PNG bianchi con alfa (Q3-a): il colore arriva da Image.color.
///   - Dimensione massima per taglio: suffisso "_s" → 64, altrimenti 256.
///
/// Le regole si riapplicano a ogni reimport. Se cambiano, incrementare
/// RulesVersion: Unity reimporta da solo gli asset interessati.
/// </summary>
public class IconImportPostprocessor : AssetPostprocessor
{
    private const string IconRoot = "Assets/Project/Art/UI/Icons/";
    private const uint RulesVersion = 1;

    private const int MaxSizeSmallCut = 64;
    private const int MaxSizeDefault = 256;

    private static readonly bool LogVerbose = false;

    public override uint GetVersion() => RulesVersion;

    private void OnPreprocessTexture()
    {
        string path = assetPath.Replace('\\', '/');
        if (!path.StartsWith(IconRoot)) return;
        if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) return;

        var importer = (TextureImporter)assetImporter;
        bool smallCut = System.IO.Path.GetFileNameWithoutExtension(path).EndsWith("_s");

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = true;
        importer.mipmapFilter = TextureImporterMipFilter.BoxFilter;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.maxTextureSize = smallCut ? MaxSizeSmallCut : MaxSizeDefault;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;

        // Campi sprite non esposti direttamente su TextureImporter.
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        settings.spriteGenerateFallbackPhysicsShape = false;
        importer.SetTextureSettings(settings);

        if (LogVerbose)
            Debug.Log($"[IconImportPostprocessor] {path} → Sprite, mip, max {importer.maxTextureSize}");
    }
}

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Règle automatiquement l'import des images du shop (slant, slant_right, cut, slashes, hazard).
    ///
    /// Avec les réglages par défaut d'Unity, ces images sont compressées et leurs bords « bouclent »
    /// (le bord gauche déteint sur le bord droit, le haut sur le bas) : c'est ce qui dessine un liseré
    /// de couleur autour des boutons et des bandeaux. Ce script passe les images en non compressé
    /// et bloque leurs bords.
    ///
    /// Il s'exécute tout seul à chaque recompilation, et ne touche qu'au dossier qui contient slant_right.png.
    /// Peut aussi être lancé à la main : menu Tools > Rage Room > Régler les images du shop.
    /// Le fichier peut être placé n'importe où dans Assets : il est ignoré dans le jeu compilé.
    /// </summary>
    static class ShopTextureSetup
    {
        static readonly string[] Names = { "slant.png", "slant_right.png", "cut.png", "slashes.png", "hazard.png" };

        [InitializeOnLoadMethod]
        static void RunAfterReload()
        {
            EditorApplication.delayCall += Apply;
        }

        [MenuItem("Tools/Rage Room/Régler les images du shop")]
        static void Apply()
        {
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("slant_right t:Texture2D"))
            {
                string anchor = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(anchor) != "slant_right.png") continue;

                string folder = Path.GetDirectoryName(anchor).Replace('\\', '/');
                foreach (string fileName in Names)
                    if (Fix(folder + "/" + fileName)) changed++;
            }

            if (changed > 0)
                Debug.Log($"[ShopTextureSetup] Import réglé pour {changed} image(s) du shop.");
        }

        /// <summary>Renvoie true si l'image a dû être réglée (et donc réimportée).</summary>
        static bool Fix(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;

            // La bande de chantier se répète à l'horizontale : seul son bord haut/bas est bloqué.
            TextureWrapMode wrapU = path.EndsWith("hazard.png") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;

            bool ok = importer.textureType == TextureImporterType.Default
                   && importer.wrapModeU == wrapU
                   && importer.wrapModeV == TextureWrapMode.Clamp
                   && importer.textureCompression == TextureImporterCompression.Uncompressed
                   && importer.npotScale == TextureImporterNPOTScale.None
                   && importer.alphaIsTransparency
                   && importer.mipmapEnabled
                   && importer.filterMode == FilterMode.Trilinear;
            if (ok) return false;

            importer.textureType = TextureImporterType.Default;
            importer.wrapModeU = wrapU;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;   // pas de blocs de compression sur les bords
            importer.npotScale = TextureImporterNPOTScale.None;                       // garde la taille exacte, les découpes du USS en dépendent
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;                                            // rendu plus stable quand le panneau est loin
            importer.filterMode = FilterMode.Trilinear;
            importer.SaveAndReimport();
            return true;
        }
    }
}
#endif
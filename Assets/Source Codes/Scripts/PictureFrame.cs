using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Met une image dans le cadre. Pioche dans la liste et dans le dossier
    /// persistentDataPath/Photos (sur Quest : Android/data/[package]/files/Photos).
    /// </summary>
    public class PictureFrame : MonoBehaviour
    {
        [SerializeField] Renderer pictureRenderer;
        [SerializeField] int materialIndex = 0;
        [SerializeField] string textureProperty = "_BaseMap"; // URP/Lit
        [SerializeField] List<Texture> pictures = new List<Texture>();
        [SerializeField] bool loadPhotosFolder = true;

        static List<Texture> s_FolderPhotos;
        MaterialPropertyBlock mpb;

        void Start()
        {
            var pool = new List<Texture>(pictures);
            if (loadPhotosFolder) pool.AddRange(LoadPhotosFolder());
            pool.RemoveAll(t => t == null);
            if (pool.Count > 0) SetPicture(pool[Random.Range(0, pool.Count)]);
        }

        public void SetPicture(Texture tex)
        {
            if (pictureRenderer == null || tex == null) return;
            mpb ??= new MaterialPropertyBlock();
            pictureRenderer.GetPropertyBlock(mpb, materialIndex);
            mpb.SetTexture(textureProperty, tex);
            pictureRenderer.SetPropertyBlock(mpb, materialIndex);
        }

        static List<Texture> LoadPhotosFolder()
        {
            if (s_FolderPhotos != null) return s_FolderPhotos;
            s_FolderPhotos = new List<Texture>();
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Photos");
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return s_FolderPhotos; }
                foreach (var file in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;
                    var tex = new Texture2D(2, 2);
                    if (tex.LoadImage(File.ReadAllBytes(file))) s_FolderPhotos.Add(tex);
                    else Destroy(tex);
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[PictureFrame] {e.Message}"); }
            return s_FolderPhotos;
        }
    }
}

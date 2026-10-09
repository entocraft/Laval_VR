using System;
using System.Collections.Generic;
using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Liste des objets commandables depuis le panneau. Chaque entrée peut avoir plusieurs
    /// variantes (ex. deux modèles de télé) : une variante est tirée au hasard à chaque spawn.
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Catalogue d'objets", fileName = "SpawnCatalog")]
    public class SpawnCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string displayName = "Objet";
            public string Category = "Catégorie";
            public Sprite icon;
            [Tooltip("Prefabs possibles pour cet objet. Un est tiré au hasard à chaque spawn.")]
            public GameObject[] variants = Array.Empty<GameObject>();
            [Tooltip("Masse utilisée si le spawner doit ajouter lui-même un Rigidbody.")]
            [Min(0.05f)] public float mass = 1f;
            [Min(1)] public int maxPerOrder = 10;

            public GameObject PickVariant()
            {
                if (variants == null || variants.Length == 0) return null;
                int start = UnityEngine.Random.Range(0, variants.Length);
                for (int i = 0; i < variants.Length; i++)
                {
                    var prefab = variants[(start + i) % variants.Length];
                    if (prefab != null) return prefab;
                }
                return null;
            }
        }

        public List<Entry> entries = new List<Entry>();

        [ContextMenu("Remplir avec les objets de la Rage Room")]
        public void FillDefaults()
        {
            entries = new List<Entry>
            {
                
            };
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}

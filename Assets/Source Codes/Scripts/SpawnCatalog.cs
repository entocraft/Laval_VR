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
                new Entry { displayName = "Bouteille en verre", mass = 0.5f, maxPerOrder = 20 },
                new Entry { displayName = "Caisse en bois",     mass = 4f,   maxPerOrder = 10 },
                new Entry { displayName = "Brique",             mass = 2.5f, maxPerOrder = 20 },
                new Entry { displayName = "Télé",               mass = 12f,  maxPerOrder = 5 },
                new Entry { displayName = "Imprimante",         mass = 8f,   maxPerOrder = 5 },
                new Entry { displayName = "Miroir",             mass = 10f,  maxPerOrder = 3 },
                new Entry { displayName = "Vase",               mass = 1.5f, maxPerOrder = 10 },
                new Entry { displayName = "Cadre photo",        mass = 1f,   maxPerOrder = 10 },
                new Entry { displayName = "Statue",             mass = 30f,  maxPerOrder = 2 },
                new Entry { displayName = "Vaisselle",          mass = 0.4f, maxPerOrder = 20 },
                new Entry { displayName = "Tonneau",            mass = 15f,  maxPerOrder = 5 },
            };
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}

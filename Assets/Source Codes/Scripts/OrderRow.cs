using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RageRoom
{
    /// <summary>Une ligne du panneau de commande : nom de l'article, bouton -, quantité, bouton +.</summary>
    public class OrderRow : MonoBehaviour
    {
        [Tooltip("Index de l'article dans le catalogue.")]
        public int entryIndex;
        public TMP_Text nameText;
        public TMP_Text countText;
        public Button minusButton;
        public Button plusButton;

        [NonSerialized] public int count;
        [NonSerialized] public SpawnCatalog.Entry entry;
    }
}

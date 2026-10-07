using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RageRoom
{
    /// <summary>
    /// Panneau de commande : au lancement, lit le catalogue et duplique le modèle de ligne
    /// (l'enfant « ObjectButton ») une fois par article, puis gère quantités, total et validation.
    /// Le modèle lui-même est masqué ; l'UI n'est plus générée par code, elle vient de la scène.
    ///
    /// Ce que le script cherche dans chaque copie d'ObjectButton (tout est optionnel) :
    ///  - un texte TextMeshPro pour le nom, un autre pour la quantité ;
    ///  - un bouton + et un bouton - (avec un seul bouton, un clic ajoute 1) ;
    ///  - une Image dont le nom contient « icon » pour l'icône de l'article.
    /// Pour imposer les références au lieu de les laisser deviner : ajoute le composant OrderRow
    /// sur ObjectButton et remplis ses champs, ils seront repris tels quels dans chaque copie.
    /// </summary>
    public class OrderPanel : MonoBehaviour
    {
        [SerializeField] SpawnCatalog catalog;
        [SerializeField] ObjectSpawner spawner;
        [Tooltip("Plafond d'objets, tous articles confondus, dans une seule commande. "
               + "Le maximum réel baisse tout seul selon la place restante dans la salle (Max Objects In Room du spawner).")]
        [SerializeField, Min(1)] int maxTotalPerOrder = 20;

        [Header("Modèle de ligne")]
        [Tooltip("Objet dupliqué une fois par article du catalogue. Vide : cherche un enfant nommé « ObjectButton ».")]
        [SerializeField] GameObject rowTemplate;
        [Tooltip("Écart vertical (px) entre deux copies quand le parent du modèle n'a pas de Layout Group.")]
        [SerializeField] float spacingWithoutLayout = 8f;

        [Header("Références UI (toutes optionnelles)")]
        [SerializeField] TMP_Text totalText;
        [SerializeField] TMP_Text feedbackText;
        [SerializeField] Button resetButton;
        [SerializeField] Button clearButton;
        [SerializeField] Button validateButton;
        [SerializeField] float feedbackDuration = 2.5f;
        [Tooltip("Durée de l'animation d'apparition du panneau (0 = aucune).")]
        [SerializeField] float popDuration = 0.15f;

        [Header("Diagnostic")]
        [Tooltip("Affiche dans la console ce que le script trouve au lancement (catalogue, modèle, lignes créées).")]
        [SerializeField] bool debugLog = true;

        const string TemplateName = "ObjectButton";

        readonly List<OrderRow> rows = new List<OrderRow>();
        Vector3 baseScale;
        float feedbackHideTime, popT, nextLimitCheck;
        int limit; // maximum commandable en ce moment

        const float LimitCheckInterval = 0.2f;

        void Awake()
        {
            baseScale = transform.localScale;
            if (debugLog)
                Debug.Log($"[OrderPanel] Démarrage sur '{PathOf(transform)}' | catalogue : "
                        + (catalog == null ? "AUCUN" : $"'{catalog.name}' ({catalog.entries.Count} article(s))"), this);
            BuildRows();

            if (resetButton) resetButton.onClick.AddListener(ResetCounts);
            if (clearButton) clearButton.onClick.AddListener(ClearRoom);
            if (validateButton) validateButton.onClick.AddListener(Validate);
            if (feedbackText) feedbackText.text = "";
            UpdateLimit(true);
        }

        void OnEnable()
        {
            popT = 0f;
            nextLimitCheck = 0f; // recalcule le maximum dès la réouverture du panneau
        }

        void Update()
        {
            if (popT < 1f)
            {
                popT = popDuration > 0f ? Mathf.Min(1f, popT + Time.unscaledDeltaTime / popDuration) : 1f;
                float s = 1f - Mathf.Pow(1f - popT, 3f); // ease-out
                transform.localScale = baseScale * s;
            }
            if (feedbackText && feedbackText.text.Length > 0 && Time.time > feedbackHideTime)
                feedbackText.text = "";

            // La place disponible change quand des objets sont livrés, cassés ou supprimés.
            if (Time.unscaledTime >= nextLimitCheck)
            {
                nextLimitCheck = Time.unscaledTime + LimitCheckInterval;
                UpdateLimit(false);
            }
        }

        // ---------- Maximum de commande ----------

        /// <summary>Maximum commandable maintenant : le plafond par commande, réduit à la place restante dans la salle.</summary>
        int ComputeLimit()
        {
            return spawner != null ? Mathf.Min(maxTotalPerOrder, spawner.FreeSlots) : maxTotalPerOrder;
        }

        void UpdateLimit(bool force)
        {
            int newLimit = ComputeLimit();
            if (!force && newLimit == limit) return;
            limit = newLimit;

            // La salle s'est remplie entre-temps : on retire le surplus de la sélection, en partant du bas de la liste.
            int excess = Total() - limit;
            for (int i = rows.Count - 1; i >= 0 && excess > 0; i--)
            {
                int cut = Mathf.Min(rows[i].count, excess);
                if (cut == 0) continue;
                rows[i].count -= cut;
                excess -= cut;
                RefreshRow(rows[i]);
            }
            RefreshTotal();
        }

        /// <summary>
        /// Conservé pour l'outil d'éditeur RageRoomSetup, qui l'appelle encore.
        /// Ne génère plus rien : l'UI vient de la scène et les lignes sont créées au lancement du jeu.
        /// </summary>
        public void BuildUI()
        {
            if (!Application.isPlaying)
                Debug.LogWarning("[OrderPanel] BuildUI ne génère plus l'UI : les lignes sont créées au lancement "
                               + "à partir de l'enfant « " + TemplateName + " ».", this);
        }

        // ---------- Création des lignes à partir du catalogue ----------

        void BuildRows()
        {
            if (rowTemplate == null)
            {
                Transform found = FindChild(TemplateName);
                if (found != null) rowTemplate = found.gameObject;
            }
            if (rowTemplate == null)
            {
                Debug.LogError($"[OrderPanel] Aucun enfant « {TemplateName} » trouvé sous '{name}'.", this);
                return;
            }
            if (catalog == null || catalog.entries.Count == 0)
            {
                Debug.LogWarning("[OrderPanel] Catalogue vide ou non assigné : aucune ligne créée.", this);
                rowTemplate.SetActive(false);
                return;
            }

            Transform parent = rowTemplate.transform.parent;
            RectTransform templateRt = rowTemplate.transform as RectTransform;
            bool hasLayout = parent.GetComponent<LayoutGroup>() != null;
            int firstIndex = rowTemplate.transform.GetSiblingIndex() + 1;

            for (int i = 0; i < catalog.entries.Count; i++)
            {
                SpawnCatalog.Entry entry = catalog.entries[i];
                GameObject go = Instantiate(rowTemplate, parent);
                go.transform.SetSiblingIndex(firstIndex + i); // les copies prennent la place du modèle
                go.SetActive(true);

                // Sans Layout Group sur le parent, on empile les copies vers le bas à la main.
                if (!hasLayout && templateRt != null && go.transform is RectTransform rt)
                    rt.anchoredPosition = templateRt.anchoredPosition
                                          + Vector2.down * i * (templateRt.rect.height + spacingWithoutLayout);

                rows.Add(SetupRow(go, i, entry));
                go.name = TemplateName + " " + entry.displayName;
            }

            rowTemplate.SetActive(false);

            if (debugLog && rows.Count > 0)
            {
                OrderRow r = rows[0];
                Debug.Log($"[OrderPanel] {rows.Count} ligne(s) créée(s) sous '{PathOf(parent)}' à partir de '{PathOf(rowTemplate.transform)}'"
                        + (hasLayout ? "" : " (parent sans Layout Group : empilement manuel)")
                        + $"\nDétecté dans le modèle -> nom : {NameOf(r.nameText)} | quantité : {NameOf(r.countText)}"
                        + $" | bouton + : {NameOf(r.plusButton)} | bouton - : {NameOf(r.minusButton)}", rows[0]);
            }
        }

        static string NameOf(Component c) => c == null ? "(aucun)" : "'" + c.name + "'";

        static string PathOf(Transform t)
        {
            string path = t.name;
            while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
            return path;
        }

        OrderRow SetupRow(GameObject go, int index, SpawnCatalog.Entry entry)
        {
            if (!go.TryGetComponent(out OrderRow row)) row = go.AddComponent<OrderRow>();
            AutoFill(row, go);

            row.entryIndex = index;
            row.entry = entry;
            row.count = 0;
            if (row.nameText) row.nameText.text = entry.displayName;

            Image icon = FindIcon(go);
            if (icon != null && entry.icon != null) icon.sprite = entry.icon;

            if (row.minusButton) row.minusButton.onClick.AddListener(() => Change(row, -1));
            if (row.plusButton) row.plusButton.onClick.AddListener(() => Change(row, +1));
            RefreshRow(row);
            return row;
        }

        // Complète les références d'OrderRow restées vides en devinant d'après les noms des enfants,
        // puis d'après leur ordre dans la hiérarchie.
        static void AutoFill(OrderRow row, GameObject go)
        {
            var buttons = new List<Button>(go.GetComponentsInChildren<Button>(true));
            buttons.Remove(row.plusButton);
            buttons.Remove(row.minusButton);
            if (row.plusButton == null) row.plusButton = Take(buttons, "plus", "add", "ajout", "+");
            if (row.minusButton == null) row.minusButton = Take(buttons, "minus", "moins", "remove", "retir", "-");
            if (row.plusButton == null && buttons.Count > 0)
            {
                row.plusButton = buttons[buttons.Count - 1]; // un seul bouton : il sert de +
                buttons.RemoveAt(buttons.Count - 1);
            }
            if (row.minusButton == null && buttons.Count > 0) row.minusButton = buttons[0];

            var texts = new List<TMP_Text>();
            foreach (TMP_Text t in go.GetComponentsInChildren<TMP_Text>(true))
                if (t != row.nameText && t != row.countText
                    && !IsLabelOf(t, row.plusButton, go) && !IsLabelOf(t, row.minusButton, go))
                    texts.Add(t);
            if (row.countText == null) row.countText = Take(texts, "count", "quant", "qty", "nombre", "amount");
            if (row.nameText == null) row.nameText = Take(texts, "name", "nom", "label", "title", "titre");
            if (row.nameText == null && texts.Count > 0)
            {
                row.nameText = texts[0];
                texts.RemoveAt(0);
            }
            if (row.countText == null && texts.Count > 0) row.countText = texts[0];
        }

        // Libellé d'un bouton +/- (le « + » écrit dessus) : à ne pas prendre pour le nom ou la quantité.
        static bool IsLabelOf(TMP_Text text, Button button, GameObject root)
        {
            return button != null && button.gameObject != root && text.transform.IsChildOf(button.transform);
        }

        // Retire de la liste et renvoie le premier élément dont le nom correspond à un des mots-clés.
        static T Take<T>(List<T> items, params string[] keys) where T : Component
        {
            foreach (T item in items)
            {
                string n = item.name.ToLowerInvariant().Trim();
                foreach (string key in keys)
                {
                    bool match = key.Length == 1 ? n.EndsWith(key, System.StringComparison.Ordinal) : n.Contains(key);
                    if (!match) continue;
                    items.Remove(item);
                    return item;
                }
            }
            return null;
        }

        static Image FindIcon(GameObject go)
        {
            foreach (Image img in go.GetComponentsInChildren<Image>(true))
            {
                if (img.gameObject == go) continue;
                string n = img.name.ToLowerInvariant();
                if (n.Contains("icon") || n.Contains("icône")) return img;
            }
            return null;
        }

        Transform FindChild(string childName)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (t != transform && t.name == childName) return t;
            return null;
        }

        // ---------- Actions ----------

        int Total()
        {
            int total = 0;
            foreach (var r in rows) total += r.count;
            return total;
        }

        void Change(OrderRow row, int delta)
        {
            int roomLeft = limit - (Total() - row.count);
            row.count = Mathf.Clamp(row.count + delta, 0, Mathf.Max(0, Mathf.Min(row.entry.maxPerOrder, roomLeft)));
            RefreshRow(row);
            RefreshTotal();
        }

        void Validate()
        {
            if (spawner == null) { ShowFeedback("Aucun ObjectSpawner assigné !"); return; }

            var order = new List<ObjectSpawner.OrderLine>();
            int requested = 0;
            foreach (var r in rows)
                if (r.count > 0) { order.Add(new ObjectSpawner.OrderLine(r.entry, r.count)); requested += r.count; }
            if (requested == 0) return;

            int accepted = spawner.PlaceOrder(order);
            if (accepted == requested) ShowFeedback($"Commande validée : {accepted} objet(s) en route !");
            else if (accepted == 0) ShowFeedback("La salle est pleine, vide-la d'abord !");
            else ShowFeedback($"Salle presque pleine : {accepted}/{requested} objets livrés.");
            ResetCounts();
            UpdateLimit(true); // la commande vient de consommer de la place
        }

        void ResetCounts()
        {
            foreach (var r in rows) Change(r, -r.count);
        }

        void ClearRoom()
        {
            if (spawner == null) return;
            spawner.ClearRoom();
            ShowFeedback("Salle vidée.");
            UpdateLimit(true);
        }

        void RefreshRow(OrderRow row)
        {
            // Pas de texte de quantité dans le modèle : on l'affiche à la suite du nom.
            if (row.countText) row.countText.text = row.count.ToString();
            else if (row.nameText)
                row.nameText.text = row.count > 0 ? $"{row.entry.displayName}  x{row.count}" : row.entry.displayName;

            if (row.minusButton) row.minusButton.interactable = row.count > 0;
        }

        void RefreshTotal()
        {
            int total = Total();
            bool full = total >= limit;
            if (totalText)
                totalText.text = limit == 0 ? "Salle pleine : casse des objets ou vide la salle"
                               : total == 0 ? $"Aucun objet sélectionné (max {limit})"
                               : full ? $"Total : <b>{total} / {limit}</b> (maximum atteint)"
                               : $"Total : <b>{total} / {limit}</b> objets";
            if (validateButton) validateButton.interactable = total > 0;

            // Les + se grisent quand la commande est pleine ou l'article à son max
            foreach (var r in rows)
                if (r.plusButton) r.plusButton.interactable = !full && r.count < r.entry.maxPerOrder;
        }

        void ShowFeedback(string msg)
        {
            if (feedbackText == null) { Debug.Log("[OrderPanel] " + msg, this); return; }
            feedbackText.text = msg;
            feedbackHideTime = Time.time + feedbackDuration;
        }
    }
}
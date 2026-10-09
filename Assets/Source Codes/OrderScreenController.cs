using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Écran de commande en UI Toolkit, branché sur le même système que OrderPanel :
    /// il lit le SpawnCatalog et envoie la commande à l'ObjectSpawner.
    /// À placer sur le GameObject qui porte le UI Document.
    ///
    /// Éléments cherchés dans le UXML, par leur nom puis, à défaut, par leur classe :
    ///  - obligatoires : ScrollView "product-grid", ScrollView "cart-list", Button "order-button" ;
    ///  - optionnels : Label "cart-badge", "cart-empty", "cart-total" (classe cart-total-value), "order-feedback",
    ///    Button "clear-button" (vide la salle), VisualElement "category-bar" (onglets de catégories ; créé par le script s'il manque).
    ///
    /// Catégories : chaque article du catalogue est rangé d'après son champ « category » (texte ou enum).
    /// Un onglet est créé par catégorie rencontrée, dans l'ordre du catalogue : rien d'autre à faire pour en ajouter une.
    ///
    /// Les décorations, les animations et les sons sont ajoutés par ce script : rien à créer dans UI Builder.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class OrderScreenController : MonoBehaviour
    {
        [SerializeField] SpawnCatalog catalog;
        [Tooltip("Vide : prend le premier ObjectSpawner trouvé dans la scène.")]
        [SerializeField] ObjectSpawner spawner;
        [Tooltip("Plafond d'objets, tous articles confondus, dans une seule commande. "
               + "Le maximum réel baisse tout seul selon la place restante dans la salle (Max Objects In Room du spawner).")]
        [SerializeField, Min(1)] int maxTotalPerOrder = 20;
        [SerializeField] float feedbackDuration = 2.5f;

        [Header("Style")]
        [Tooltip("Affiche tous les textes de l'écran en majuscules (style arcade).")]
        [SerializeField] bool uppercase = true;
        [Tooltip("Rend le fond rectangulaire et la bordure des boutons invisibles : leur forme vient alors de l'image en biais du USS. "
               + "À décocher si tu reviens à un style sans image de fond.")]
        [SerializeField] bool imageButtons = true;
        [Tooltip("Ajoute les décorations du style arcade : bandes de chantier, barres obliques après le titre, "
               + "numéro sur chaque article. Leur apparence se règle dans le USS.")]
        [SerializeField] bool decorations = true;

        [Header("Catégories")]
        [Tooltip("Ajoute un premier onglet qui montre tous les articles, toutes catégories confondues.")]
        [SerializeField] bool showAllCategory = true;
        [SerializeField] string allCategoryLabel = "Tout";
        [Tooltip("Nom de l'onglet des articles dont le champ category est vide.")]
        [SerializeField] string uncategorizedLabel = "Autres";

        [Header("Animations")]
        [Tooltip("Apparition du panneau et des cartes, rebond des chiffres, bandeau de validation, bouton Commander qui bat.")]
        [SerializeField] bool animations = true;
        [Tooltip("Vitesse de défilement des bandes de chantier, en pixels par seconde (0 = bandes fixes).")]
        [SerializeField] float hazardSpeed = 24f;

        [Header("Sons")]
        [SerializeField] bool sounds = true;
        [SerializeField, Range(0f, 1f)] float volume = 0.5f;
        [Tooltip("Vide : utilise l'AudioSource de cet objet, ou en crée une (son 3D qui vient du panneau).")]
        [SerializeField] AudioSource audioSource;
        [Tooltip("Vide : un petit son synthétique est généré au lancement.")]
        [SerializeField] AudioClip hoverSound;
        [Tooltip("Vide : un petit son synthétique est généré au lancement.")]
        [SerializeField] AudioClip addSound;
        [Tooltip("Vide : un petit son synthétique est généré au lancement.")]
        [SerializeField] AudioClip removeSound;
        [Tooltip("Vide : un petit son synthétique est généré au lancement.")]
        [SerializeField] AudioClip orderSound;
        [Tooltip("Vide : un petit son synthétique est généré au lancement.")]
        [SerializeField] AudioClip errorSound;

        enum Sfx { Hover, Add, Remove, Order, Error, Tab }

        class CartRow
        {
            public VisualElement root;
            public Label qty;
            public Button plus;
        }

        const long TickIntervalMs = 200;
        const long BumpMs = 110;
        const long BannerHoldMs = 1100;
        const int Rate = 44100;

        int[] counts = new int[0];   // quantité choisie pour chaque article du catalogue
        int limit;                   // maximum commandable en ce moment
        int shownTotal = -1;         // dernier total affiché, pour ne faire rebondir les chiffres que s'ils changent
        float feedbackHideTime, lastHoverTime;

        // Catégories : celle de chaque article et la liste des onglets.
        string[] entryCategory = new string[0];
        readonly List<string> categoryNames = new List<string>();
        readonly List<Button> categoryChips = new List<Button>();
        VisualElement categoryBar;

        readonly List<Button> addButtons = new List<Button>();
        readonly List<VisualElement> cards = new List<VisualElement>();
        readonly Dictionary<int, CartRow> cartRows = new Dictionary<int, CartRow>();
        readonly List<VisualElement> hazardStripes = new List<VisualElement>();
        readonly Dictionary<Sfx, AudioClip> synthClips = new Dictionary<Sfx, AudioClip>();

        ScrollView productGrid, cartList;
        Label cartBadge, cartEmpty, cartTotal, orderFeedback, banner;
        Button orderButton, clearButton;
        IVisualElementScheduledItem tick, hazardScroll, pulse, bannerOut, bannerReset;

        void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[OrderScreen] Le UI Document n'a pas d'interface : vérifie Source Asset et Panel Settings.", this);
                return;
            }

            productGrid = Find<ScrollView>(root, "product-grid", "product-grid");
            cartList = Find<ScrollView>(root, "cart-list", "cart-list");
            orderButton = Find<Button>(root, "order-button", "order-button");
            clearButton = Find<Button>(root, "clear-button", "clear-button");
            cartBadge = Find<Label>(root, "cart-badge", "cart-badge");
            cartEmpty = Find<Label>(root, "cart-empty", "cart-empty");
            cartTotal = Find<Label>(root, "cart-total", "cart-total-value");
            orderFeedback = Find<Label>(root, "order-feedback", "order-feedback");

            bool ok = Check(productGrid, "product-grid", "ScrollView");
            ok &= Check(cartList, "cart-list", "ScrollView");
            ok &= Check(orderButton, "order-button", "Button");
            if (!ok) return;

            // Les textes optionnels absents ne bloquent rien, mais on le signale : c'est la cause d'un total qui ne bouge pas.
            var missing = new List<string>();
            if (cartTotal == null) missing.Add("\"cart-total\" (le total)");
            if (cartBadge == null) missing.Add("\"cart-badge\" (le compteur du panier)");
            if (cartEmpty == null) missing.Add("\"cart-empty\" (le message panier vide)");
            if (orderFeedback == null) missing.Add("\"order-feedback\" (le message après commande)");
            if (missing.Count > 0)
                Debug.LogWarning("[OrderScreen] Label(s) introuvable(s) dans le UXML, ils ne seront pas mis à jour : "
                               + string.Join(", ", missing) + ". Donne ce nom à un Label (champ Name dans UI Builder).", this);

            categoryBar = Find<VisualElement>(root, "category-bar", "category-bar");

            if (spawner == null) spawner = FindFirstObjectByType<ObjectSpawner>();
            if (spawner == null)
                Debug.LogWarning("[OrderScreen] Aucun ObjectSpawner assigné ni trouvé dans la scène : la commande ne livrera rien.", this);

            orderButton.clicked += Validate;
            if (clearButton != null) clearButton.clicked += ClearRoom;
            if (orderFeedback != null) orderFeedback.text = "";

            // Textes fixes du UXML (titre, « Votre panier », « Commander »...) : passés en majuscules si demandé.
            if (uppercase)
                root.Query<TextElement>().ForEach(t => { t.text = t.text.ToUpperInvariant(); });

            // Boutons déjà présents dans le UXML (Commander, Vider la salle...).
            root.Query<Button>().ForEach(b => { Shape(b); Hoverable(b); });

            VisualElement panel = root.Q<VisualElement>("shop-root") ?? root.Q<VisualElement>(className: "shop-root");

            hazardStripes.Clear();
            if (decorations) AddDecorations(root, panel);

            banner = null;
            if (animations && panel != null)
            {
                // Bandeau qui traverse l'écran à la validation d'une commande.
                banner = new Label { pickingMode = PickingMode.Ignore };
                banner.AddToClassList("order-banner");
                panel.Add(banner);

                // Apparition du panneau : la classe est retirée juste après, le USS anime le retour à la normale.
                panel.AddToClassList("shop-root--intro");
                panel.schedule.Execute(() => panel.RemoveFromClassList("shop-root--intro")).StartingIn(60);
            }

            shownTotal = -1;
            BuildProducts();
            BuildCategories();
            UpdateLimit(true);

            // La place disponible change quand des objets sont livrés, cassés ou supprimés.
            tick = root.schedule.Execute(Tick).Every(TickIntervalMs);

            if (animations)
            {
                if (hazardStripes.Count > 0 && !Mathf.Approximately(hazardSpeed, 0f))
                    hazardScroll = root.schedule.Execute(ScrollHazard).Every(33);
                pulse = root.schedule.Execute(Pulse).Every(900);
            }
        }

        void OnDisable()
        {
            if (orderButton != null) orderButton.clicked -= Validate;
            if (clearButton != null) clearButton.clicked -= ClearRoom;
            tick?.Pause();
            hazardScroll?.Pause();
            pulse?.Pause();
            bannerOut?.Pause();
            bannerReset?.Pause();
            tick = hazardScroll = pulse = bannerOut = bannerReset = null;
        }

        void OnDestroy()
        {
            foreach (AudioClip clip in synthClips.Values)
                if (clip != null) Destroy(clip);
            synthClips.Clear();
        }

        // ---------- Recherche des éléments ----------

        /// <summary>Cherche un élément par son nom, puis par sa classe si le nom n'a pas été renseigné dans UI Builder.</summary>
        static T Find<T>(VisualElement root, string elementName, string className) where T : VisualElement
        {
            T element = root.Q<T>(elementName);
            if (element == null) element = root.Q<T>(className: className);
            return element;
        }

        bool Check(VisualElement element, string elementName, string type)
        {
            if (element != null) return true;
            Debug.LogError($"[OrderScreen] Élément introuvable dans le UXML : il faut un {type} nommé \"{elementName}\". "
                         + "Vérifie le type de l'élément et son champ Name dans UI Builder.", this);
            return false;
        }

        string Txt(string s) => uppercase && s != null ? s.ToUpperInvariant() : s;

        /// <summary>
        /// Retire le fond rectangulaire et la bordure d'un bouton. Le thème par défaut d'Unity les recolore au survol,
        /// au clic et au focus, ce qui ferait réapparaître un rectangle ou un liseré autour de la forme en biais ;
        /// un style posé par code passe avant.
        /// </summary>
        Button Shape(Button button)
        {
            if (!imageButtons || button == null) return button;
            button.style.backgroundColor = Color.clear;
            button.style.borderTopWidth = 0f;
            button.style.borderRightWidth = 0f;
            button.style.borderBottomWidth = 0f;
            button.style.borderLeftWidth = 0f;
            return button;
        }

        void Tick()
        {
            if (orderFeedback != null && orderFeedback.text.Length > 0 && Time.unscaledTime > feedbackHideTime)
                orderFeedback.text = "";
            UpdateLimit(false);
        }

        // ---------- Décorations ----------

        /// <summary>
        /// Ajoute les éléments purement décoratifs, pour ne pas avoir à les créer dans UI Builder.
        /// Ils sont repérés par leurs classes (shop-header, shop-title) et le panneau shop-root ; s'ils manquent, rien n'est ajouté.
        /// </summary>
        void AddDecorations(VisualElement root, VisualElement panel)
        {
            VisualElement header = root.Q<VisualElement>(className: "shop-header");
            if (header != null && header.parent != null && root.Q<VisualElement>(className: "title-slashes") == null)
            {
                // Barres obliques juste après le titre.
                Label title = header.Q<Label>(className: "shop-title");
                int after = title != null ? header.IndexOf(title) + 1 : 0;
                header.Insert(Mathf.Clamp(after, 0, header.childCount), Decoration("title-slashes"));

                // Bande de chantier sous l'en-tête.
                VisualElement parent = header.parent;
                parent.Insert(parent.IndexOf(header) + 1, HazardBand(false));
            }

            // Bande de chantier tout en bas du panneau.
            if (panel != null && panel.Q<VisualElement>(className: "hazard-band--bottom") == null)
                panel.Add(HazardBand(true));
        }

        static VisualElement Decoration(string className)
        {
            var element = new VisualElement { pickingMode = PickingMode.Ignore };
            element.AddToClassList(className);
            return element;
        }

        /// <summary>Une bande = un cadre qui masque ce qui dépasse + les rayures, plus larges, que l'on fait glisser.</summary>
        VisualElement HazardBand(bool bottom)
        {
            VisualElement band = Decoration("hazard-band");
            if (bottom) band.AddToClassList("hazard-band--bottom");
            VisualElement stripes = Decoration("hazard-band__stripes");
            band.Add(stripes);
            hazardStripes.Add(stripes);
            return band;
        }

        void ScrollHazard()
        {
            foreach (VisualElement stripes in hazardStripes)
            {
                float height = stripes.resolvedStyle.height;
                if (float.IsNaN(height) || height <= 0f) continue;
                // Le motif est deux fois plus large que haut : on boucle sur une période pour un défilement sans à-coup.
                float x = Mathf.Repeat(Time.unscaledTime * hazardSpeed, height * 2f);
                stripes.style.translate = new Translate(new Length(x, LengthUnit.Pixel), new Length(0f, LengthUnit.Pixel));
            }
        }

        // ---------- Animations ----------

        /// <summary>Petit rebond : la classe est posée puis retirée, le USS anime l'aller et le retour.</summary>
        void Bump(VisualElement element, string className = "bump")
        {
            if (!animations || element == null) return;
            element.AddToClassList(className);
            element.schedule.Execute(() => element.RemoveFromClassList(className)).StartingIn(BumpMs);
        }

        /// <summary>Fait battre le bouton Commander, comme un pouls, tant qu'il y a quelque chose dans le panier.</summary>
        void Pulse()
        {
            if (orderButton != null && Total() > 0) Bump(orderButton, "order-button--pulse");
        }

        void ShowBanner(string text, bool error)
        {
            if (!animations || banner == null) return;
            bannerOut?.Pause();
            bannerReset?.Pause();

            banner.text = Txt(text);
            banner.EnableInClassList("order-banner--error", error);
            banner.RemoveFromClassList("order-banner--out");
            banner.AddToClassList("order-banner--in");

            // Sortie par la droite, puis retour discret (invisible) à la position de départ.
            bannerOut = banner.schedule.Execute(() =>
            {
                banner.RemoveFromClassList("order-banner--in");
                banner.AddToClassList("order-banner--out");
            }).StartingIn(BannerHoldMs);
            bannerReset = banner.schedule.Execute(() => banner.RemoveFromClassList("order-banner--out")).StartingIn(BannerHoldMs + 450);
        }

        // ---------- Sons ----------

        void Hoverable(VisualElement element)
        {
            if (element != null) element.RegisterCallback<PointerEnterEvent>(OnHover);
        }

        void OnHover(PointerEnterEvent evt)
        {
            if (evt.currentTarget is VisualElement element && !element.enabledInHierarchy) return;
            if (Time.unscaledTime - lastHoverTime < 0.06f) return;
            lastHoverTime = Time.unscaledTime;
            Play(Sfx.Hover, 0.5f);
        }

        void Play(Sfx sfx, float gain = 1f)
        {
            if (!sounds || volume <= 0f || !isActiveAndEnabled) return;

            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                audioSource.spatialBlend = 1f;      // son 3D : il vient du panneau
                audioSource.rolloffMode = AudioRolloffMode.Linear;
                audioSource.minDistance = 1f;
                audioSource.maxDistance = 12f;
            }

            AudioClip clip = ClipFor(sfx);
            if (clip != null) audioSource.PlayOneShot(clip, volume * gain);
        }

        AudioClip ClipFor(Sfx sfx)
        {
            AudioClip custom = null;
            switch (sfx)
            {
                case Sfx.Hover: custom = hoverSound; break;
                case Sfx.Add: custom = addSound; break;
                case Sfx.Remove: custom = removeSound; break;
                case Sfx.Order: custom = orderSound; break;
                case Sfx.Error: custom = errorSound; break;
            }
            if (custom != null) return custom;

            if (!synthClips.TryGetValue(sfx, out AudioClip clip) || clip == null)
            {
                clip = Synthesize(sfx);
                synthClips[sfx] = clip;
            }
            return clip;
        }

        /// <summary>Sons d'interface générés par code (petits bips d'arcade), utilisés tant qu'aucun clip n'est assigné.</summary>
        static AudioClip Synthesize(Sfx sfx)
        {
            var samples = new List<float>(Rate);
            switch (sfx)
            {
                case Sfx.Hover:     // tic bref et aigu
                    Tone(samples, 1500f, 1900f, 0.035f, 0.25f, 0f);
                    break;
                case Sfx.Add:       // deux notes qui montent
                    Tone(samples, 620f, 620f, 0.05f, 0.45f, 0.6f);
                    Tone(samples, 930f, 930f, 0.10f, 0.45f, 0.6f);
                    break;
                case Sfx.Remove:    // deux notes qui descendent
                    Tone(samples, 620f, 620f, 0.05f, 0.45f, 0.6f);
                    Tone(samples, 415f, 415f, 0.10f, 0.45f, 0.6f);
                    break;
                case Sfx.Order:     // arpège montant, dernière note tenue
                    Tone(samples, 523f, 523f, 0.07f, 0.45f, 0.5f);
                    Tone(samples, 659f, 659f, 0.07f, 0.45f, 0.5f);
                    Tone(samples, 784f, 784f, 0.07f, 0.45f, 0.5f);
                    Tone(samples, 1047f, 1047f, 0.40f, 0.5f, 0.5f);
                    break;
                case Sfx.Tab:       // clac bref
                    Tone(samples, 880f, 700f, 0.05f, 0.4f, 0.4f);
                    break;
                case Sfx.Error:     // deux buzz graves
                    Tone(samples, 150f, 135f, 0.12f, 0.5f, 1f);
                    Tone(samples, 0f, 0f, 0.04f, 0f, 0f);
                    Tone(samples, 150f, 120f, 0.22f, 0.5f, 1f);
                    break;
            }

            AudioClip clip = AudioClip.Create("UI_" + sfx, samples.Count, 1, Rate, false);
            clip.SetData(samples.ToArray(), 0);
            return clip;
        }

        /// <summary>
        /// Ajoute une note : fréquence qui glisse de f0 à f1, attaque rapide puis extinction.
        /// square = 0 pour un son doux (sinus), 1 pour un son plus dur, proche d'une onde carrée.
        /// </summary>
        static void Tone(List<float> samples, float f0, float f1, float duration, float gain, float square)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(duration * Rate));
            float phase = 0f;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                phase += 2f * Mathf.PI * Mathf.Lerp(f0, f1, t) / Rate;
                float s = Mathf.Sin(phase);
                s = Mathf.Lerp(s, Mathf.Clamp(s * 3f, -1f, 1f), square);
                float attack = Mathf.Clamp01(i / (0.003f * Rate));
                float decay = (1f - t) * (1f - t);
                samples.Add(s * attack * decay * gain);
            }
        }

        // ---------- Catégories ----------

        static System.Reflection.MemberInfo categoryMember;
        static bool categoryMemberSearched;

        /// <summary>
        /// Catégorie d'un article, lue dans son champ « category ». Le champ est lu par son nom plutôt qu'en dur,
        /// pour accepter aussi bien un texte qu'un enum, et pour que ce script compile même si le champ est renommé.
        /// Pour un enum, le nom affiché est celui donné par [InspectorName("...")] s'il y en a un.
        /// </summary>
        string CategoryOf(SpawnCatalog.Entry entry)
        {
            if (!categoryMemberSearched)
            {
                categoryMemberSearched = true;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.IgnoreCase;
                System.Type type = typeof(SpawnCatalog.Entry);
                foreach (string candidate in new[] { "category", "categorie" })
                {
                    categoryMember = (System.Reflection.MemberInfo)type.GetField(candidate, flags) ?? type.GetProperty(candidate, flags);
                    if (categoryMember != null) break;
                }
                if (categoryMember == null)
                    Debug.LogWarning("[OrderScreen] Aucun champ « category » trouvé dans SpawnCatalog.Entry : les onglets de catégories ne sont pas affichés.", this);
            }

            object value = null;
            if (categoryMember is System.Reflection.FieldInfo field) value = field.GetValue(entry);
            else if (categoryMember is System.Reflection.PropertyInfo property) value = property.GetValue(entry, null);
            if (value == null) return uncategorizedLabel;

            string text = value.ToString();
            if (value is System.Enum)
            {
                // Nom d'affichage de la valeur d'enum, si [InspectorName] en fournit un.
                System.Reflection.FieldInfo enumField = value.GetType().GetField(text);
                object[] names = enumField != null ? enumField.GetCustomAttributes(typeof(InspectorNameAttribute), false) : null;
                if (names != null && names.Length > 0) text = ((InspectorNameAttribute)names[0]).displayName;
            }

            text = text != null ? text.Trim() : "";
            return text.Length > 0 ? text : uncategorizedLabel;
        }

        /// <summary>Crée un onglet par catégorie rencontrée dans le catalogue. Avec une seule catégorie, la barre reste masquée.</summary>
        void BuildCategories()
        {
            categoryNames.Clear();
            categoryChips.Clear();

            int count = catalog != null ? catalog.entries.Count : 0;
            entryCategory = new string[count];
            for (int i = 0; i < count; i++)
            {
                // « armes » et « Armes » vont dans le même onglet : la première écriture rencontrée sert de nom.
                string category = CategoryOf(catalog.entries[i]);
                string known = categoryNames.Find(n => string.Equals(n, category, System.StringComparison.OrdinalIgnoreCase));
                if (known == null) categoryNames.Add(category);
                entryCategory[i] = known ?? category;
            }

            bool useful = categoryMember != null && categoryNames.Count > 1;
            if (!useful)
            {
                if (categoryBar != null) categoryBar.style.display = DisplayStyle.None;
                return;
            }

            // Barre absente du UXML : on la crée juste au-dessus du bloc qui contient la grille.
            if (categoryBar == null)
            {
                VisualElement body = productGrid.parent;
                if (body == null || body.parent == null) return;
                categoryBar = new VisualElement { name = "category-bar" };
                categoryBar.AddToClassList("category-bar");
                body.parent.Insert(body.parent.IndexOf(body), categoryBar);
            }

            categoryBar.Clear();
            categoryBar.style.display = DisplayStyle.Flex;
            if (showAllCategory) AddCategoryChip(allCategoryLabel, null);
            foreach (string category in categoryNames) AddCategoryChip(category, category);

            SelectCategory(showAllCategory ? null : categoryNames[0], false);
        }

        void AddCategoryChip(string label, string category)
        {
            var chip = Shape(new Button(() => SelectCategory(category, true)) { text = Txt(label) });
            chip.AddToClassList("category-chip");
            chip.userData = category;   // null pour l'onglet « Tout »
            Hoverable(chip);
            categoryBar.Add(chip);
            categoryChips.Add(chip);
        }

        /// <summary>Ouvre un onglet : seuls les articles de cette catégorie restent visibles (null = tous). Le panier n'est pas touché.</summary>
        void SelectCategory(string category, bool byUser)
        {
            foreach (Button chip in categoryChips)
                chip.EnableInClassList("category-chip--active", (string)chip.userData == category);

            int shown = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                bool visible = category == null || (i < entryCategory.Length && entryCategory[i] == category);
                VisualElement card = cards[i];
                card.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                // Les articles de l'onglet arrivent l'un après l'autre.
                if (byUser && animations)
                {
                    card.AddToClassList("product-card--enter");
                    card.schedule.Execute(() => card.RemoveFromClassList("product-card--enter")).StartingIn(40 + shown * 50);
                }
                shown++;
            }

            if (byUser)
            {
                productGrid.scrollOffset = Vector2.zero;   // retour en haut de la liste
                Play(Sfx.Tab);
            }
        }

        // ---------- Catalogue ----------

        void BuildProducts()
        {
            productGrid.Clear();
            cartList.Clear();
            addButtons.Clear();
            cards.Clear();
            cartRows.Clear();

            if (catalog == null || catalog.entries.Count == 0)
            {
                counts = new int[0];
                Debug.LogWarning("[OrderScreen] Catalogue vide ou non assigné : glisse ton SpawnCatalog "
                               + "(Assets/Source Codes/Data) dans le champ Catalog du composant.", this);
                return;
            }

            counts = new int[catalog.entries.Count];
            for (int i = 0; i < catalog.entries.Count; i++)
            {
                VisualElement card = CreateProductCard(i, catalog.entries[i]);
                productGrid.Add(card);
                cards.Add(card);

                // Les cartes arrivent l'une après l'autre.
                if (animations)
                {
                    card.AddToClassList("product-card--enter");
                    card.schedule.Execute(() => card.RemoveFromClassList("product-card--enter")).StartingIn(220 + i * 70);
                }
            }

            Debug.Log($"[OrderScreen] {catalog.entries.Count} article(s) du catalogue « {catalog.name} » affiché(s).", this);
        }

        VisualElement CreateProductCard(int index, SpawnCatalog.Entry entry)
        {
            var card = new VisualElement();
            card.AddToClassList("product-card");
            Hoverable(card);

            // Numéro de l'article (01, 02...), comme dans une liste d'épreuves.
            if (decorations)
            {
                var number = new Label((index + 1).ToString("00")) { pickingMode = PickingMode.Ignore };
                number.AddToClassList("product-index");
                card.Add(number);
            }

            // Sans icône, la carte s'affiche quand même, juste sans le carré d'image.
            if (entry.icon != null)
            {
                var icon = new VisualElement();
                icon.AddToClassList("product-icon");
                icon.style.backgroundImage = new StyleBackground(entry.icon);
                card.Add(icon);
            }

            var nameLabel = new Label(Txt(entry.displayName));
            nameLabel.AddToClassList("product-name");
            card.Add(nameLabel);

            var addButton = Shape(new Button(() => Change(index, +1)) { text = Txt("Ajouter") });
            addButton.AddToClassList("glass-button");
            addButton.AddToClassList("add-button");
            card.Add(addButton);
            addButtons.Add(addButton);

            return card;
        }

        // ---------- Panier ----------

        int Total()
        {
            int total = 0;
            foreach (int c in counts) total += c;
            return total;
        }

        void Change(int index, int delta)
        {
            int before = counts[index];
            int roomLeft = limit - (Total() - before);
            int max = Mathf.Max(0, Mathf.Min(catalog.entries[index].maxPerOrder, roomLeft));
            counts[index] = Mathf.Clamp(before + delta, 0, max);

            if (counts[index] == before)
            {
                if (delta > 0) Play(Sfx.Error, 0.6f);   // commande pleine ou article au maximum
                return;
            }

            Play(delta > 0 ? Sfx.Add : Sfx.Remove);
            if (delta > 0 && index < cards.Count) Bump(cards[index], "product-card--punch");
            Refresh();
        }

        void ResetCounts()
        {
            for (int i = 0; i < counts.Length; i++) counts[i] = 0;
            Refresh();
        }

        /// <summary>
        /// Met l'affichage à jour : lignes du panier, compteur, total, boutons.
        /// Les lignes existantes sont conservées et modifiées sur place, pour que seuls les changements s'animent.
        /// </summary>
        void Refresh()
        {
            int total = Total();
            bool full = total >= limit;

            int position = 0;   // place de la ligne dans le panier, dans l'ordre du catalogue
            for (int i = 0; i < counts.Length; i++)
            {
                bool exists = cartRows.TryGetValue(i, out CartRow row);
                if (counts[i] <= 0)
                {
                    if (exists)
                    {
                        row.root.RemoveFromHierarchy();
                        cartRows.Remove(i);
                    }
                    continue;
                }

                if (!exists)
                {
                    row = CreateCartLine(i);
                    cartRows[i] = row;
                    cartList.Insert(Mathf.Min(position, cartList.contentContainer.childCount), row.root);

                    // La nouvelle ligne glisse depuis la droite.
                    if (animations)
                    {
                        VisualElement line = row.root;
                        line.AddToClassList("cart-line--enter");
                        line.schedule.Execute(() => line.RemoveFromClassList("cart-line--enter")).StartingIn(30);
                    }
                }

                string quantity = counts[i].ToString();
                if (row.qty.text != quantity)
                {
                    row.qty.text = quantity;
                    if (exists) Bump(row.qty);
                }
                row.plus.SetEnabled(!full && counts[i] < catalog.entries[i].maxPerOrder);
                position++;
            }

            if (cartBadge != null) cartBadge.text = Txt($"Panier : {total}");
            if (cartTotal != null) cartTotal.text = limit == 0 ? Txt("Salle pleine") : $"{total} / {limit}";
            if (cartEmpty != null)
            {
                cartEmpty.text = Txt(limit == 0 ? "Salle pleine : casse des objets ou vide la salle" : "Votre panier est vide");
                cartEmpty.style.display = total == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            orderButton.SetEnabled(total > 0);

            // Les boutons Ajouter se grisent quand la commande est pleine ou l'article à son maximum.
            for (int i = 0; i < addButtons.Count; i++)
                addButtons[i].SetEnabled(!full && counts[i] < catalog.entries[i].maxPerOrder);

            // Les chiffres rebondissent quand le total change (pas au premier affichage).
            if (total != shownTotal)
            {
                if (shownTotal >= 0)
                {
                    Bump(cartTotal);
                    Bump(cartBadge);
                }
                shownTotal = total;
            }
        }

        CartRow CreateCartLine(int index)
        {
            SpawnCatalog.Entry entry = catalog.entries[index];
            var row = new CartRow();

            row.root = new VisualElement();
            row.root.AddToClassList("cart-line");

            var nameLabel = new Label(Txt(entry.displayName));
            nameLabel.AddToClassList("cart-line-name");
            row.root.Add(nameLabel);

            var controls = new VisualElement();
            controls.AddToClassList("cart-line-controls");

            var qty = new VisualElement();
            qty.AddToClassList("cart-line-qty");

            var minus = Shape(new Button(() => Change(index, -1)) { text = "-" });
            minus.AddToClassList("qty-button");
            Hoverable(minus);
            qty.Add(minus);

            row.qty = new Label("");
            row.qty.AddToClassList("qty-value");
            qty.Add(row.qty);

            row.plus = Shape(new Button(() => Change(index, +1)) { text = "+" });
            row.plus.AddToClassList("qty-button");
            Hoverable(row.plus);
            qty.Add(row.plus);

            controls.Add(qty);
            row.root.Add(controls);
            return row;
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

            // La salle s'est remplie entre-temps : on retire le surplus du panier, en partant du bas de la liste.
            int surplus = Total() - limit;
            for (int i = counts.Length - 1; i >= 0 && surplus > 0; i--)
            {
                int removed = Mathf.Min(counts[i], surplus);
                counts[i] -= removed;
                surplus -= removed;
            }

            Refresh();
        }

        // ---------- Actions ----------

        void Validate()
        {
            if (spawner == null) { ShowFeedback("Aucun ObjectSpawner assigné !"); Play(Sfx.Error); return; }

            var order = new List<ObjectSpawner.OrderLine>();
            int requested = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;
                order.Add(new ObjectSpawner.OrderLine(catalog.entries[i], counts[i]));
                requested += counts[i];
            }
            if (requested == 0) return;

            int accepted = spawner.PlaceOrder(order);
            if (accepted == requested)
            {
                ShowFeedback($"Commande validée : {accepted} objet(s) en route !");
                ShowBanner("Commande validée !", false);
                Play(Sfx.Order);
            }
            else if (accepted == 0)
            {
                ShowFeedback("La salle est pleine, vide-la d'abord !");
                ShowBanner("Salle pleine !", true);
                Play(Sfx.Error);
            }
            else
            {
                ShowFeedback($"Salle presque pleine : {accepted}/{requested} objets livrés.");
                ShowBanner($"{accepted} / {requested} livrés", false);
                Play(Sfx.Order);
            }

            ResetCounts();
            UpdateLimit(true);
        }

        void ClearRoom()
        {
            if (spawner == null) return;
            spawner.ClearRoom();
            ShowFeedback("Salle vidée.");
            ShowBanner("Salle vidée", false);
            Play(Sfx.Remove);
            UpdateLimit(true);
        }

        void ShowFeedback(string msg)
        {
            if (orderFeedback == null) { Debug.Log("[OrderScreen] " + msg, this); return; }
            orderFeedback.text = Txt(msg);
            feedbackHideTime = Time.unscaledTime + feedbackDuration;
        }
    }
}
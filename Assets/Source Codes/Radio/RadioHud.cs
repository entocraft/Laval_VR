using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Annonce « À l'écoute » : un petit panneau UI Toolkit (RadioHud.uxml + RadioHud.uss) qui glisse dans le HUD
    /// au début de chaque morceau (pochette, titre, artiste, album), reste quelques secondes puis repart.
    /// Créé automatiquement par le RadioPlayer : rien à placer dans la scène.
    ///
    /// Placement : quand le HUD de score existe, l'annonce s'accroche juste en dessous et bouge avec lui.
    /// Sinon (menu principal, score désactivé), elle suit le regard toute seule. Réglages dans l'asset RadioConfig.
    ///
    /// Les formes en biais sont dessinées par ce script, sans image : tout élément du UXML qui porte la classe
    /// « slant » devient un parallélogramme, de la couleur donnée dans le USS par --slant-color.
    /// </summary>
    [DefaultExecutionOrder(1200)] // après le HUD de score (1100), pour se placer par rapport à sa position du jour
    public class RadioHud : MonoBehaviour
    {
        /// <summary>Taille du panneau en pixels. Avec le Panel Settings du HUD, 1 pixel = 1 mm à l'échelle 1.</summary>
        static readonly Vector2 Size = new Vector2(520f, 150f);
        const float PixelsPerMeter = 1000f;

        const string VisibleClass = "np--visible";
        /// <summary>Durée de la glissade de sortie (doit couvrir la transition du USS).</summary>
        const float LeaveTime = 0.45f;

        static readonly CustomStyleProperty<Color> SlantColor = new CustomStyleProperty<Color>("--slant-color");
        static readonly CustomStyleProperty<float> SlantSize = new CustomStyleProperty<float>("--slant-size");

        sealed class Slant
        {
            public Color color = Color.clear;
            public float size = 12f;
        }

        enum State { Hidden, Entering, Visible, Leaving }

        RadioConfig config;
        UIDocument document;

        VisualElement boundRoot, root, cover;
        Label stationLabel, titleLabel, artistLabel, albumLabel;
        bool missingReported;

        // Annonce
        State state = State.Hidden;
        float stateUntil;
        RadioStation pendingStation;
        RadioTrack pendingTrack;

        // Placement
        UIDocument scoreHud;
        float nextScoreSearch;
        Transform head;
        Vector3 anchor = Vector3.forward, smoothPosition;
        bool placed, following;

        // ------------------------------------------------------------------ Mise en place

        /// <summary>Appelé par le RadioPlayer juste après la création de l'objet.</summary>
        public void Init(RadioConfig radioConfig)
        {
            config = radioConfig;

            // Même montage que le HUD de score : un UI Document de taille fixe, affiché dans le monde.
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = config.hudPanelSettings;
            document.visualTreeAsset = config.hudUxml;
            document.worldSpaceSizeMode = UIDocument.WorldSpaceSizeMode.Fixed;
            document.worldSpaceSize = Size;
            document.pivotReferenceSize = PivotReferenceSize.Layout;
            transform.localScale = Vector3.one * config.hudScale;
        }

        void OnEnable() { RadioPlayer.TrackStarted += OnTrackStarted; }

        void OnDisable() { RadioPlayer.TrackStarted -= OnTrackStarted; }

        void OnTrackStarted(RadioStation station, RadioTrack track)
        {
            // Affichée dès que possible par LateUpdate ; un morceau qui démarre pendant une annonce la remplace.
            pendingStation = station;
            pendingTrack = track;
        }

        /// <summary>
        /// Retrouve les éléments du UXML. Le UI Document reconstruit son contenu à chaque activation :
        /// on vérifie donc à chaque image que les éléments connus sont toujours les bons.
        /// </summary>
        bool EnsureBound()
        {
            VisualElement current = document != null ? document.rootVisualElement : null;
            if (current == null) return false;
            if (current == boundRoot) return root != null;

            boundRoot = current;
            state = State.Hidden;

            root = current.Q<VisualElement>("radio-hud");
            cover = current.Q<VisualElement>("radio-hud-cover");
            stationLabel = current.Q<Label>("radio-hud-station");
            titleLabel = current.Q<Label>("radio-hud-title");
            artistLabel = current.Q<Label>("radio-hud-artist");
            albumLabel = current.Q<Label>("radio-hud-album");

            if (root == null || titleLabel == null)
            {
                if (!missingReported)
                {
                    missingReported = true;
                    Debug.LogError("[Radio] RadioHud.uxml ne contient pas les éléments attendus : il faut au moins un VisualElement "
                                 + "nommé « radio-hud » et un Label « radio-hud-title ».", this);
                }
                root = null;
                return false;
            }

            // L'annonce ne doit jamais intercepter les rayons des manettes.
            current.pickingMode = PickingMode.Ignore;
            root.pickingMode = PickingMode.Ignore;

            if (config.hudFont != null) root.style.unityFontDefinition = new StyleFontDefinition(config.hudFont);
            else root.AddToClassList("np--default-font");

            current.Query<VisualElement>(className: "slant").ForEach(AttachSlant);

            // Rien à dessiner tant qu'aucun morceau n'est annoncé
            root.RemoveFromClassList(VisibleClass);
            root.style.display = DisplayStyle.None;
            return true;
        }

        // ------------------------------------------------------------------ Formes en biais

        /// <summary>Fait dessiner à un élément un parallélogramme à sa taille, de la couleur --slant-color de son style.</summary>
        static void AttachSlant(VisualElement element)
        {
            if (element.userData is Slant) return;

            var slant = new Slant();
            element.userData = slant;
            ReadSlant(element.customStyle, slant);
            element.RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                ReadSlant(e.customStyle, slant);
                element.MarkDirtyRepaint();
            });
            element.generateVisualContent += context => DrawSlant(context, slant);
        }

        static void ReadSlant(ICustomStyle style, Slant slant)
        {
            if (style == null) return;
            Color color;
            float size;
            if (style.TryGetValue(SlantColor, out color)) slant.color = color;
            if (style.TryGetValue(SlantSize, out size)) slant.size = size;
        }

        static void DrawSlant(MeshGenerationContext context, Slant slant)
        {
            VisualElement element = context.visualElement;
            float w = element.layout.width, h = element.layout.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f || slant.color.a <= 0f) return;

            // « slant » : /____/    « slant slant--right » : |____/    « slant slant--left » : /____|
            float k = Mathf.Min(slant.size, w * 0.5f);
            float topLeft = element.ClassListContains("slant--right") ? 0f : k;
            float bottomRight = element.ClassListContains("slant--left") ? w : w - k;

            Painter2D painter = context.painter2D;
            painter.fillColor = slant.color;
            painter.BeginPath();
            painter.MoveTo(new Vector2(topLeft, 0f));
            painter.LineTo(new Vector2(w, 0f));
            painter.LineTo(new Vector2(bottomRight, h));
            painter.LineTo(new Vector2(0f, h));
            painter.ClosePath();
            painter.Fill();
        }

        // ------------------------------------------------------------------ Boucle

        void LateUpdate()
        {
            if (config == null || !EnsureBound()) return;

            float now = Time.unscaledTime;
            switch (state)
            {
                case State.Hidden:
                    if (pendingTrack != null && Place(true))
                    {
                        Fill(pendingStation, pendingTrack);
                        pendingStation = null;
                        pendingTrack = null;

                        // L'élément s'affiche d'abord hors champ ; la classe posée juste après lance la glissade du USS.
                        root.RemoveFromClassList(VisibleClass);
                        root.style.display = DisplayStyle.Flex;
                        state = State.Entering;
                        stateUntil = now + 0.05f;
                    }
                    break;

                case State.Entering:
                    Place(false);
                    if (now >= stateUntil)
                    {
                        root.AddToClassList(VisibleClass);
                        state = State.Visible;
                        stateUntil = now + config.hudDuration;
                    }
                    break;

                case State.Visible:
                    Place(false);
                    if (now >= stateUntil || pendingTrack != null)
                    {
                        root.RemoveFromClassList(VisibleClass);
                        state = State.Leaving;
                        stateUntil = now + LeaveTime;
                    }
                    break;

                case State.Leaving:
                    Place(false);
                    if (now >= stateUntil)
                    {
                        root.style.display = DisplayStyle.None;
                        state = State.Hidden;
                    }
                    break;
            }
        }

        void Fill(RadioStation station, RadioTrack track)
        {
            if (stationLabel != null) stationLabel.text = Text(station != null ? station.stationName : "");
            titleLabel.text = Text(track.title);
            if (artistLabel != null) SetOptional(artistLabel, track.artist);
            if (albumLabel != null) SetOptional(albumLabel, track.album);

            if (cover != null)
            {
                Texture2D texture = station != null ? station.CoverOf(track) : track.cover;
                cover.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
                cover.EnableInClassList("np__cover--empty", texture == null);
            }
        }

        /// <summary>Un champ laissé vide dans la radio (pas d'album…) ne prend pas de place dans l'annonce.</summary>
        void SetOptional(Label label, string value)
        {
            bool has = !string.IsNullOrEmpty(value);
            label.text = has ? Text(value) : "";
            label.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
        }

        string Text(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return config.uppercase ? value.ToUpperInvariant() : value;
        }

        // ------------------------------------------------------------------ Placement

        /// <summary>
        /// Place le panneau. <paramref name="snap"/> : premier placement d'une annonce, sans glissement depuis l'ancienne position.
        /// Renvoie false tant qu'il n'y a ni HUD de score ni caméra (pendant un chargement de scène).
        /// </summary>
        bool Place(bool snap)
        {
            UIDocument score = config.hudUnderScore ? FindScoreHud() : null;
            if (score != null)
            {
                // Sous le HUD de score, aligné sur son bord gauche, à la même échelle : les deux bougent ensemble.
                Transform hud = score.transform;
                Vector2 hudSize = score.worldSpaceSize;
                var local = new Vector3((Size.x - hudSize.x) * 0.5f / PixelsPerMeter,
                                        -(hudSize.y * 0.5f + config.hudGap + Size.y * 0.5f) / PixelsPerMeter,
                                        0f);
                transform.SetPositionAndRotation(hud.TransformPoint(local), hud.rotation);
                transform.localScale = hud.lossyScale;
                placed = false; // si le HUD de score disparaît, le suivi autonome repart de zéro
                return true;
            }
            return Follow(snap);
        }

        /// <summary>Cherche le HUD de score par le nom de son script, pour que la radio marche aussi sans le score.</summary>
        UIDocument FindScoreHud()
        {
            if (scoreHud != null) return scoreHud.isActiveAndEnabled ? scoreHud : null;
            if (Time.unscaledTime < nextScoreSearch) return null;

            nextScoreSearch = Time.unscaledTime + 2f;
            foreach (UIDocument candidate in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (candidate != document && candidate.GetComponent("ScoreHud") != null)
                {
                    scoreHud = candidate;
                    return scoreHud.isActiveAndEnabled ? scoreHud : null;
                }
            return null;
        }

        /// <summary>
        /// Suivi autonome, sur le même principe que le HUD de score : le panneau reste immobile tant que le regard
        /// ne s'écarte pas trop (on peut le lire), puis se replace en douceur devant le joueur.
        /// </summary>
        bool Follow(bool snap)
        {
            if (head == null)
            {
                Camera cam = Camera.main;
                if (cam == null) return false;
                head = cam.transform;
                placed = false;
            }
            if (snap) placed = false;

            float dt = Time.unscaledDeltaTime;
            Vector3 gaze = head.forward;
            if (!placed)
            {
                anchor = gaze;
                following = false;
            }

            if (Vector3.Angle(anchor, gaze) > config.hudDeadZone) following = true;
            if (following)
            {
                anchor = Vector3.Slerp(anchor, gaze, 1f - Mathf.Exp(-config.hudFollowSpeed * dt)).normalized;
                if (Vector3.Angle(anchor, gaze) < 3f) following = false;
            }

            // Direction de l'annonce : celle du regard mémorisé, décalée vers le haut et la gauche, toujours d'aplomb
            float yaw = Mathf.Atan2(anchor.x, anchor.z) * Mathf.Rad2Deg - config.hudAngleLeft;
            float pitch = Mathf.Asin(Mathf.Clamp(anchor.y, -1f, 1f)) * Mathf.Rad2Deg + config.hudAngleUp;
            pitch = Mathf.Clamp(pitch, -80f, 80f);
            Vector3 direction = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;

            // La position suit la tête de près : en marchant, l'annonce ne reste pas en arrière.
            Vector3 target = head.position + direction * config.hudDistance;
            if (!placed || (smoothPosition - target).sqrMagnitude > 4f) smoothPosition = target;
            else smoothPosition = Vector3.Lerp(smoothPosition, target, 1f - Mathf.Exp(-12f * dt));
            placed = true;

            // Le texte se lit en regardant dans le sens de l'axe Z du panneau.
            Vector3 away = smoothPosition - head.position;
            if (away.sqrMagnitude > 1e-6f)
                transform.SetPositionAndRotation(smoothPosition, Quaternion.LookRotation(away, Vector3.up));
            transform.localScale = Vector3.one * config.hudScale;
            return true;
        }
    }
}

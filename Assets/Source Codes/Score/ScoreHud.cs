using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// HUD du score : un panneau UI Toolkit (ScoreHud.uxml + ScoreHud.uss) qui flotte en haut à gauche du regard
    /// et suit le joueur. Créé automatiquement par le ScoreManager : rien à placer dans la scène.
    ///
    /// Suivi : le panneau reste immobile tant que le regard ne s'écarte pas trop, et tant qu'on le regarde lui
    /// (on peut donc le lire), puis il se replace en douceur. Position, taille et réactivité se règlent
    /// dans la section HUD du barème (asset ScoreTable).
    ///
    /// Impacts : chaque ligne de points arrive en grand et vient s'écraser sur le panneau, qui recule sous le choc,
    /// avec un éclair blanc et un son. Le tintement monte dans l'aigu à mesure que le combo grandit.
    /// Les sons sont fabriqués par ScoreSynth, ou remplacés par ceux du barème.
    ///
    /// Les formes en biais sont dessinées par ce script, sans image : tout élément du UXML qui porte la classe
    /// « slant » devient un parallélogramme, de la couleur donnée dans le USS par --slant-color.
    /// </summary>
    [DefaultExecutionOrder(1100)]
    public class ScoreHud : MonoBehaviour
    {
        /// <summary>Taille du panneau en pixels. Avec le Panel Settings du HUD, 1 pixel = 1 mm à l'échelle 1.</summary>
        static readonly Vector2 Size = new Vector2(600f, 340f);
        const float PixelsPerMeter = 1000f;

        // Impact : rebond de la ligne après la chute, et ressort qui ramène le panneau après son recul.
        const float SettleTime = 0.32f;
        const float SlamStagger = 0.07f;
        const float PunchStiffness = 500f;
        const float PunchDamping = 22f;
        const float PunchKick = 44f;   // vitesse à donner au ressort pour 1 m de recul au maximum

        static readonly CustomStyleProperty<Color> SlantColor = new CustomStyleProperty<Color>("--slant-color");
        static readonly CustomStyleProperty<float> SlantSize = new CustomStyleProperty<float>("--slant-size");

        enum LineKind { Score, Bonus, ComboEnd }

        sealed class Slant
        {
            public Color color = Color.clear;
            public float size = 16f;
        }

        sealed class Line
        {
            public VisualElement element;
            public Label pointsLabel, textLabel;
            public float startAt, dieAt;
        }

        /// <summary>Un élément en train de s'écraser sur le HUD.</summary>
        sealed class Slam
        {
            public VisualElement element;
            public float startAt;
            public float fromScale, fromAngle;
            public float punch;
            public Action onLand;
            public bool landed;
        }

        ScoreManager manager;
        ScoreTable table;
        ScoreEngine engine;
        UIDocument document;

        VisualElement boundRoot, root, comboBox, multiplierBox, progressFill, timerFill, feed;
        Label scoreLabel, multiplierLabel, comboLabel, nextLabel;
        bool missingReported;
        readonly List<Line> lines = new List<Line>();
        readonly List<Slam> slams = new List<Slam>();
        float lastSlamStart = -100f;

        // Objets cassés à la même image par la même cause (une explosion, par exemple) : ils partagent une ligne.
        Line batchLine;
        int batchFrame = -1, batchCount, batchPoints;
        ScoreCause batchCause;

        // Valeurs actuellement affichées
        float shownScore;
        int shownScoreInt = int.MinValue;
        int shownCombo = int.MinValue;
        float shownMultiplier = -1f;
        float shownNext = -1f;
        float shownProgress;

        // Suivi du regard
        Transform head;
        Vector3 anchor = Vector3.forward;
        Vector3 smoothPosition;
        bool placed, following;
        float punchOffset, punchVelocity;

        // Sons
        AudioSource[] voices;
        int nextVoice;
        AudioClip hitClip, comboClip, bonusClip, multiplierClip, comboEndClip;
        readonly List<AudioClip> madeClips = new List<AudioClip>();

        // ------------------------------------------------------------------ Mise en place

        /// <summary>Appelé par le ScoreManager juste après la création du HUD.</summary>
        public void Init(ScoreManager scoreManager)
        {
            manager = scoreManager;
            table = scoreManager.Table;
            engine = scoreManager.Engine;

            engine.Scored += OnScored;
            engine.Bonus += OnBonus;
            engine.ComboEnded += OnComboEnded;
            manager.ScoreReset += OnScoreReset;

            // Même montage que les menus : un UI Document de taille fixe, affiché dans le monde.
            // Le mode de taille « Fixed » est celui par défaut : il suffit de donner la taille.
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = table.hudPanelSettings;
            document.visualTreeAsset = table.hudUxml;
            document.worldSpaceSize = Size;
            transform.localScale = Vector3.one * table.hudScale;

            BuildAudio();
        }

        void OnDestroy()
        {
            if (engine != null)
            {
                engine.Scored -= OnScored;
                engine.Bonus -= OnBonus;
                engine.ComboEnded -= OnComboEnded;
            }
            if (manager != null) manager.ScoreReset -= OnScoreReset;

            foreach (AudioClip clip in madeClips)
                if (clip != null) Destroy(clip);
        }

        /// <summary>
        /// Retrouve les éléments du UXML. Le UI Document reconstruit son contenu à chaque activation :
        /// on vérifie donc à chaque image que les éléments connus sont toujours les bons.
        /// </summary>
        bool EnsureBound()
        {
            VisualElement current = document != null ? document.rootVisualElement : null;
            if (current == null) return false;
            if (current == boundRoot) return scoreLabel != null;

            boundRoot = current;
            lines.Clear();
            slams.Clear();
            batchLine = null;
            shownScoreInt = int.MinValue;
            shownCombo = int.MinValue;
            shownMultiplier = -1f;
            shownNext = -1f;
            shownProgress = 0f;

            root = current.Q<VisualElement>("score-root");
            scoreLabel = current.Q<Label>("score-value");
            comboBox = current.Q<VisualElement>("combo-box");
            multiplierBox = current.Q<VisualElement>("combo-multiplier");
            multiplierLabel = current.Q<Label>("combo-multiplier-text");
            comboLabel = current.Q<Label>("combo-count");
            progressFill = current.Q<VisualElement>("combo-progress-fill");
            nextLabel = current.Q<Label>("combo-next");
            timerFill = current.Q<VisualElement>("combo-timer-fill");
            feed = current.Q<VisualElement>("score-feed");

            if (root == null || scoreLabel == null || feed == null)
            {
                if (!missingReported)
                {
                    missingReported = true;
                    Debug.LogError("[Score] ScoreHud.uxml ne contient pas les éléments attendus : il faut au moins un VisualElement "
                                 + "nommé « score-root », un Label « score-value » et un VisualElement « score-feed ».", this);
                }
                scoreLabel = null;
                return false;
            }

            // Le HUD ne doit jamais intercepter les rayons des manettes.
            current.pickingMode = PickingMode.Ignore;
            root.pickingMode = PickingMode.Ignore;

            if (table.hudFont != null) root.style.unityFontDefinition = new StyleFontDefinition(table.hudFont);
            else root.AddToClassList("score-root--default-font");

            current.Query<VisualElement>(className: "slant").ForEach(AttachSlant);
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
            if (table == null || engine == null) return;

            float dt = Time.unscaledDeltaTime;
            Follow(dt);
            if (!EnsureBound()) return;

            UpdateScore(dt);
            UpdateCombo(dt);

            float now = Time.unscaledTime;
            UpdateSlams(now);
            UpdateLines(now);
        }

        /// <summary>Le nombre affiché rattrape le vrai score en défilant.</summary>
        void UpdateScore(float dt)
        {
            float target = engine.Score;
            float speed = Mathf.Max(80f, Mathf.Abs(target - shownScore) * 6f);
            shownScore = Mathf.MoveTowards(shownScore, target, speed * dt);
            int scoreInt = Mathf.RoundToInt(shownScore);
            if (scoreInt == shownScoreInt) return;
            shownScoreInt = scoreInt;
            scoreLabel.text = FormatNumber(scoreInt);
        }

        /// <summary>Bloc du combo : multiplicateur « X3 », barre d'avancement vers le suivant, chrono.</summary>
        void UpdateCombo(float dt)
        {
            int combo = engine.ComboCount;
            bool active = combo >= 2;

            if (combo != shownCombo)
            {
                bool wasActive = shownCombo >= 2;
                shownCombo = combo;
                if (comboBox != null)
                {
                    comboBox.EnableInClassList("combo--active", active);
                    // Début du combo : tout le bloc vient s'écraser à sa place.
                    if (active && !wasActive) StartSlam(comboBox, 0f, table.hudSlamScale * 0.8f, 5f, table.hudPunch * 0.5f, null);
                }
                if (comboLabel != null && active) comboLabel.text = "COMBO " + combo;
            }

            float multiplier = engine.Multiplier;
            if (!Mathf.Approximately(multiplier, shownMultiplier))
            {
                bool higher = shownMultiplier > 0f && multiplier > shownMultiplier;
                shownMultiplier = multiplier;
                if (multiplierLabel != null) multiplierLabel.text = "X" + FormatMultiplier(multiplier);

                // Multiplicateur qui monte : le « X » s'écrase à son tour, de plus en plus aigu.
                if (higher && multiplierBox != null)
                {
                    VisualElement box = multiplierBox;
                    float pitch = Mathf.Clamp(1f + (multiplier - 2f) * 0.07f, 0.9f, 1.6f);
                    StartSlam(box, 0.05f, table.hudSlamScale, -10f, table.hudPunch, () =>
                    {
                        Flash(box);
                        Play(multiplierClip, 1f, pitch);
                    });
                }
            }

            // Barre d'avancement : part de zéro à chaque nouveau multiplicateur, pleine au dernier palier.
            float nextMultiplier;
            float progress = table.MultiplierProgress(combo, out nextMultiplier);
            if (!Mathf.Approximately(nextMultiplier, shownNext))
            {
                shownNext = nextMultiplier;
                if (nextLabel != null) nextLabel.text = nextMultiplier > 0f ? "X" + FormatMultiplier(nextMultiplier) : "MAX";
            }
            if (active)
            {
                float previous = shownProgress;
                shownProgress = progress < shownProgress ? progress : Mathf.MoveTowards(shownProgress, progress, 4f * dt);
                if (progressFill != null && !Mathf.Approximately(previous, shownProgress))
                    progressFill.style.width = Length.Percent(shownProgress * 100f);

                if (timerFill != null)
                {
                    float left = Mathf.Clamp01(engine.ComboTimeLeft(Time.time) / Mathf.Max(0.01f, table.comboWindow));
                    timerFill.style.width = Length.Percent(left * 100f);
                }
            }
            else if (shownProgress > 0f)
            {
                shownProgress = 0f;
                if (progressFill != null) progressFill.style.width = Length.Percent(0f);
            }
        }

        // ------------------------------------------------------------------ Suivi du joueur

        void Follow(float dt)
        {
            if (head == null)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                head = cam.transform;
            }

            Vector3 gaze = head.forward;
            if (!placed) anchor = gaze;

            // Deux zones où le HUD ne bouge pas : autour de la direction mémorisée (zone morte),
            // et autour du HUD lui-même, pour qu'on puisse le regarder sans le faire fuir.
            float halfDiagonal = 0.5f * Size.magnitude * table.hudScale / PixelsPerMeter;
            float lookZone = Mathf.Atan(halfDiagonal / Mathf.Max(0.1f, table.hudDistance)) * Mathf.Rad2Deg + 5f;
            float offset = Mathf.Sqrt(table.hudAngleUp * table.hudAngleUp + table.hudAngleLeft * table.hudAngleLeft);
            float deadZone = Mathf.Max(table.hudDeadZone, offset - lookZone + 2f);

            Vector3 direction = HudDirection(anchor);
            bool readingHud = Vector3.Angle(gaze, direction) < lookZone;
            if (!readingHud && Vector3.Angle(anchor, gaze) > deadZone) following = true;
            if (following)
            {
                anchor = Vector3.Slerp(anchor, gaze, 1f - Mathf.Exp(-table.hudFollowSpeed * dt)).normalized;
                direction = HudDirection(anchor);
                if (Vector3.Angle(anchor, gaze) < 3f) following = false;
            }

            // La position suit la tête de près : en marchant, le HUD ne reste pas en arrière.
            Vector3 target = head.position + direction * table.hudDistance;
            if (!placed || (smoothPosition - target).sqrMagnitude > 4f) smoothPosition = target; // premier placement, téléportation
            else smoothPosition = Vector3.Lerp(smoothPosition, target, 1f - Mathf.Exp(-12f * dt));
            placed = true;

            // Recul sous les impacts : un ressort amorti ramène le panneau à sa place.
            // Calculé par petits pas : le recul est le même à 72 comme à 120 images par seconde.
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 240f), 1, 16);
            float step = Mathf.Min(dt, 0.1f) / steps;
            for (int i = 0; i < steps; i++)
            {
                punchVelocity += (-PunchStiffness * punchOffset - PunchDamping * punchVelocity) * step;
                punchOffset += punchVelocity * step;
            }

            // Le texte se lit en regardant dans le sens de l'axe Z du panneau.
            Vector3 away = smoothPosition - head.position;
            bool valid = away.sqrMagnitude > 1e-6f;
            Quaternion rotation = valid ? Quaternion.LookRotation(away, Vector3.up) : transform.rotation;
            Vector3 position = valid ? smoothPosition + away.normalized * punchOffset : smoothPosition;

            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = Vector3.one * table.hudScale;
        }

        /// <summary>Direction du HUD pour une direction de regard : décalée vers le haut et la gauche, toujours d'aplomb.</summary>
        Vector3 HudDirection(Vector3 from)
        {
            float yaw = Mathf.Atan2(from.x, from.z) * Mathf.Rad2Deg - table.hudAngleLeft;
            float pitch = Mathf.Asin(Mathf.Clamp(from.y, -1f, 1f)) * Mathf.Rad2Deg + table.hudAngleUp;
            pitch = Mathf.Clamp(pitch, -70f, 70f);
            return Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
        }

        // ------------------------------------------------------------------ Impacts

        /// <summary>
        /// Fait arriver un élément en grand, incliné, puis l'écrase à sa place : à l'impact le panneau recule
        /// de « punch » mètres et onLand est appelé (son, éclair).
        /// </summary>
        void StartSlam(VisualElement element, float delay, float fromScale, float fromAngle, float punch, Action onLand)
        {
            if (element == null) return;
            for (int i = slams.Count - 1; i >= 0; i--)
                if (slams[i].element == element) slams.RemoveAt(i);

            slams.Add(new Slam
            {
                element = element,
                startAt = Time.unscaledTime + delay,
                fromScale = Mathf.Max(1f, fromScale),
                fromAngle = fromAngle,
                punch = punch,
                onLand = onLand
            });
        }

        void UpdateSlams(float now)
        {
            float fall = Mathf.Max(0.01f, table.hudSlamTime);

            for (int i = slams.Count - 1; i >= 0; i--)
            {
                Slam slam = slams[i];
                VisualElement element = slam.element;
                if (element.panel == null) // retiré du HUD entre-temps
                {
                    slams.RemoveAt(i);
                    continue;
                }

                float t = now - slam.startAt;
                if (t < 0f) continue;

                if (t < fall)
                {
                    // Chute : de plus en plus vite, jusqu'à la taille normale.
                    float k = t / fall;
                    float ease = k * k * k;
                    float scale = Mathf.LerpUnclamped(slam.fromScale, 1f, ease);
                    element.style.scale = new Scale(new Vector3(scale, scale, 1f));
                    element.style.rotate = new Rotate(Mathf.LerpUnclamped(slam.fromAngle, 0f, ease));
                    element.style.opacity = Mathf.Clamp01(k * 2.5f);
                    continue;
                }

                if (!slam.landed)
                {
                    slam.landed = true;
                    punchVelocity += slam.punch * PunchKick;
                    if (slam.onLand != null) slam.onLand();
                }

                float u = t - fall;
                if (u < SettleTime)
                {
                    // Impact : l'élément s'écrase un peu, puis rebondit jusqu'à sa taille.
                    float scale = 1f - 0.16f * Mathf.Exp(-16f * u) * Mathf.Cos(34f * u);
                    element.style.scale = new Scale(new Vector3(scale, scale, 1f));
                    element.style.rotate = new Rotate(0f);
                    element.style.opacity = 1f;
                }
                else
                {
                    // Terminé : on rend la main au USS.
                    element.style.scale = StyleKeyword.Null;
                    element.style.rotate = StyleKeyword.Null;
                    element.style.opacity = StyleKeyword.Null;
                    slams.RemoveAt(i);
                }
            }
        }

        /// <summary>Éclair blanc très bref sur un élément en biais (classe « flash » du USS).</summary>
        static void Flash(VisualElement element)
        {
            if (element == null) return;
            element.AddToClassList("flash");
            element.schedule.Execute(() => element.RemoveFromClassList("flash")).StartingIn(80);
        }

        /// <summary>Petit rebond d'un élément (classe « bump » du USS), le temps de la transition.</summary>
        static void Bump(VisualElement element)
        {
            if (element == null) return;
            element.AddToClassList("bump");
            element.schedule.Execute(() => element.RemoveFromClassList("bump")).StartingIn(130);
        }

        // ------------------------------------------------------------------ Événements du score

        void OnScored(ScoreEvent e)
        {
            if (!EnsureBound()) return;

            int frame = Time.frameCount;
            if (batchLine != null && batchFrame == frame && batchCause == e.cause && lines.Contains(batchLine))
            {
                batchCount++;
                batchPoints += e.points;
                batchLine.pointsLabel.text = "+" + FormatNumber(batchPoints);
                batchLine.textLabel.text = batchCount + " OBJETS · " + Upper(e.causeLabel);
            }
            else
            {
                batchLine = AddLine("+" + FormatNumber(e.points), Upper(e.label) + " · " + Upper(e.causeLabel), LineKind.Score, e.comboCount);
                batchFrame = frame;
                batchCause = e.cause;
                batchCount = 1;
                batchPoints = e.points;
            }
            Bump(scoreLabel);
        }

        void OnBonus(BonusEvent e)
        {
            if (!EnsureBound()) return;
            string text = Upper(e.label);
            if (e.count >= 2) text += " X" + e.count;
            AddLine(e.points > 0 ? "+" + FormatNumber(e.points) : "", text, LineKind.Bonus, 0);
            Bump(scoreLabel);
        }

        void OnComboEnded(int count, int points)
        {
            if (!EnsureBound()) return;
            AddLine("", "COMBO " + count + " · " + FormatNumber(points) + " PTS", LineKind.ComboEnd, 0);
        }

        void OnScoreReset()
        {
            shownScore = 0f;
            shownScoreInt = int.MinValue;
            foreach (Line line in lines) line.element.RemoveFromHierarchy();
            lines.Clear();
            batchLine = null;
        }

        // ------------------------------------------------------------------ Fil des points

        Line AddLine(string points, string text, LineKind kind, int comboCount)
        {
            var element = new VisualElement();
            element.pickingMode = PickingMode.Ignore;
            element.AddToClassList("feed-line");
            element.AddToClassList("slant");
            if (kind == LineKind.Bonus) element.AddToClassList("feed-line--bonus");
            else if (kind == LineKind.ComboEnd) element.AddToClassList("feed-line--combo");

            // Plusieurs lignes à la même image : elles s'écrasent l'une après l'autre, pas toutes ensemble.
            float startAt = Mathf.Max(Time.unscaledTime, lastSlamStart + SlamStagger);
            lastSlamStart = startAt;

            var line = new Line { element = element, startAt = startAt, dieAt = startAt + table.hudLineLife };

            if (!string.IsNullOrEmpty(points))
            {
                line.pointsLabel = new Label(points);
                line.pointsLabel.pickingMode = PickingMode.Ignore;
                line.pointsLabel.AddToClassList("feed-points");
                element.Add(line.pointsLabel);
            }
            line.textLabel = new Label(text);
            line.textLabel.pickingMode = PickingMode.Ignore;
            line.textLabel.AddToClassList("feed-text");
            element.Add(line.textLabel);

            AttachSlant(element);

            // Invisible jusqu'au début de sa chute.
            element.style.opacity = 0f;
            feed.Insert(0, element);
            lines.Insert(0, line);

            float delay = startAt - Time.unscaledTime;
            switch (kind)
            {
                case LineKind.Bonus:
                    StartSlam(element, delay, table.hudSlamScale * 1.2f, -9f, table.hudPunch, () =>
                    {
                        Flash(element);
                        Play(bonusClip, 1f, 1f);
                    });
                    break;

                case LineKind.ComboEnd:
                    StartSlam(element, delay, table.hudSlamScale * 0.85f, 4f, table.hudPunch * 0.5f, () =>
                    {
                        Flash(element);
                        Play(comboEndClip, 0.9f, 1f);
                    });
                    break;

                default:
                    // Le tintement monte d'un demi-ton par objet du combo, sur un peu plus d'une octave.
                    float comboPitch = Mathf.Pow(2f, Mathf.Clamp(comboCount - 1, 0, 14) / 12f);
                    StartSlam(element, delay, table.hudSlamScale * 0.7f, -4f, table.hudPunch * 0.5f, () =>
                    {
                        Flash(element);
                        Play(hitClip, 0.5f, UnityEngine.Random.Range(0.94f, 1.06f));
                        Play(comboClip, 0.7f, comboPitch);
                    });
                    break;
            }

            // Trop de lignes : les plus anciennes partent tout de suite.
            int max = Mathf.Max(1, table.hudLines);
            while (lines.Count > max)
            {
                lines[lines.Count - 1].element.RemoveFromHierarchy();
                lines.RemoveAt(lines.Count - 1);
            }
            return line;
        }

        /// <summary>Les lignes s'estompent à la fin de leur durée, puis disparaissent.</summary>
        void UpdateLines(float now)
        {
            const float fade = 0.4f;
            float settled = table.hudSlamTime + SettleTime;

            for (int i = lines.Count - 1; i >= 0; i--)
            {
                Line line = lines[i];
                if (now >= line.dieAt)
                {
                    line.element.RemoveFromHierarchy();
                    lines.RemoveAt(i);
                }
                else if (now >= line.dieAt - fade && now > line.startAt + settled)
                {
                    line.element.style.opacity = Mathf.Clamp01((line.dieAt - now) / fade);
                }
            }
        }

        // ------------------------------------------------------------------ Sons

        void BuildAudio()
        {
            hitClip = table.soundHit != null ? table.soundHit : MakeClip("Score - impact", ScoreSynth.Thud());
            comboClip = table.soundCombo != null ? table.soundCombo : MakeClip("Score - combo", ScoreSynth.Tick());
            bonusClip = table.soundBonus != null ? table.soundBonus : MakeClip("Score - bonus", ScoreSynth.Slam());
            multiplierClip = table.soundMultiplier != null ? table.soundMultiplier : MakeClip("Score - multiplicateur", ScoreSynth.Rise());
            comboEndClip = table.soundComboEnd != null ? table.soundComboEnd : MakeClip("Score - fin de combo", ScoreSynth.Bank());

            // Plusieurs sources : chaque son a sa propre hauteur, et deux impacts rapprochés ne se coupent pas.
            voices = new AudioSource[6];
            for (int i = 0; i < voices.Length; i++)
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f; // son d'interface : même volume où que soit le panneau
                voices[i] = source;
            }
        }

        AudioClip MakeClip(string clipName, float[] samples)
        {
            AudioClip clip = AudioClip.Create(clipName, samples.Length, 1, ScoreSynth.SampleRate, false);
            clip.SetData(samples, 0);
            madeClips.Add(clip);
            return clip;
        }

        void Play(AudioClip clip, float volume, float pitch)
        {
            if (clip == null || voices == null || !table.hudSounds || table.hudVolume <= 0f) return;

            AudioSource source = voices[nextVoice];
            nextVoice = (nextVoice + 1) % voices.Length;
            source.clip = clip;
            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume * table.hudVolume);
            source.Play();
        }

        // ------------------------------------------------------------------ Texte

        static string Upper(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : text.ToUpper(CultureInfo.InvariantCulture);
        }

        /// <summary>2 donne « 2 », 1,5 donne « 1,5 ».</summary>
        static string FormatMultiplier(float value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>12450 donne « 12 450 » (espace simple : présent dans toutes les polices).</summary>
        static string FormatNumber(int value)
        {
            string digits = Mathf.Abs(value).ToString(CultureInfo.InvariantCulture);
            var builder = new StringBuilder(digits.Length + 4);
            if (value < 0) builder.Append('-');
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0 && (digits.Length - i) % 3 == 0) builder.Append(' ');
                builder.Append(digits[i]);
            }
            return builder.ToString();
        }
    }
}

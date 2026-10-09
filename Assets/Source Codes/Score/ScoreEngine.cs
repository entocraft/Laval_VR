using System;
using System.Collections.Generic;
using UnityEngine;

namespace RageRoom
{
    /// <summary>Un objet qui vient de casser (ou d'être écrasé), tel que signalé par son composant ScoreValue.</summary>
    public struct BreakReport
    {
        /// <summary>Numéro unique de l'objet.</summary>
        public int objectId;
        public string label;
        public int basePoints;
        /// <summary>Point d'impact (monde).</summary>
        public Vector3 point;
        /// <summary>Vrai pour un écrasement, faux pour une casse.</summary>
        public bool crushed;
        /// <summary>L'objet était tenu en main au moment de casser.</summary>
        public bool wasHeld;
        /// <summary>Moment où le joueur a lâché l'objet, ou une valeur négative s'il ne l'a jamais tenu.</summary>
        public float releasedAt;
        public Vector3 releasePos;
        public float time;
        public int frame;
    }

    /// <summary>Ce qui se trouve autour d'une casse au moment où elle est analysée (fourni par le ScoreManager).</summary>
    public struct LiveContext
    {
        /// <summary>L'objet se trouve dans le souffle d'un klaxon qui sonne.</summary>
        public bool inHornBlast;
        /// <summary>Arme ou objet tenu en main à portée du point d'impact (0 = aucun).</summary>
        public int heldToolId;
        /// <summary>Objet lancé, encore en mouvement, à portée du point d'impact (0 = aucun).</summary>
        public int thrownId;
        public Vector3 thrownFrom;
    }

    /// <summary>Une casse comptée dans le score.</summary>
    public struct ScoreEvent
    {
        public string label;
        public ScoreCause cause;
        public string causeLabel;
        public int basePoints;
        /// <summary>Points réellement gagnés, multiplicateurs compris.</summary>
        public int points;
        public float multiplier;
        public int comboCount;
        public Vector3 point;
        public bool crushed;
    }

    /// <summary>Un bonus accordé (combo spécial, distance...).</summary>
    public struct BonusEvent
    {
        public string label;
        public int points;
        /// <summary>Valeur du compteur qui a déclenché le bonus (0 si sans objet).</summary>
        public int count;
    }

    /// <summary>
    /// Calcul du score : décide ce qui a cassé chaque objet, tient le combo en cours et accorde les bonus du barème.
    /// Ne dépend pas de la scène : le ScoreManager lui signale ce qui se passe (casses, explosions, tirs)
    /// et l'appelle une fois par image avec Tick().
    ///
    /// Une casse n'est analysée qu'à l'image suivante : cela laisse le temps à l'explosion ou à la bille
    /// responsable d'être signalée, même si elle l'est juste après la casse.
    /// </summary>
    public sealed class ScoreEngine
    {
        // Tolérances de rapprochement entre une casse et sa cause.
        const float PelletMatchDistance = 0.35f;   // bille <-> point d'impact
        const float HeldMatchDistance = 0.5f;      // objet fracassé en main <-> objet voisin cassé par le même geste
        const float ThrownMatchDistance = 0.6f;    // objet lancé qui a cassé <-> objet voisin qu'il a touché
        const int FrameSlack = 2;                  // écart d'images toléré entre deux événements liés
        const float MinChainDelay = 0.02f;         // deux explosions simultanées ne forment pas une chaîne

        readonly ScoreTable table;

        /// <summary>Interroge la scène autour d'une casse (armes tenues, klaxon, objets lancés).</summary>
        public Func<BreakReport, LiveContext> probe;

        public event Action<ScoreEvent> Scored;
        public event Action<BonusEvent> Bonus;
        /// <summary>Fin d'un combo : nombre d'objets cassés, points gagnés pendant le combo.</summary>
        public event Action<int, int> ComboEnded;

        public int Score { get; private set; }
        public int ComboCount { get; private set; }
        public float Multiplier { get; private set; } = 1f;
        /// <summary>Points gagnés depuis le début du combo en cours.</summary>
        public int ComboPoints { get; private set; }
        public int ObjectsBroken { get; private set; }
        public int BestCombo { get; private set; }

        float comboExpire;
        int causeMask;
        int varietyAwarded;

        // ------------------------------------------------------------------ Mémoire à court terme

        sealed class Group
        {
            public List<BonusTier> tiers;
            public int count;
            public int awardedUpTo;
            public float lastTime;
            public bool dirty;
        }

        sealed class Blast
        {
            public Vector3 center;
            public float radius;
            public float time;
            public int frame;
            public Blast root;
            public int explosives = 1;
            public Group breaks;
        }

        struct PelletHit
        {
            public int gunId;
            public Vector3 muzzle;
            public Vector3 point;
            public float time;
            public int frame;
        }

        sealed class Gun
        {
            public Group line;
            public Vector3 lineOrigin;
            public Vector3 lineDir;
            public float lineLast = -100f;
            public int streak;
            public readonly Queue<float> shots = new Queue<float>();
        }

        struct Recent
        {
            public int objectId;
            public Vector3 point;
            public float time;
            public int frame;
            public bool wasHeld;
            public bool thrown;
            public Vector3 releasePos;
        }

        readonly List<BreakReport> pending = new List<BreakReport>();
        int published;
        readonly List<Recent> recents = new List<Recent>();
        readonly List<Blast> blasts = new List<Blast>();
        readonly List<PelletHit> pelletHits = new List<PelletHit>();
        readonly Dictionary<int, Gun> guns = new Dictionary<int, Gun>();
        readonly Dictionary<int, Group> sweeps = new Dictionary<int, Group>();
        readonly Dictionary<int, Group> strikes = new Dictionary<int, Group>();
        readonly List<Group> dirty = new List<Group>();
        readonly List<int> stale = new List<int>();

        public ScoreEngine(ScoreTable table)
        {
            this.table = table;
        }

        /// <summary>Temps restant (secondes) avant la fin du combo en cours.</summary>
        public float ComboTimeLeft(float now)
        {
            return ComboCount > 0 ? Mathf.Max(0f, comboExpire - now) : 0f;
        }

        /// <summary>Remet le score et le combo à zéro.</summary>
        public void Reset()
        {
            Score = 0;
            ComboCount = 0;
            Multiplier = 1f;
            ComboPoints = 0;
            ObjectsBroken = 0;
            BestCombo = 0;
            causeMask = 0;
            varietyAwarded = 0;
            pending.Clear();
            published = 0;
            recents.Clear();
            blasts.Clear();
            pelletHits.Clear();
            guns.Clear();
            sweeps.Clear();
            strikes.Clear();
            dirty.Clear();
        }

        // ------------------------------------------------------------------ Ce que la scène signale

        /// <summary>Un objet vient de casser ou d'être écrasé.</summary>
        public void ReportBreak(BreakReport report)
        {
            pending.Add(report);
        }

        /// <summary>Une explosion vient d'avoir lieu.</summary>
        public void RegisterExplosion(Vector3 center, float radius, float time, int frame)
        {
            // Explosif déclenché par une explosion précédente : il était dans son rayon et saute juste après.
            Blast parent = null;
            for (int i = blasts.Count - 1; i >= 0; i--)
            {
                Blast b = blasts[i];
                float delay = time - b.time;
                if (delay < MinChainDelay || delay > table.chainWindow) continue;
                if (Vector3.Distance(center, b.center) > b.radius * 1.1f) continue;
                parent = b;
                break;
            }

            var blast = new Blast { center = center, radius = radius, time = time, frame = frame };
            if (parent != null)
            {
                blast.root = parent.root;
                blast.root.explosives++;
                AwardExact(table.chainBonus, blast.root.explosives);
            }
            else
            {
                blast.root = blast;
                blast.breaks = new Group { tiers = table.explosionBonus };
            }
            blasts.Add(blast);
        }

        /// <summary>Le pistolet vient de tirer une bille.</summary>
        public void RegisterShot(int gunId, float time)
        {
            GetGun(gunId).shots.Enqueue(time);
        }

        /// <summary>Une bille vient de toucher quelque chose.</summary>
        public void RegisterPelletHit(int gunId, Vector3 muzzle, Vector3 point, float time, int frame)
        {
            pelletHits.Add(new PelletHit { gunId = gunId, muzzle = muzzle, point = point, time = time, frame = frame });
        }

        /// <summary>Ajoute des points en dehors du barème (défi, objectif...), avec une ligne dans le HUD.</summary>
        public void AddBonus(string label, int points)
        {
            GiveBonus(label, points, 0);
        }

        // ------------------------------------------------------------------ Boucle

        /// <summary>À appeler une fois par image, après tout le reste.</summary>
        public void Tick(float now, int frame)
        {
            // 1. Les casses toutes fraîches sont visibles de leurs voisines avant même d'être analysées.
            for (; published < pending.Count; published++)
            {
                BreakReport r = pending[published];
                recents.Add(new Recent
                {
                    objectId = r.objectId,
                    point = r.point,
                    time = r.time,
                    frame = r.frame,
                    wasHeld = r.wasHeld,
                    thrown = IsThrown(r),
                    releasePos = r.releasePos
                });
            }

            // 2. Analyse des casses des images précédentes.
            int done = 0;
            while (done < pending.Count && pending[done].frame < frame)
            {
                Resolve(pending[done], now);
                done++;
            }
            if (done > 0)
            {
                pending.RemoveRange(0, done);
                published -= done;
            }

            // 3. Bonus à compteur : un seul message par groupe et par image, même si plusieurs paliers tombent d'un coup.
            for (int i = 0; i < dirty.Count; i++) Flush(dirty[i]);
            dirty.Clear();

            // 4. Tirs qui n'ont rien cassé : la série sans faute repart de zéro.
            foreach (KeyValuePair<int, Gun> pair in guns)
            {
                Gun g = pair.Value;
                while (g.shots.Count > 0 && now - g.shots.Peek() > table.shotTimeout)
                {
                    g.shots.Dequeue();
                    g.streak = 0;
                }
            }

            // 5. Fin du combo.
            if (ComboCount > 0 && now >= comboExpire && pending.Count == 0) EndCombo();

            Prune(now);
        }

        void EndCombo()
        {
            int count = ComboCount, points = ComboPoints;
            ComboCount = 0;
            ComboPoints = 0;
            Multiplier = 1f;
            causeMask = 0;
            varietyAwarded = 0;
            if (count >= 2) ComboEnded?.Invoke(count, points);
        }

        void Prune(float now)
        {
            float blastLife = Mathf.Max(table.blastAftermath, table.chainWindow) + 1f;
            blasts.RemoveAll(b => now - b.time > blastLife);
            pelletHits.RemoveAll(h => now - h.time > 0.5f);
            recents.RemoveAll(r => now - r.time > 0.5f);
            PruneGroups(sweeps, now, 5f);
            PruneGroups(strikes, now, table.throwWindow + 2f);
        }

        void PruneGroups(Dictionary<int, Group> groups, float now, float life)
        {
            if (groups.Count == 0) return;
            stale.Clear();
            foreach (KeyValuePair<int, Group> pair in groups)
                if (now - pair.Value.lastTime > life) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) groups.Remove(stale[i]);
        }

        // ------------------------------------------------------------------ Analyse d'une casse

        bool IsThrown(BreakReport r)
        {
            return !r.wasHeld && r.releasedAt >= 0f && r.time - r.releasedAt <= table.throwWindow;
        }

        void Resolve(BreakReport r, float now)
        {
            ScoreCause cause;
            Group group = null;          // compteur de bonus alimenté par cette casse
            float distance = -1f;        // distance de tir ou de lancer, si elle a un sens
            List<DistanceTier> distanceTiers = null;
            Gun shooter = null;

            LiveContext ctx = probe != null ? probe(r) : default(LiveContext);

            int toolId, thrownId;
            Vector3 thrownFrom;
            PelletHit hit;
            Blast blast;

            if (FindPelletHit(r, out hit))
            {
                // Tir : une bille a touché ce point à la même image.
                cause = ScoreCause.Tir;
                shooter = GetGun(hit.gunId);
                group = LineGroup(shooter, hit, r);
                // Bonus de distance une seule fois par ligne de tir, pas pour chaque objet traversé.
                if (group.count == 0)
                {
                    distance = Vector3.Distance(hit.muzzle, r.point);
                    distanceTiers = table.longShotBonus;
                }
            }
            else if ((blast = FindBlast(r, false)) != null)
            {
                cause = ScoreCause.Explosion;
                group = blast.root.breaks;
            }
            else if (ctx.inHornBlast)
            {
                cause = ScoreCause.Klaxon;
            }
            else if (r.wasHeld)
            {
                cause = ScoreCause.EnMain;
            }
            else if (ctx.heldToolId != 0)
            {
                cause = ScoreCause.Coup;
                group = TimedGroup(sweeps, ctx.heldToolId, table.sweepBonus, now, table.sweepWindow);
            }
            else if (FindRecentHeld(r, out toolId))
            {
                // L'objet tenu qui a servi à frapper s'est cassé lui aussi dans le même geste.
                cause = ScoreCause.Coup;
                group = TimedGroup(sweeps, toolId, table.sweepBonus, now, table.sweepWindow);
            }
            else if (IsThrown(r))
            {
                distance = Vector3.Distance(r.releasePos, r.point);
                if (distance >= table.minThrowDistance)
                {
                    cause = ScoreCause.Lancer;
                    distanceTiers = table.longThrowBonus;
                }
                else
                {
                    cause = ScoreCause.EnMain;
                    distance = -1f;
                }
            }
            else if (ctx.thrownId != 0)
            {
                cause = ScoreCause.Projectile;
                group = TimedGroup(strikes, ctx.thrownId, table.strikeBonus, now, table.throwWindow);
                // Bonus de distance au premier objet touché seulement.
                if (group.count == 0)
                {
                    distance = Vector3.Distance(ctx.thrownFrom, r.point);
                    distanceTiers = table.longThrowBonus;
                }
            }
            else if (FindRecentThrown(r, out thrownId, out thrownFrom))
            {
                // L'objet lancé s'est cassé à l'impact : on le retrouve parmi les casses voisines.
                // Pas de bonus de distance ici : l'objet lancé l'a déjà reçu pour sa propre casse.
                cause = ScoreCause.Projectile;
                group = TimedGroup(strikes, thrownId, table.strikeBonus, now, table.throwWindow);
            }
            else if ((blast = FindBlast(r, true)) != null)
            {
                // Objet soufflé par une explosion, qui casse en retombant.
                cause = ScoreCause.Explosion;
                group = blast.root.breaks;
            }
            else
            {
                cause = ScoreCause.Collateral;
            }

            Award(r, cause, now);

            if (group != null)
            {
                group.count++;
                group.lastTime = now;
                if (!group.dirty)
                {
                    group.dirty = true;
                    dirty.Add(group);
                }
            }

            if (distanceTiers != null) AwardDistance(distanceTiers, distance);

            // Série sans faute : ce tir a cassé quelque chose.
            if (shooter != null && shooter.shots.Count > 0)
            {
                shooter.shots.Dequeue();
                shooter.streak++;
                AwardExact(table.streakBonus, shooter.streak);
            }
        }

        bool FindPelletHit(BreakReport r, out PelletHit best)
        {
            best = default(PelletHit);
            float bestDistance = PelletMatchDistance;
            bool found = false;
            for (int i = 0; i < pelletHits.Count; i++)
            {
                PelletHit h = pelletHits[i];
                if (Mathf.Abs(h.frame - r.frame) > FrameSlack) continue;
                float d = Vector3.Distance(h.point, r.point);
                if (d > bestDistance) continue;
                bestDistance = d;
                best = h;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// Explosion responsable d'une casse. aftermath = faux : la casse a lieu pendant l'explosion, dans son rayon.
        /// aftermath = vrai : la casse a lieu peu après, un peu plus loin (objet soufflé qui retombe).
        /// </summary>
        Blast FindBlast(BreakReport r, bool aftermath)
        {
            Blast best = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < blasts.Count; i++)
            {
                Blast b = blasts[i];
                float d = Vector3.Distance(b.center, r.point);
                if (aftermath)
                {
                    float delay = r.time - b.time;
                    if (delay < 0f || delay > table.blastAftermath) continue;
                    if (d > b.radius * 2f + 0.5f) continue;
                }
                else
                {
                    if (Mathf.Abs(b.frame - r.frame) > FrameSlack) continue;
                    if (d > b.radius * 1.15f + 0.3f) continue;
                }
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = b;
            }
            return best;
        }

        bool FindRecentHeld(BreakReport r, out int toolId)
        {
            toolId = 0;
            for (int i = 0; i < recents.Count; i++)
            {
                Recent x = recents[i];
                if (!x.wasHeld || x.objectId == r.objectId) continue;
                if (Mathf.Abs(x.frame - r.frame) > FrameSlack) continue;
                if (Vector3.Distance(x.point, r.point) > HeldMatchDistance) continue;
                toolId = x.objectId;
                return true;
            }
            return false;
        }

        bool FindRecentThrown(BreakReport r, out int thrownId, out Vector3 from)
        {
            thrownId = 0;
            from = default(Vector3);
            for (int i = 0; i < recents.Count; i++)
            {
                Recent x = recents[i];
                if (!x.thrown || x.objectId == r.objectId) continue;
                if (Mathf.Abs(x.frame - r.frame) > FrameSlack) continue;
                if (Vector3.Distance(x.point, r.point) > ThrownMatchDistance) continue;
                thrownId = x.objectId;
                from = x.releasePos;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ Groupes de bonus

        Gun GetGun(int id)
        {
            Gun g;
            if (!guns.TryGetValue(id, out g))
            {
                g = new Gun();
                guns[id] = g;
            }
            return g;
        }

        /// <summary>
        /// Tir en ligne : la casse prolonge la ligne en cours si elle suit de près la précédente et se trouve
        /// dans le même axe vu depuis le canon. Sinon elle commence une nouvelle ligne.
        /// </summary>
        Group LineGroup(Gun g, PelletHit hit, BreakReport r)
        {
            Vector3 fromOrigin = r.point - g.lineOrigin;
            bool sameLine = g.line != null
                         && r.time - g.lineLast <= table.lineWindow
                         && fromOrigin.sqrMagnitude > 1e-4f
                         && Vector3.Angle(fromOrigin, g.lineDir) <= table.lineAngle;

            if (!sameLine)
            {
                g.line = new Group { tiers = table.lineBonus };
                g.lineOrigin = hit.muzzle;
                Vector3 dir = r.point - hit.muzzle;
                g.lineDir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            }
            g.lineLast = r.time;
            return g.line;
        }

        /// <summary>Groupe qui dure tant que les casses se suivent d'assez près ; sinon un nouveau groupe le remplace.</summary>
        Group TimedGroup(Dictionary<int, Group> groups, int key, List<BonusTier> tiers, float now, float window)
        {
            Group g;
            if (!groups.TryGetValue(key, out g) || now - g.lastTime > window)
            {
                g = new Group { tiers = tiers, lastTime = now };
                groups[key] = g;
            }
            return g;
        }

        /// <summary>Accorde tous les paliers franchis depuis le dernier passage, en un seul message.</summary>
        void Flush(Group g)
        {
            g.dirty = false;
            if (g.tiers == null || g.count <= g.awardedUpTo)
            {
                g.awardedUpTo = g.count;
                return;
            }

            int points = 0, bestCount = int.MinValue;
            string label = null;
            for (int i = 0; i < g.tiers.Count; i++)
            {
                BonusTier t = g.tiers[i];
                if (t == null || t.count <= g.awardedUpTo || t.count > g.count) continue;
                points += t.points;
                if (t.count > bestCount)
                {
                    bestCount = t.count;
                    label = t.label;
                }
            }
            g.awardedUpTo = g.count;
            if (label != null) GiveBonus(label, points, g.count);
        }

        /// <summary>Accorde le palier dont le seuil vaut exactement le compteur (compteurs qui montent de 1 en 1).</summary>
        void AwardExact(List<BonusTier> tiers, int count)
        {
            if (tiers == null) return;
            for (int i = 0; i < tiers.Count; i++)
            {
                BonusTier t = tiers[i];
                if (t != null && t.count == count) GiveBonus(t.label, t.points, count);
            }
        }

        void AwardDistance(List<DistanceTier> tiers, float meters)
        {
            if (tiers == null) return;
            DistanceTier best = null;
            for (int i = 0; i < tiers.Count; i++)
            {
                DistanceTier t = tiers[i];
                if (t == null || meters < t.meters) continue;
                if (best == null || t.meters > best.meters) best = t;
            }
            if (best != null) GiveBonus(best.label, best.points, 0);
        }

        // ------------------------------------------------------------------ Points

        void Award(BreakReport r, ScoreCause cause, float now)
        {
            ComboCount++;
            comboExpire = now + table.comboWindow;
            if (ComboCount > BestCombo) BestCombo = ComboCount;
            Multiplier = table.MultiplierFor(ComboCount);

            int points = table.Round(r.basePoints * table.CauseMultiplier(cause) * Multiplier);
            Score += points;
            ComboPoints += points;
            ObjectsBroken++;

            Scored?.Invoke(new ScoreEvent
            {
                label = r.label,
                cause = cause,
                causeLabel = table.CauseLabel(cause),
                basePoints = r.basePoints,
                points = points,
                multiplier = Multiplier,
                comboCount = ComboCount,
                point = r.point,
                crushed = r.crushed
            });

            // Variété : nombre de types de casse différents dans le combo.
            if (cause != ScoreCause.Collateral)
            {
                causeMask |= 1 << (int)cause;
                int kinds = 0;
                for (int m = causeMask; m != 0; m >>= 1) kinds += m & 1;
                if (kinds > varietyAwarded)
                {
                    varietyAwarded = kinds;
                    AwardExact(table.varietyBonus, kinds);
                }
            }
        }

        void GiveBonus(string label, int points, int count)
        {
            if (points <= 0 && string.IsNullOrEmpty(label)) return;
            if (points > 0)
            {
                Score += points;
                if (ComboCount > 0) ComboPoints += points;
            }
            Bonus?.Invoke(new BonusEvent { label = label, points = Mathf.Max(0, points), count = count });
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Zone d'apparition supplémentaire (ex. 2e table). Elle n'a pas de file d'attente propre :
    /// c'est l'ObjectSpawner qui répartit les objets d'une commande entre toutes ses zones.
    /// </summary>
    public class SpawnZone : MonoBehaviour
    {
        [Tooltip("Taille de la boîte. L'échelle du transform est aussi prise en compte. Le bas = surface de pose.")]
        public Vector3 areaSize = new Vector3(1.5f, 1f, 0.8f);

        public Vector3 WorldSize => Vector3.Scale(areaSize, transform.lossyScale);

        /// <summary>Hauteur au-dessus du bas de la zone d'où l'on cherche la surface (table, sol).</summary>
        public const float SurfaceProbeHeight = 1.5f;

        /// <summary>Raison du dernier échec de FindFreePose (pour le message de l'ObjectSpawner).</summary>
        public static string LastFailure { get; private set; } = "";

        /// <summary>
        /// Cherche une position libre dans l'emprise au sol de la zone. La surface (table, sol)
        /// est trouvée par un rayon vertical : l'objet est posé dessus, même si la boîte est
        /// un peu enfoncée ou trop basse. Le joueur (calque Ignore Raycast) n'est pas une surface.
        /// « local » est la boîte englobante de l'objet à sa taille réelle (échelle du prefab comprise).
        /// </summary>
        public static bool FindFreePose(Transform zone, Vector3 worldSize, Bounds local, float margin,
                                        int attempts, LayerMask mask, out Vector3 pos, out Quaternion rot)
        {
            var half = local.extents + Vector3.one * margin;
            float radius = new Vector2(local.extents.x, local.extents.z).magnitude;
            float rangeX = Mathf.Max(0f, worldSize.x * 0.5f - radius);
            float rangeZ = Mathf.Max(0f, worldSize.z * 0.5f - radius);
            float bottomY = -worldSize.y * 0.5f;

            int noSurface = 0, blocked = 0;
            Vector3 lastCenter = default;
            Quaternion lastRot = Quaternion.identity;
            string lastSurface = "";

            for (int i = 0; i < attempts; i++)
            {
                rot = zone.rotation * Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                var probeLocal = new Vector3(Random.Range(-rangeX, rangeX), bottomY + SurfaceProbeHeight, Random.Range(-rangeZ, rangeZ));
                var probe = zone.position + zone.rotation * probeLocal;

                if (!Physics.Raycast(probe, Vector3.down, out var hit, SurfaceProbeHeight + 1f,
                                     mask & Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    noSurface++;
                    continue;
                }

                // Objet posé sur la surface, avec une petite marge
                var center = new Vector3(probe.x, hit.point.y + local.extents.y + margin * 2f, probe.z);
                pos = center - rot * local.center; // le pivot du prefab n'est pas forcément au centre

                if (!Physics.CheckBox(center, half, rot, mask, QueryTriggerInteraction.Ignore))
                    return true;

                blocked++;
                lastCenter = center;
                lastRot = rot;
                lastSurface = hit.collider.name;
            }

            LastFailure = DescribeFailure(half, attempts, noSurface, blocked, lastCenter, lastRot, lastSurface, mask);
            pos = default;
            rot = default;
            return false;
        }

        static string DescribeFailure(Vector3 half, int attempts, int noSurface, int blocked,
                                      Vector3 lastCenter, Quaternion lastRot, string lastSurface, LayerMask mask)
        {
            Vector3 box = half * 2f;
            var sb = new System.Text.StringBuilder();
            sb.Append($"boîte testée {box.x:0.00} x {box.y:0.00} x {box.z:0.00} m ; ")
              .Append($"{noSurface}/{attempts} essais sans surface sous la zone, {blocked}/{attempts} essais bloqués");

            if (blocked > 0)
            {
                var names = new List<string>();
                foreach (var col in Physics.OverlapBox(lastCenter, half, lastRot, mask, QueryTriggerInteraction.Ignore))
                {
                    string n = col.name + " (" + col.GetType().Name + ")";
                    if (!names.Contains(n) && names.Count < 6) names.Add(n);
                }
                sb.Append($" ; dernier essai : posé sur « {lastSurface} » à {lastCenter.y:0.00} m de haut, bloqué par ")
                  .Append(names.Count > 0 ? string.Join(", ", names) : "(rien retrouvé)");
            }
            return sb.ToString();
        }

        public static void DrawGizmo(Transform zone, Vector3 worldSize)
        {
            Gizmos.matrix = Matrix4x4.TRS(zone.position, zone.rotation, Vector3.one);
            Gizmos.color = new Color(1f, 0.45f, 0.1f, 0.25f);
            Gizmos.DrawCube(Vector3.zero, worldSize);
            Gizmos.color = new Color(1f, 0.45f, 0.1f, 1f);
            Gizmos.DrawWireCube(Vector3.zero, worldSize);
            // Surface de pose (bas de la zone)
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(new Vector3(0f, -worldSize.y * 0.5f, 0f), new Vector3(worldSize.x, 0f, worldSize.z));
        }

        void OnDrawGizmos() => DrawGizmo(transform, WorldSize);
    }
}
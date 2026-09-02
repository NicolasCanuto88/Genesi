using System.Collections.Generic;
using UnityEngine;

namespace SpaceSurvivor.Collision
{
    /// <summary>
    /// CompoundColliderAuthoring — Rev AB (Blocco 3.2.d — D5).
    /// MonoBehaviour da attaccare al GameObject di un oggetto compound (in
    /// primis: Nave). Espone una lista serializable di CompoundVolume che
    /// definisce la geometria di collisione dell'oggetto.
    ///
    /// USO:
    ///   - Nave: aggiungere al GameObject Nave (fratello di ShipMovement /
    ///     PropulsionSystem / DockingController / PoiCollisionResolver /
    ///     ShipImpactHandler). ShipMovement lo cacha in Awake e lo espone
    ///     via property Compound. Configurare i volumi in Inspector: es.
    ///     3-4 OBB per fusoliera + ali + eventuali motori.
    ///
    /// PER I POI: la geometria è per-CATEGORIA (tutti i relitti dello stesso
    /// PoiData hanno stessa mesh), quindi la lista di volumi vive su PoiData
    /// (SO), non qui. PoiInstance espone un proxy verso Data.CollisionVolumes.
    /// Questo componente NON deve essere aggiunto ai prefab POI — sarebbe
    /// duplicazione della configurazione.
    ///
    /// GIZMOS (Rev AB, Q8 = A — solo wireframe v1):
    ///   OnDrawGizmos          — disegna ogni volume in WIREFRAME sempre visibile.
    ///                           OBB azzurro, Sphere verde.
    ///   OnDrawGizmosSelected  — disegna ogni volume in FILLED (semitrasparente)
    ///                           quando il GameObject è selezionato. Colori più
    ///                           saturi per visibilità.
    ///
    ///   Nota: la posizione WORLD del compound è dedotta da transform.position
    ///   e transform.rotation di questo GameObject SOLO in editor (per il
    ///   preview visuale). A runtime, la posizione/rotazione LOGICA
    ///   (ShipMovement.LogicalPosition + LogicalRotation, o PoiInstance
    ///   analoghe) è passata esplicitamente al math helper — la mesh visuale
    ///   può divergere dalla posizione logica (nave ferma in worldspace,
    ///   invariante Rev Q).
    ///
    ///   Handles interattivi (resize/rotate come BoxCollider nativo) sono
    ///   RIMANDATI a debito dedicato D9 (Q8 Rev AA workshop). In Rev AB il
    ///   tuning avviene via drag numerici in Inspector + gizmi wireframe
    ///   per feedback visivo.
    ///
    /// DIPENDE DA: —
    /// USATO DA:   ShipMovement (property Compound), CompoundColliderMath
    ///             (indirettamente, tramite consumer che passano la lista).
    /// </summary>
    [DisallowMultipleComponent]
    public class CompoundColliderAuthoring : MonoBehaviour
    {
        [Tooltip("Lista di volumi primitivi (OBB / Sphere) che compongono la " +
                 "geometria di collisione dell'oggetto. Ogni volume è definito " +
                 "in LOCAL space rispetto a LogicalPosition + LogicalRotation " +
                 "dell'oggetto ospite (Nave o POI). La lista può essere vuota " +
                 "(oggetto senza collisione — sconsigliato in gameplay reale).")]
        [SerializeField] private List<CompoundVolume> volumes = new List<CompoundVolume>();

        /// <summary>
        /// Lista read-only dei volumi. Il chiamante deve trasformare local→world
        /// usando la LogicalPosition + LogicalRotation dell'oggetto ospite
        /// (NON transform.position/rotation di questo GameObject: la Nave è
        /// statica in worldspace, invariante Rev Q).
        /// </summary>
        public IReadOnlyList<CompoundVolume> Volumes => volumes;

        /// <summary>Numero di volumi configurati (comodo per early-return del math helper).</summary>
        public int Count => volumes.Count;

        // =========================================================================
        // GIZMOS — Q8 A (wireframe v1, handles interattivi rimandati a D9)
        // =========================================================================
#if UNITY_EDITOR
        [Header("Debug (Editor only)")]
        [Tooltip("Se true, disegna i gizmi anche quando il GameObject non è " +
                 "selezionato (wireframe sempre visibile). Utile per navigare la " +
                 "scena e vedere l'ingombro della Nave. Default true.")]
        [SerializeField] private bool alwaysShowGizmos = true;

        [Tooltip("Colore dei volumi OBB. Default azzurro (coerente col " +
                 "BoxCollider nativo Unity).")]
        [SerializeField] private Color obbColor = new Color(0.4f, 0.7f, 1f, 1f);

        [Tooltip("Colore dei volumi Sphere. Default verde (SphereCollider nativo).")]
        [SerializeField] private Color sphereColor = new Color(0.4f, 1f, 0.5f, 1f);

        private void OnDrawGizmos()
        {
            if (!alwaysShowGizmos) return;
            DrawGizmosInternal(filled: false);
        }

        private void OnDrawGizmosSelected()
        {
            // Selected: disegno filled anche se alwaysShowGizmos era off,
            // così la selezione è comunque evidente.
            DrawGizmosInternal(filled: true);
        }

        private void DrawGizmosInternal(bool filled)
        {
            if (volumes == null || volumes.Count == 0) return;

            // Preview in editor: usa transform.position/rotation del GameObject
            // ospite. A runtime la posizione LOGICA può divergere — questo è un
            // preview di editing, non uno stato runtime.
            Vector3 basePos = transform.position;
            Quaternion baseRot = transform.rotation;

            for (int i = 0; i < volumes.Count; i++)
            {
                CompoundVolume v = volumes[i];
                Vector3 worldCenter = basePos + baseRot * v.localPosition;
                Quaternion worldRot = baseRot * Quaternion.Euler(v.localEulerAngles);

                switch (v.type)
                {
                    case CompoundVolumeType.OBB:
                        DrawObbGizmo(worldCenter, worldRot, v.scale, obbColor, filled);
                        break;

                    case CompoundVolumeType.Sphere:
                        DrawSphereGizmo(worldCenter, v.Radius, sphereColor, filled);
                        break;
                }
            }
        }

        private static void DrawObbGizmo(
            Vector3 center, Quaternion rotation, Vector3 fullScale,
            Color color, bool filled)
        {
            Matrix4x4 prev = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(center, rotation, Vector3.one);

            if (filled)
            {
                Color fillColor = color;
                fillColor.a = 0.15f;
                Gizmos.color = fillColor;
                Gizmos.DrawCube(Vector3.zero, fullScale);
            }

            Gizmos.color = color;
            Gizmos.DrawWireCube(Vector3.zero, fullScale);

            Gizmos.matrix = prev;
        }

        private static void DrawSphereGizmo(
            Vector3 center, float radius, Color color, bool filled)
        {
            if (filled)
            {
                Color fillColor = color;
                fillColor.a = 0.15f;
                Gizmos.color = fillColor;
                Gizmos.DrawSphere(center, radius);
            }

            Gizmos.color = color;
            Gizmos.DrawWireSphere(center, radius);
        }
#endif
    }
}

using System;
using UnityEngine;

namespace SpaceSurvivor.Collision
{
    /// <summary>
    /// Tipo primitivo di un volume di collisione compound.
    /// Rev AB (Blocco 3.2.d — D5): OBB + Sphere. Capsule esclusa v1 (Q1 Rev AA).
    /// Aggiungere una nuova variante = un nuovo entry nel dispatcher di
    /// CompoundColliderMath.
    /// </summary>
    public enum CompoundVolumeType
    {
        /// <summary>Oriented Bounding Box. halfExtents = (scale.x, scale.y, scale.z) * 0.5.</summary>
        OBB = 0,

        /// <summary>Sfera. radius = scale.x * 0.5 (scale.y/z IGNORATI, uniformità garantita a livello geometrico).</summary>
        Sphere = 1,
    }

    /// <summary>
    /// CompoundVolume — Rev AB (Blocco 3.2.d — D5).
    ///
    /// Descrittore serializable di UN volume primitivo (OBB o Sphere) all'interno
    /// del compound collider di un oggetto (Nave o POI). Ogni oggetto espone una
    /// lista di questi volumi (via CompoundColliderAuthoring per la Nave, via
    /// PoiData per le categorie POI). La lista è la geometria di collisione
    /// dell'oggetto, sostituisce il modello sferico (HardCollisionRadius +
    /// ShipCollisionRadius) usato fino a Rev AA.
    ///
    /// DECISIONI ARCHITETTURALI (Rev AA workshop, Q1-Q3):
    ///   Q1 = OBB + Sphere (capsule esclusa v1).
    ///   Q2 = Compound su entrambi (Nave e POI, più volumi per oggetto).
    ///   Q3 = Struttura per volume = Position / Rotation / Scale (Transform-like).
    ///
    /// SEMANTICA CAMPI:
    ///   type              — OBB o Sphere.
    ///   localPosition     — centro del volume, in LOCAL space del compound
    ///                       (ossia rispetto a LogicalPosition + LogicalRotation
    ///                       dell'oggetto ospite: Nave o POI). Non è una
    ///                       Transform nativa Unity; il compound non ha un
    ///                       Transform intermedio.
    ///   localEulerAngles  — rotazione in gradi (yaw, pitch, roll), in LOCAL
    ///                       space del compound. Applicata via
    ///                       Quaternion.Euler(localEulerAngles). Ignorato per
    ///                       Sphere (sfera invariante per rotazione).
    ///   scale             — per OBB: dimensione totale (halfExtents = scale/2)
    ///                       per Sphere: diametro = scale.x (Y/Z ignorati)
    ///                       Convenzione Transform-like (Q3): scale rappresenta
    ///                       la dimensione TOTALE dell'oggetto, non half-size.
    ///
    /// TRASFORMAZIONE LOCAL → WORLD (calcolata al volo dal math helper):
    ///   worldCenter_i    = objectLogicalPos + objectLogicalRot * localPosition
    ///   worldRotation_i  = objectLogicalRot * Quaternion.Euler(localEulerAngles)
    ///   worldHalfExtents = scale * 0.5   (per OBB — la scala del compound non
    ///                                     è modificata da rotazione)
    ///   worldRadius      = scale.x * 0.5 (per Sphere)
    ///
    /// SCELTA: value type (struct). Nessuna allocazione heap per liste di volumi
    /// tipicamente piccole (2-5 elementi). Serializzazione Unity nativa
    /// (Serializable + [SerializeField List&lt;CompoundVolume&gt;]).
    ///
    /// GIZMOS: disegnati da CompoundColliderAuthoring (per la Nave) e da un
    /// eventuale gizmo su PoiInstance (per i POI, in v1 solo runtime nel resolver).
    /// Colori (Rev AB, Q8 = A): OBB azzurro, Sphere verde. Selezionato = filled,
    /// unselected = wireframe. Runtime: coppia in massima compenetrazione = rosso.
    /// </summary>
    [Serializable]
    public struct CompoundVolume
    {
        [Tooltip("Tipo primitivo del volume. OBB per corpi paralleliepipedali " +
                 "(fusoliera nave, cubo POI, ali, motori). Sphere per corpi " +
                 "tondeggianti (meteoriti, POI sferici).")]
        public CompoundVolumeType type;

        [Tooltip("Centro del volume in LOCAL space del compound (rispetto a " +
                 "LogicalPosition + LogicalRotation dell'oggetto ospite). Unità: " +
                 "unità logiche.")]
        public Vector3 localPosition;

        [Tooltip("Rotazione del volume in LOCAL space del compound, in gradi " +
                 "Euler (yaw, pitch, roll). Ignorato per Sphere (invariante per " +
                 "rotazione).")]
        public Vector3 localEulerAngles;

        [Tooltip("Dimensione TOTALE del volume (convenzione Transform-like). " +
                 "Per OBB: halfExtents = scale/2 su ogni asse. Per Sphere: " +
                 "diametro = scale.x, componenti Y/Z ignorati. Unità: unità " +
                 "logiche.")]
        public Vector3 scale;

        /// <summary>
        /// Costruttore di comodo per OBB. Rotazione default (0,0,0).
        /// </summary>
        public static CompoundVolume Obb(Vector3 localPos, Vector3 localEuler, Vector3 scale)
        {
            return new CompoundVolume
            {
                type = CompoundVolumeType.OBB,
                localPosition = localPos,
                localEulerAngles = localEuler,
                scale = scale,
            };
        }

        /// <summary>
        /// Costruttore di comodo per Sphere. Il diametro è dedotto da scale.x;
        /// scale.y/z sono ignorati ma salvati per coerenza serializzazione.
        /// </summary>
        public static CompoundVolume Sphere(Vector3 localPos, float diameter)
        {
            return new CompoundVolume
            {
                type = CompoundVolumeType.Sphere,
                localPosition = localPos,
                localEulerAngles = Vector3.zero,
                scale = new Vector3(diameter, diameter, diameter),
            };
        }

        /// <summary>
        /// halfExtents in local space (OBB). Non applicabile a Sphere — il
        /// chiamante deve controllare type prima.
        /// </summary>
        public Vector3 HalfExtents => scale * 0.5f;

        /// <summary>
        /// Radius in local space (Sphere). Non applicabile a OBB — il chiamante
        /// deve controllare type prima.
        /// </summary>
        public float Radius => scale.x * 0.5f;
    }
}

using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// DockingAnchor — Rev AB (Blocco 3.2.d — D5, Q6 = B).
    ///
    /// Marker component da attaccare a un GameObject figlio del ROOT del
    /// prefab POI (NON sotto "Visual" — Visual è mosso a runtime da
    /// ExternalWorldFollower e trascinerebbe via l'anchor). La Transform
    /// dell'anchor (position + forward) definisce il PUNTO DI ATTRACCO e la
    /// DIREZIONE DI APPROCCIO del docking, decoupled dalla geometria di
    /// collisione (compound OBB+Sphere, Q1-Q3 Rev AA).
    ///
    /// MOTIVAZIONE (Q6 workshop Rev AB):
    ///   Fino a Rev AA il DockingController derivava l'asse di approccio
    ///   da rotation del POI × PoiData.DockingApproachDirectionLocal. Con
    ///   il passaggio a compound collider (Rev AB) il concetto "raggio di
    ///   attracco" perde senso: il POI ha più volumi, quale è "quello di
    ///   attracco"?
    ///
    ///   Q6 = B (Anchor Transform separato): la collisione descrive "dove
    ///   sto"; il docking descrive "come mi avvicino". Sono due
    ///   responsabilità semanticamente ortogonali. Un POI-relitto può avere
    ///   fusoliera principale come volume maggiore e docking bay laterale
    ///   su un'ala: l'anchor sta sull'ala, il volume principale sta sulla
    ///   fusoliera.
    ///
    /// USO:
    ///   1. Nel prefab POI: creare un GameObject figlio DIRETTO del root
    ///      (fratello di Visual), chiamato "DockingAnchor".
    ///   2. Posizionarlo sul punto di attracco (tipicamente sul bordo del
    ///      compound, dove il pilota deve terminare la manovra) guardando
    ///      la mesh sotto Visual come riferimento visivo.
    ///   3. Orientare il forward (Z+) del Transform in modo che punti FUORI
    ///      dal POI, verso la direzione da cui la nave deve arrivare.
    ///      Convention: pilota arriva lungo -DockingAnchor.forward, ovvero
    ///      "opposto al forward dell'anchor".
    ///   4. Aggiungere questo componente sul GameObject (nessun setup
    ///      inspector — è un marker).
    ///
    /// FALLBACK (anchor mancante):
    ///   PoiInstance risolve DockingAnchorForwardWorld con GetComponentInChildren.
    ///   Se non trovato, emette Debug.LogWarning UNA VOLTA e fallisce su
    ///   convention pre-Rev AB: forward = LogicalRotation * Vector3.up
    ///   (l'up del POI, coerente con la default direction Vector3.up del
    ///   vecchio dockingApproachDirection di PoiData).
    ///
    /// GIZMOS:
    ///   Freccia blu lungo forward (lunghezza 5u) per visualizzare la
    ///   direzione di approccio nell'editor. Sfera piccola sul pivot per
    ///   identificare la posizione. Visibili sempre (non solo selezionato)
    ///   per navigazione scena.
    ///
    /// DIPENDE DA: — (marker)
    /// USATO DA:   PoiInstance (GetComponentInChildren&lt;DockingAnchor&gt;)
    /// </summary>
    [DisallowMultipleComponent]
    public class DockingAnchor : MonoBehaviour
    {
        // Marker — nessun campo runtime. La Transform di questo GameObject
        // è tutto ciò che serve.

#if UNITY_EDITOR
        [Header("Debug (Editor only)")]
        [Tooltip("Lunghezza della freccia gizmo che rappresenta la direzione " +
                 "di approccio (forward). Default 5 u logiche.")]
        [SerializeField] private float gizmoArrowLength = 5f;

        [Tooltip("Colore della freccia + pivot. Default blu (coerente con Z+ " +
                 "convention di Unity per il forward).")]
        [SerializeField] private Color gizmoColor = new Color(0.3f, 0.5f, 1f, 1f);

        private void OnDrawGizmos()
        {
            Gizmos.color = gizmoColor;

            // Pivot: piccola sfera sul GameObject.
            Gizmos.DrawSphere(transform.position, 0.5f);

            // Freccia forward.
            Vector3 origin = transform.position;
            Vector3 tip = origin + transform.forward * gizmoArrowLength;
            Gizmos.DrawLine(origin, tip);

            // Punte della freccia (2 segmenti a 30° dal forward).
            Vector3 right = transform.right * (gizmoArrowLength * 0.15f);
            Vector3 up = transform.up * (gizmoArrowLength * 0.15f);
            Vector3 back = -transform.forward * (gizmoArrowLength * 0.25f);

            Gizmos.DrawLine(tip, tip + back + right);
            Gizmos.DrawLine(tip, tip + back - right);
            Gizmos.DrawLine(tip, tip + back + up);
            Gizmos.DrawLine(tip, tip + back - up);
        }
#endif
    }
}

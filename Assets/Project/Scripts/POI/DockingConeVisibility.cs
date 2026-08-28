using UnityEngine;
using SpaceSurvivor.Ship;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// DockingConeVisibility — Milestone 3, Blocco 3.1.b (D5, Rev W).
    ///
    /// MonoBehaviour rendering-only (NON NetworkBehaviour) da attaccare al
    /// GameObject del cono di attracco nel prefab POI (attualmente il figlio
    /// 'Cylinder' di PoiInstance_Wreck).
    ///
    /// RESPONSABILITÀ:
    /// Attiva o disattiva il MeshRenderer del cono in base alla distanza logica
    /// nave↔POI, con soglia PoiData.coneVisibilityRadius. Sopra la soglia il
    /// cono è disabilitato (nessuno lo vede); sotto la soglia il cono è
    /// abilitato ma renderizzato SOLO dalla camera del pilota locale seduto
    /// (grazie a Layer 'DockingConeVisual' + Culling Mask gestiti da
    /// PilotStation — D9).
    ///
    /// D5 e D9 sono ORTOGONALI:
    ///   - D5 (questo script): gate globale di distanza. Nessuno vede il cono
    ///     se sei lontano dal POI, incluso il pilota.
    ///   - D9 (PilotStation):   gate di ruolo. Solo la camera del pilota locale
    ///     seduto include il layer DockingConeVisual nella cullingMask.
    /// Cono visibile ⇔ (distanza ≤ ConeVisibilityRadius) AND (spettatore == pilota
    /// locale seduto).
    ///
    /// POLLING:
    /// Il check è deliberatamente NON per-frame: InvokeRepeating a
    /// pollIntervalSeconds (default 0.2s). Un ritardo di 200ms
    /// nell'attivazione/disattivazione visiva è impercettibile alle velocità
    /// realistiche della nave, e riduce carico. Il polling parte
    /// automaticamente in OnEnable e si sospende in OnDisable — resistente a
    /// disable dinamici (respawn POI, teardown scene).
    ///
    /// DIPENDE DA:
    ///   - PoiInstance genitore (risolto in Awake via GetComponentInParent)
    ///   - PoiData.ConeVisibilityRadius (accessor introdotto in Rev W)
    ///   - ShipMovement.Instance.LogicalPosition (nave statica in world space,
    ///     posizione logica NetVar-driven)
    ///   - MeshRenderer sul proprio GameObject (auto-cache in Awake)
    ///
    /// SETUP EDITOR (documentato nel prefab):
    ///   1. Nel prefab PoiInstance_Wreck, selezionare il GameObject 'Cylinder'.
    ///   2. Add Component → Docking Cone Visibility.
    ///   3. Nessun campo da assegnare: tutto risolto per componente automatico.
    ///
    /// EDGE CASES gestiti:
    ///   - ShipMovement.Instance == null (transizioni scena, teardown): il
    ///     polling resta attivo ma no-op, il renderer conserva l'ultimo stato.
    ///   - PoiInstance o Data nulli: warning una volta in Awake, componente
    ///     rimane inerte (renderer nello stato del prefab, tipicamente attivo).
    ///   - MeshRenderer nullo: warning una volta in Awake, poi no-op.
    /// </summary>
    [DisallowMultipleComponent]
    public class DockingConeVisibility : MonoBehaviour
    {
        [Tooltip("Intervallo di polling della distanza in secondi. Default 0.2s. " +
                 "Aumentare se si notano stutter marginali con molti POI in vista; " +
                 "diminuire (fino a 0.05s) se si nota latenza di attivazione " +
                 "all'ingresso in coneVisibilityRadius. NON usare per-frame " +
                 "(Update): un check di distanza al frame è sprecato per un " +
                 "cambio di stato binario che avviene raramente.")]
        [Range(0.05f, 1.0f)]
        [SerializeField] private float pollIntervalSeconds = 0.2f;

        [Tooltip("Se true, logga i cambi di stato (visibile↔invisibile) in " +
                 "Console per debug del D5. Disattivare in playtest normale.")]
        [SerializeField] private bool debugLogStateChanges = false;

        // ── Riferimenti cachati ──────────────────────────────────────────────

        private PoiInstance poiInstance;
        private MeshRenderer meshRenderer;
        private bool isSetupValid;
        private bool lastVisibleState;
        private bool hasLoggedInvalidSetup;

        // =========================================================================
        // LIFECYCLE
        // =========================================================================

        private void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            poiInstance = GetComponentInParent<PoiInstance>();

            if (meshRenderer == null)
            {
                Debug.LogWarning(
                    $"[DockingConeVisibility] {name}: nessun MeshRenderer sul " +
                    "GameObject. Il componente non farà nulla.");
                isSetupValid = false;
                return;
            }

            if (poiInstance == null)
            {
                Debug.LogWarning(
                    $"[DockingConeVisibility] {name}: nessun PoiInstance nei " +
                    "genitori. Il componente non farà nulla.");
                isSetupValid = false;
                return;
            }

            isSetupValid = true;

            // Stato iniziale: cono nascosto finché non arriva il primo poll
            // (evita flash del cono a distanze lunghe subito dopo lo spawn).
            meshRenderer.enabled = false;
            lastVisibleState = false;
        }

        private void OnEnable()
        {
            if (!isSetupValid) return;
            InvokeRepeating(nameof(PollDistance), 0f, pollIntervalSeconds);
        }

        private void OnDisable()
        {
            CancelInvoke(nameof(PollDistance));
        }

        // =========================================================================
        // POLLING
        // =========================================================================

        /// <summary>
        /// Confronta la distanza logica nave↔POI con
        /// PoiInstance.Data.ConeVisibilityRadius e aggiorna
        /// meshRenderer.enabled. Nessun operazione se ShipMovement non è ancora
        /// disponibile (transizioni scena).
        /// </summary>
        private void PollDistance()
        {
            if (!isSetupValid) return;

            var ship = ShipMovement.Instance;
            if (ship == null)
            {
                // Transizione scena o teardown: non modifichiamo lo stato.
                return;
            }

            var data = poiInstance.Data;
            if (data == null)
            {
                if (!hasLoggedInvalidSetup)
                {
                    Debug.LogWarning(
                        $"[DockingConeVisibility] {name}: PoiInstance.Data è null. " +
                        "Componente inerte.");
                    hasLoggedInvalidSetup = true;
                }
                return;
            }

            float radius = data.ConeVisibilityRadius;
            float sqrDistance = (poiInstance.LogicalPosition - ship.LogicalPosition).sqrMagnitude;
            bool shouldBeVisible = sqrDistance <= radius * radius;

            if (shouldBeVisible != lastVisibleState)
            {
                meshRenderer.enabled = shouldBeVisible;
                lastVisibleState = shouldBeVisible;

                if (debugLogStateChanges)
                {
                    float dist = Mathf.Sqrt(sqrDistance);
                    Debug.Log(
                        $"[DockingConeVisibility] {poiInstance.name}: " +
                        $"cono {(shouldBeVisible ? "VISIBILE" : "nascosto")} " +
                        $"(distanza {dist:F1}m, soglia {radius:F1}m)");
                }
            }
        }
    }
}

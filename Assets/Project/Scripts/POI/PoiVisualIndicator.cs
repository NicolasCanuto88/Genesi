using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiVisualIndicator — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// MonoBehaviour client-side (non NetworkBehaviour) che si iscrive al
    /// PoiInstance parent/root e cambia il colore emissivo del Renderer
    /// del proprio GameObject in base al ScanState corrente.
    ///
    /// PERCHÉ NON NetworkBehaviour:
    ///   Il feedback visivo dipende ESCLUSIVAMENTE da un valore già
    ///   replicato (PoiInstance._scanState). Ogni client riceve la NetVar
    ///   sync e questo componente reagisce localmente. Nessuna necessità
    ///   di replicazione aggiuntiva. Stesso pattern di ExternalWorldFollower
    ///   (Rev S): "client-side simulation deterministica dallo stato
    ///   replicato".
    ///
    /// PALETTE (2b):
    ///   Unknown  → nessuna emissione (visibile solo dal contorno mesh)
    ///   Detected → cyan #00C8EF (accent identità del progetto, Rev P)
    ///   Scanned  → ambra tenue (differenzia "già analizzato" da "nuovo")
    ///
    /// ATTACCARE A:
    ///   Il GameObject "Visual" child del prefab PoiInstance, dove risiede
    ///   il Renderer. Se il modello ha più Renderer, il primo trovato
    ///   nell'albero via GetComponentInChildren viene usato.
    ///
    /// USO DI MaterialPropertyBlock:
    ///   Coerente con la regola invariante del progetto ("Luci:
    ///   MaterialPropertyBlock, no istanze materiale, aggiornare _BaseColor
    ///   e _EmissionColor insieme") — anche qui: no material.Instance,
    ///   MaterialPropertyBlock per non causare fork del materiale.
    ///
    /// DIPENDE DA:
    ///   - PoiInstance (evento OnScanStateChanged, property ScanState)
    ///   - Un Renderer sul GameObject o child (Mesh Renderer standard)
    ///   - Shader URP/Lit (usa _EmissionColor e _BaseColor come da
    ///     convenzione URP)
    /// </summary>
    public class PoiVisualIndicator : MonoBehaviour
    {
        [Header("Riferimento")]
        [Tooltip("Il PoiInstance a cui iscriversi. Se lasciato vuoto, verrà " +
                 "risolto automaticamente in Awake cercando nei parent " +
                 "(GetComponentInParent).")]
        [SerializeField] private PoiInstance poiInstance;

        [Tooltip("Renderer da colorare. Se lasciato vuoto, verrà risolto in " +
                 "Awake via GetComponent → GetComponentInChildren.")]
        [SerializeField] private Renderer targetRenderer;

        [Header("Palette colori (HDR — emission)")]
        [Tooltip("Colore emissivo per stato Detected. Default: cyan #00C8EF, " +
                 "l'accent del design system (Rev P).")]
        [ColorUsage(showAlpha: true, hdr: true)]
        [SerializeField] private Color detectedEmission = new Color(0f, 0.784f, 0.937f) * 3f;

        [Tooltip("Colore emissivo per stato Scanned. Default: ambra tenue.")]
        [ColorUsage(showAlpha: true, hdr: true)]
        [SerializeField] private Color scannedEmission = new Color(1f, 0.7f, 0.15f) * 1.5f;

        [Tooltip("Colore emissivo per stato Unknown. Default: nero (nessuna " +
                 "emissione). Il POI è tecnicamente visibile solo dal contorno " +
                 "mesh illuminato dalla luce ambientale.")]
        [ColorUsage(showAlpha: true, hdr: true)]
        [SerializeField] private Color unknownEmission = Color.black;

        [Header("Base color (opzionale — dietro all'emissione)")]
        [Tooltip("Se true, aggiorna anche _BaseColor per rendere il POI più " +
                 "visibile anche quando l'emissione è quasi assente. Default " +
                 "OFF: lascia il BaseColor originale del materiale, cambia solo " +
                 "l'emissione. Usare ON solo per debug se il POI risulta " +
                 "invisibile in scena.")]
        [SerializeField] private bool alsoUpdateBaseColor = false;

        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;

        // Shader property IDs — cachati per performance.
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private MaterialPropertyBlock _propBlock;
        private bool _initialized = false;

        // ── Lifecycle Unity ──────────────────────────────────────────────────

        private void Awake()
        {
            _propBlock = new MaterialPropertyBlock();

            if (poiInstance == null)
                poiInstance = GetComponentInParent<PoiInstance>();

            if (targetRenderer == null)
            {
                targetRenderer = GetComponent<Renderer>();
                if (targetRenderer == null)
                    targetRenderer = GetComponentInChildren<Renderer>();
            }

            if (poiInstance == null)
            {
                Debug.LogError($"[PoiVisualIndicator] {name}: PoiInstance non trovato nei parent.");
                enabled = false;
                return;
            }

            if (targetRenderer == null)
            {
                Debug.LogError($"[PoiVisualIndicator] {name}: Renderer non trovato.");
                enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            if (poiInstance == null) return;

            poiInstance.OnScanStateChanged += HandleScanStateChanged;

            // Sincronizza lo stato iniziale — necessario perché il POI
            // potrebbe già essere in stato non-Unknown quando il subscriber
            // si iscrive (es. late-join, o riattivazione del componente).
            ApplyStateColor(poiInstance.ScanState);
            _initialized = true;
        }

        private void OnDisable()
        {
            if (poiInstance == null) return;
            poiInstance.OnScanStateChanged -= HandleScanStateChanged;
        }

        // ── Reazione al cambio di stato ──────────────────────────────────────

        private void HandleScanStateChanged(PoiScanState previous, PoiScanState next)
        {
            ApplyStateColor(next);

            if (verboseLogging)
            {
                Debug.Log($"[PoiVisualIndicator] {name}: {previous} → {next}");
            }
        }

        private void ApplyStateColor(PoiScanState state)
        {
            if (targetRenderer == null || _propBlock == null) return;

            Color emission = state switch
            {
                PoiScanState.Detected => detectedEmission,
                PoiScanState.Scanned => scannedEmission,
                _ => unknownEmission
            };

            // Legge il blocco esistente per non perdere override precedenti
            // (es. shader variant che ha _BaseColor già impostato).
            targetRenderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor(EmissionColorId, emission);

            if (alsoUpdateBaseColor)
            {
                // Base color = emission scaled down (visibile ma non
                // sovraesposta).
                Color baseCol = emission * 0.3f;
                baseCol.a = 1f;
                _propBlock.SetColor(BaseColorId, baseCol);
            }

            targetRenderer.SetPropertyBlock(_propBlock);
        }
    }
}

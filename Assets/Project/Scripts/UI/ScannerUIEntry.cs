using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SpaceSurvivor.Poi;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerUIEntry — Milestone 3, Blocco 3 · Rev BH (Fase 2b, postazione Scanner).
    ///
    /// Singola riga della lista dello ScannerUI: nome + distanza corrente di un
    /// PoiInstance. La distanza è aggiornata dal ScannerUI parent, non da qui.
    ///
    /// Rev BH — la riga è ora SELEZIONABILE: ha un Button così che l'EventSystem
    /// possa navigarla (frecce/controller) e mostrare il riquadro cyan
    /// (MenuSelectionHighlight segue la selezione, come nelle dashboard). La
    /// selezione mostra il dettaglio del POI; l'attivazione (click / Submit)
    /// richiede lo scan attivo su quel POI (evento OnActivated → ScannerUI).
    ///
    /// PREFAB (vedi guida setup Stage B):
    ///   ScannerUIEntry (RectTransform + Button [Transition None])
    ///   ├─ Icon     (TMP_Text opzionale, es. '▸')
    ///   ├─ Name     (TMP_Text — display name del POI)
    ///   └─ Distance (TMP_Text — distanza formattata)
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class ScannerUIEntry : MonoBehaviour
    {
        [Tooltip("Text del nome del POI (es. 'Relitto abbandonato').")]
        [SerializeField] private TMP_Text nameText;

        [Tooltip("Text della distanza corrente (es. '1834 m').")]
        [SerializeField] private TMP_Text distanceText;

        [Tooltip("Text dell'icona (es. '▸'). Opzionale — se null, viene ignorato.")]
        [SerializeField] private TMP_Text iconText;

        [Tooltip("Button della riga (selezione + attivazione scan). Se null viene " +
                 "risolto con GetComponent in Awake.")]
        [SerializeField] private Button button;

        /// <summary>Il POI a cui questa riga è associata (bind da ScannerUI).</summary>
        public PoiInstance Poi { get; private set; }

        /// <summary>Il Button selezionabile della riga (per la selezione EventSystem).</summary>
        public Button Button => button;

        /// <summary>Emesso quando la riga è attivata (click / Submit): ScannerUI vi
        /// aggancia la richiesta di scan attivo sul POI associato.</summary>
        public event Action<PoiInstance> OnActivated;

        private void Awake()
        {
            if (button == null) button = GetComponent<Button>();
            if (button != null) button.onClick.AddListener(HandleClick);
        }

        private void OnDestroy()
        {
            if (button != null) button.onClick.RemoveListener(HandleClick);
        }

        /// <summary>Associa la riga a un PoiInstance (una tantum, alla creazione).</summary>
        public void Bind(PoiInstance poi)
        {
            Poi = poi;
        }

        private void HandleClick()
        {
            if (Poi != null) OnActivated?.Invoke(Poi);
        }

        /// <summary>Popola il nome del POI.</summary>
        public void SetName(string displayName)
        {
            if (nameText != null) nameText.text = displayName;
        }

        /// <summary>Aggiorna la distanza. Chiamato periodicamente dallo ScannerUI.</summary>
        public void SetDistance(float distanceMeters)
        {
            if (distanceText == null) return;

            if (distanceMeters < 1000f)
                distanceText.text = $"{distanceMeters:F0} m";
            else
                distanceText.text = $"{distanceMeters / 1000f:F1} km";
        }

        /// <summary>Imposta il colore di tutti i Text della riga (Detected cyan /
        /// Scanned ambra).</summary>
        public void SetTextColor(Color c)
        {
            if (nameText != null) nameText.color = c;
            if (distanceText != null) distanceText.color = c;
            if (iconText != null) iconText.color = c;
        }
    }
}
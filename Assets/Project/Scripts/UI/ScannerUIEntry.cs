using TMPro;
using UnityEngine;

namespace SpaceSurvivor.UI
{
    /// <summary>
    /// ScannerUIEntry — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// Singola riga della lista dello ScannerUI. Mostra nome + distanza
    /// corrente di un PoiInstance. Distanza aggiornata ogni frame dal
    /// ScannerUI parent, non da questo componente.
    ///
    /// PREFAB (assemblato in Editor secondo le istruzioni della sessione):
    ///   ScannerUIEntry (RectTransform)
    ///   ├─ Icon (TMP_Text con carattere ▸ o simile)
    ///   ├─ Name (TMP_Text — display name del POI)
    ///   └─ Distance (TMP_Text — distanza formattata)
    ///
    /// Il colore del testo può essere impostato via SetTextColor per
    /// differenziare visualmente Detected (cyan) da Scanned (ambra) —
    /// coerente con la palette del PoiVisualIndicator.
    /// </summary>
    public class ScannerUIEntry : MonoBehaviour
    {
        [Tooltip("Text del nome del POI (es. 'Relitto abbandonato').")]
        [SerializeField] private TMP_Text nameText;

        [Tooltip("Text della distanza corrente (es. '1834 m').")]
        [SerializeField] private TMP_Text distanceText;

        [Tooltip("Text dell'icona (es. '▸'). Opzionale — se null, viene " +
                 "ignorato.")]
        [SerializeField] private TMP_Text iconText;

        /// <summary>Popola nome del POI (una tantum, chiamato in bind).</summary>
        public void SetName(string displayName)
        {
            if (nameText != null) nameText.text = displayName;
        }

        /// <summary>Aggiorna la distanza. Chiamato ogni frame dal
        /// ScannerUI parent.</summary>
        public void SetDistance(float distanceMeters)
        {
            if (distanceText == null) return;

            // Formattazione: sotto 1000m mostra intero, sopra 1000m
            // mostra in km con una decimale (es. "2.3 km").
            if (distanceMeters < 1000f)
                distanceText.text = $"{distanceMeters:F0} m";
            else
                distanceText.text = $"{distanceMeters / 1000f:F1} km";
        }

        /// <summary>Imposta il colore di tutti i Text della entry.</summary>
        public void SetTextColor(Color c)
        {
            if (nameText != null) nameText.color = c;
            if (distanceText != null) distanceText.color = c;
            if (iconText != null) iconText.color = c;
        }
    }
}

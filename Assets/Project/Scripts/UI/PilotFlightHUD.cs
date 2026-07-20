using TMPro;
using SpaceSurvivor.Ship;
using UnityEngine;

/// <summary>
/// PilotFlightHUD — Milestone 3, Blocco 3 fase 2 (Rev T).
/// Heads-up display sovrapposto durante il volo MANUAL in vista terza persona.
///
/// COMPLEMENTA PilotHUD:
///   PilotHUD (esistente) = strumentazione della plancia (World Space, sul
///     monitor del cockpit). Visibile quando il Pilota guarda il monitor
///     (vista cockpit).
///   PilotFlightHUD (questo) = HUD del Pilota in volo esterno (Screen Space
///     Overlay). Visibile quando la camera è in vista terza persona MANUAL.
///
/// I due sono mutualmente esclusivi — PilotStation orchestra il toggle
/// insieme allo swap camera cockpit ↔ terza persona (EnterThirdPersonChaseCam /
/// ExitThirdPersonChaseCam).
///
/// CONTENUTO MINIMO (Rev T — versione iniziale):
///   - Velocità: current + target + max, colore adattivo (stable/accel/decel).
///     Formato identico al PilotHUD per coerenza percettiva.
///   - Throttle bar: mostra target/maxCap come frazione — "dove ho posizionato
///     la leva". Non mostra current — quella si vede nel numero.
///   - Warning "avvia motori per sterzare": attivo in MANUAL quando
///     CurrentSpeed &lt; minSpeedToSteer di ShipMovement. Feedback critico
///     scoperto in playtest — senza questo il Pilota non capisce perché il
///     mouse non risponde a velocità bassa.
///
/// NON MOSTRATI QUI (sono nel PilotHUD della plancia):
///   fuel, FTL, scudi HP, zone type.
///
/// CANVAS SETUP (Inspector):
///   Render Mode: Screen Space - Overlay (NO World Space)
///   Scale: 1,1,1
///   Radice fuori da "Nave" e fuori da Player — es. GameObject
///   "UI_Overlays" nella scena Game.unity
///
/// PATTERN Open/Close: identico agli altri IDashboardPanel — usato da
///   PilotStation per attivare/disattivare insieme allo swap camera.
///   Il GameObject deve partire ATTIVO in scena (Open è chiamato subito),
///   ma nascosto tramite un CanvasGroup (alpha=0) OPPURE lasciato attivo e
///   PilotStation chiama Close() all'ingresso. Vedi documentazione uso.
///
/// DIPENDE DA: PropulsionSystem · ShipMovement (per la soglia CanSteer)
/// USATO DA: PilotStation.EnterThirdPersonChaseCam / ExitThirdPersonChaseCam
/// </summary>
public class PilotFlightHUD : MonoBehaviour, IDashboardPanel
{
    // =========================================================================
    // ELEMENTI UI (assegnare in Inspector)
    // =========================================================================

    [Header("— Velocità —")]
    [Tooltip("Label velocità: 'VEL: 42.3 → 60.0  (max 100)  m/s'. " +
             "Freccia visibile solo se accelera/decelera (stabile = solo current).")]
    [SerializeField] private TextMeshProUGUI labelSpeed;

    [Header("— Throttle —")]
    [Tooltip("Barra throttle: target/maxCap. Mostra 'dove ho posizionato la leva'. " +
             "La velocità corrente si vede nel numero del labelSpeed.")]
    [SerializeField] private SciFiSegmentedBar barThrottle;

    [Header("— Warning 'Avvia motori' —")]
    [Tooltip("GameObject attivato quando in MANUAL e la velocità è sotto la soglia " +
             "minSpeedToSteer di ShipMovement. Deve contenere il testo warning già " +
             "impostato (es. '⚠ MOTORI FERMI — ACCELERARE PER STERZARE'). Il codice " +
             "si limita a SetActive true/false.")]
    [SerializeField] private GameObject warningNoSteer;

    // =========================================================================
    // COLORI VELOCITÀ (coerenti col PilotHUD)
    // =========================================================================

    [Header("— Colori velocità (coerenti col PilotHUD) —")]
    [SerializeField] private Color colorSpeedStable = new Color(0.20f, 1.00f, 0.50f);
    [SerializeField] private Color colorSpeedAccelerating = new Color(0.00f, 0.80f, 1.00f);
    [SerializeField] private Color colorSpeedDecelerating = new Color(1.00f, 0.70f, 0.10f);

    // =========================================================================
    // SOGLIA "no steer" (deve corrispondere a ShipMovement.minSpeedToSteer)
    // =========================================================================

    [Header("— Soglia warning 'no steer' —")]
    [Tooltip("DEVE corrispondere al valore di minSpeedToSteer configurato in " +
             "ShipMovement (Inspector di 'Nave' → ShipMovement). Non c'è modo " +
             "pulito di leggerlo runtime senza aggiungere una proprietà pubblica " +
             "in ShipMovement — se cambi la soglia lì, ricordati di rifletterla " +
             "anche qui. Default 3 m/s (stesso default di ShipMovement).")]
    [SerializeField] private float minSpeedToSteerMirror = 3f;

    // =========================================================================
    // IDashboardPanel
    // =========================================================================

    /// <summary>
    /// Chiamato da PilotStation.EnterThirdPersonChaseCam. Attiva il GameObject
    /// e avvia il refresh periodico.
    /// </summary>
    public void Open()
    {
        gameObject.SetActive(true);
        RefreshAll();
        InvokeRepeating(nameof(RefreshAll), 0.25f, 0.25f);
    }

    /// <summary>
    /// Chiamato da PilotStation.ExitThirdPersonChaseCam (o all'uscita station).
    /// Sospende il refresh e disattiva il GameObject.
    /// </summary>
    public void Close()
    {
        CancelInvoke(nameof(RefreshAll));
        gameObject.SetActive(false);
    }

    // =========================================================================
    // REFRESH — chiamato ogni 0.25s da InvokeRepeating
    // =========================================================================

    private void RefreshAll()
    {
        RefreshSpeed();
        RefreshThrottle();
        RefreshNoSteerWarning();
    }

    // ── Velocità ──────────────────────────────────────────────────────────

    private void RefreshSpeed()
    {
        if (labelSpeed == null) return;

        var ps = PropulsionSystem.Instance;
        if (ps == null)
        {
            labelSpeed.text = "VEL: ---";
            labelSpeed.color = colorSpeedStable;
            return;
        }

        float current = ps.CurrentSpeed;
        float target = ps.TargetSpeed;
        float maxCap = ps.MaxSpeedAtDegradation;
        float diff = target - current;

        string text;
        Color color;
        if (Mathf.Abs(diff) < 0.5f)
        {
            text = $"VEL: {current:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedStable;
        }
        else if (diff > 0f)
        {
            text = $"VEL: {current:F1} → {target:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedAccelerating;
        }
        else
        {
            text = $"VEL: {current:F1} ← {target:F1}  (max {maxCap:F0})  m/s";
            color = colorSpeedDecelerating;
        }

        labelSpeed.text = text;
        labelSpeed.color = color;
    }

    // ── Throttle bar ──────────────────────────────────────────────────────

    private void RefreshThrottle()
    {
        if (barThrottle == null) return;

        var ps = PropulsionSystem.Instance;
        if (ps == null)
        {
            barThrottle.SetValue(0f);
            return;
        }

        float target = ps.TargetSpeed;
        float maxCap = ps.MaxSpeedAtDegradation;
        float pct = maxCap > 0.01f ? Mathf.Clamp01(target / maxCap) : 0f;

        barThrottle.SetValue(pct);
    }

    // ── Warning "avvia motori" ────────────────────────────────────────────

    private void RefreshNoSteerWarning()
    {
        if (warningNoSteer == null) return;

        var ps = PropulsionSystem.Instance;
        bool shouldShow = ps != null
                       && ps.CurrentNavState == NavigationState.Manual
                       && ps.CurrentSpeed < minSpeedToSteerMirror;

        if (warningNoSteer.activeSelf != shouldShow)
            warningNoSteer.SetActive(shouldShow);
    }
}
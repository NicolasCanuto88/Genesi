using UnityEngine;
using Unity.Netcode;
using SpaceSurvivor.Ship;

/// <summary>
/// Screen shake della camera del player locale (Rev AE, Blocco 3.2.d parte 2).
/// Componente su GameObject Camera figlio del Player prefab.
///
/// HOTFIX Rev AE.hotfix2 — Pattern DELTA (self-cancelling):
///
/// Le due implementazioni precedenti (cattura ogni frame + additivo, poi
/// cattura in Trigger + set assoluto) avevano un difetto comune: assumevano
/// che qualcuno riscrivesse la posa base ogni frame per "correggere" il
/// residuo shake. Questa assunzione è FALSA quando il player è seduto in
/// una postazione: PilotStation/EngineeringStation/MedicalStation
/// disabilitano PlayerController.enabled = false → nessun pitch write →
/// nessuna auto-correzione della rotation.
///
/// Nuovo pattern DELTA:
///   - NON cattura una posa base né sulla position né sulla rotation.
///   - Ogni frame calcola l'offset shake corrente (Perlin decay) e applica
///     ALLA CAMERA il DELTA rispetto all'offset del frame precedente:
///         delta_pos = new_offset - prev_offset
///         transform.localPosition += delta_pos
///         delta_rot = Inverse(prev_rot) * new_rot
///         transform.localRotation *= delta_rot
///   - All'ULTIMO frame di shake (t≥1) l'offset è forzato a zero:
///         delta = 0 - prev_offset = -prev_offset
///     Il delta SOTTRAE il residuo shake — la camera torna esattamente
///     alla posa "vera" corrente (base + eventuale pitch di PlayerController
///     se abilitato, o base ferma in stazione).
///
/// Vantaggi:
///   - Funziona identico con PlayerController abilitato (first-person) e
///     disabilitato (stazione).
///   - Sopravvive al re-parenting perché opera in local space del parent
///     corrente (aborta pulito solo se parent cambia mid-shake).
///   - Nessun ripristino "hard" che possa cancellare aggiornamenti legittimi
///     di terzi (es. pitch nuovo scritto da PlayerController mid-shake).
///   - Auto-cancellante: al termine del decay il residuo è matematicamente
///     annullato dal delta finale.
///
/// PATTERN LOCAL-ONLY (invariato):
///   Auto-disabilitazione se NetworkObject padre esiste e non è owner.
///
/// API PUBBLICA (invariata):
///   CameraShaker.LocalInstance.Trigger(ImpactSeverity)
/// </summary>
[DisallowMultipleComponent]
public class CameraShaker : MonoBehaviour
{
    public static CameraShaker LocalInstance { get; private set; }

    [Header("Rotation shake (deg, max at amplitude=1)")]
    [Tooltip("Max angolo (gradi) applicato come euler additivo alla rotazione " +
             "della camera, scalato dall'ampiezza corrente del decay. Rev AE " +
             "default 2° — abbastanza percepibile senza indurre motion sickness.")]
    [Range(0f, 10f)]
    [SerializeField] private float rotationShakeDegrees = 2.0f;

    [Header("Debug")]
    [SerializeField] private bool logShakes = false;

    private bool _isShaking = false;
    private float _shakeStartTime;
    private float _shakeAmplitude;
    private float _shakeDuration;
    private float _shakeFrequency;

    // Pattern DELTA: memorizza offset applicato al frame precedente. Il delta
    // corrente = new - prev. Non serve cattura di posa base.
    private Vector3 _prevPosOffset;
    private Quaternion _prevRotOffset;

    // Parent al momento del Trigger — se cambia mid-shake abortiamo pulito
    // (il player è entrato/uscito da una stazione durante l'urto).
    private Transform _shakeStartParent;

    private float _seedPosX, _seedPosY, _seedPosZ;
    private float _seedRotX, _seedRotY, _seedRotZ;

    private void Awake()
    {
        var netObj = GetComponentInParent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned && !netObj.IsOwner)
        {
            enabled = false;
            return;
        }

        _seedPosX = Random.value * 1000f;
        _seedPosY = Random.value * 1000f;
        _seedPosZ = Random.value * 1000f;
        _seedRotX = Random.value * 1000f;
        _seedRotY = Random.value * 1000f;
        _seedRotZ = Random.value * 1000f;
    }

    private void OnEnable()
    {
        var netObj = GetComponentInParent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned && !netObj.IsOwner)
        {
            enabled = false;
            return;
        }

        LocalInstance = this;
    }

    private void OnDisable()
    {
        if (LocalInstance == this) LocalInstance = null;
    }

    /// <summary>
    /// Avvia uno shake della severity data. Pattern DELTA: nessuna cattura
    /// di posa base. Se uno shake era già in corso, il nuovo Trigger azzera
    /// prevOffset — l'eventuale residuo del vecchio shake resta scritto
    /// sulla camera come parte della "nuova base" percepita, e verrà
    /// implicitamente riassorbito dal nuovo decay che parte da zero.
    /// </summary>
    public void Trigger(ImpactSeverity severity)
    {
        var p = ImpactThresholdTable.GetShakeParams(severity);
        _shakeAmplitude = p.Amplitude;
        _shakeDuration = Mathf.Max(0.01f, p.Duration);
        _shakeFrequency = p.Frequency;
        _shakeStartTime = Time.time;

        // Baseline: nessun offset ancora applicato in questo shake.
        _prevPosOffset = Vector3.zero;
        _prevRotOffset = Quaternion.identity;
        _shakeStartParent = transform.parent;

        _isShaking = true;

        if (logShakes)
        {
            Debug.Log($"[CameraShaker] Trigger {ImpactThresholdTable.DebugLabel(severity)} " +
                      $"amp={_shakeAmplitude:F3}u dur={_shakeDuration:F2}s freq={_shakeFrequency:F0}Hz");
        }
    }

    /// <summary>
    /// LateUpdate applica il DELTA di offset (new - prev) sia per position
    /// che rotation. All'ultimo frame l'offset è forzato a zero → delta =
    /// -prevOffset → sottrae il residuo → camera esattamente sulla base
    /// "vera" corrente.
    /// </summary>
    private void LateUpdate()
    {
        if (!_isShaking) return;

        // Aborta se parent cambia mid-shake (player entra/esce stazione durante urto).
        // Il residuo shake resta scritto sulla camera del vecchio parent, ma il
        // nuovo parent ha già impostato la sua localPosition/Rotation via
        // Enter/Exit → non c'è danno visibile nel nuovo contesto.
        if (transform.parent != _shakeStartParent)
        {
            _isShaking = false;
            if (logShakes)
                Debug.Log("[CameraShaker] Shake abortito: parent cambiato mid-shake.");
            return;
        }

        float elapsed = Time.time - _shakeStartTime;
        float t = elapsed / _shakeDuration;

        Vector3 newPosOffset;
        Quaternion newRotOffset;
        bool isFinalFrame = (t >= 1f);

        if (isFinalFrame)
        {
            // Frame finale: offset target = zero. Il delta = -prevOffset
            // sottrae il residuo dello shake → camera torna a base pulita.
            newPosOffset = Vector3.zero;
            newRotOffset = Quaternion.identity;
        }
        else
        {
            // Decay lineare 1 → 0
            float posAmp = _shakeAmplitude * (1f - t);
            float samplePos = elapsed * _shakeFrequency;

            newPosOffset = new Vector3(
                (Mathf.PerlinNoise(_seedPosX, samplePos) - 0.5f) * 2f * posAmp,
                (Mathf.PerlinNoise(_seedPosY, samplePos) - 0.5f) * 2f * posAmp,
                (Mathf.PerlinNoise(_seedPosZ, samplePos) - 0.5f) * 2f * posAmp);

            float rotAmp = rotationShakeDegrees * (1f - t);
            Vector3 rotEuler = new Vector3(
                (Mathf.PerlinNoise(_seedRotX, samplePos) - 0.5f) * 2f * rotAmp,
                (Mathf.PerlinNoise(_seedRotY, samplePos) - 0.5f) * 2f * rotAmp,
                (Mathf.PerlinNoise(_seedRotZ, samplePos) - 0.5f) * 2f * rotAmp);
            newRotOffset = Quaternion.Euler(rotEuler);
        }

        // ── Applica DELTA rispetto al frame precedente ────────────────────────
        // Position: delta = new - prev, applicato come somma incrementale.
        Vector3 deltaPos = newPosOffset - _prevPosOffset;
        transform.localPosition += deltaPos;

        // Rotation: delta_rot = Inverse(prev) * new. Applicato come multiply
        // sulla rotation corrente (compone col contesto attuale, incluso
        // eventuale pitch appena scritto da PlayerController se abilitato).
        Quaternion deltaRot = Quaternion.Inverse(_prevRotOffset) * newRotOffset;
        transform.localRotation = transform.localRotation * deltaRot;

        _prevPosOffset = newPosOffset;
        _prevRotOffset = newRotOffset;

        if (isFinalFrame)
        {
            _isShaking = false;
            if (logShakes)
                Debug.Log("[CameraShaker] Shake completato, residuo annullato via delta.");
        }
    }
}
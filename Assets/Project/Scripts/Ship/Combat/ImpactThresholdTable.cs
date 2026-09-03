using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// Tabella statica di soglie e parametri per il feedback teatrale
    /// da impatto (Rev AE, Blocco 3.2.d parte 2 — QF-1 confermata).
    ///
    /// UNA SOLA sorgente di verità condivisa tra shake (CameraShaker) e
    /// audio (ImpactAudioController): stesse soglie → stessa severity
    /// percepita sui due canali per lo stesso urto. Se i canali usassero
    /// tabelle indipendenti, un urto a v=3.05 u/s potrebbe suonare "medium"
    /// e shake-are "light" — dissonanza percettiva da evitare.
    ///
    /// PLACEHOLDER Milestone 1B — QF-1: valori hardcoded const. Se il
    /// playtest Rev AE evidenzia necessità di tuning frequente in editor,
    /// promuoviamo a ScriptableObject (debito D20). Fino ad allora, evitiamo
    /// il costo di un asset da versionare per 3 numeri.
    ///
    /// La soglia inferiore di Light NON è definita qui: coincide con
    /// DockingController.ConfirmMaxVelocity (invariante Rev X). Sotto quella
    /// soglia ShipImpactHandler scarta l'impatto e questa tabella non viene
    /// mai interrogata.
    /// </summary>
    public static class ImpactThresholdTable
    {
        // ── Soglie velocity (u/s) ─────────────────────────────────────────────
        // Boundary Light→Medium: 3.0 u/s (urto sopra RCS max ma sotto Coasting)
        // Boundary Medium→Hard : 8.0 u/s (limite RCS in docking = pilotaggio
        //                                  fine fallito → collisione dura)
        private const float MediumThreshold = 3.0f;
        private const float HardThreshold = 8.0f;

        // ── Shake per severity ────────────────────────────────────────────────
        // Amplitude in unità locali (metri) — offset massimo della camera.
        // Duration in secondi — durata totale del decay Perlin.
        // Frequency in Hz — rate della perturbazione Perlin.
        //
        // Valori tarati per feel Lethal Company-like: Light appena percepibile
        // (evita nausea su bump di allineamento fine), Hard nettamente scioccante
        // (comunica "hai fatto un errore serio") senza sfociare in disorientamento
        // che comprometta il recovery post-impatto.
        private const float LightShakeAmplitude = 0.04f;
        private const float LightShakeDuration = 0.20f;
        private const float LightShakeFrequency = 22f;

        private const float MediumShakeAmplitude = 0.14f;
        private const float MediumShakeDuration = 0.40f;
        private const float MediumShakeFrequency = 18f;

        private const float HardShakeAmplitude = 0.32f;
        private const float HardShakeDuration = 0.75f;
        private const float HardShakeFrequency = 14f;

        // ── Audio per severity ────────────────────────────────────────────────
        // Volume in scala lineare (0..1). Pitch base — variazione random ±5%
        // applicata da ImpactAudioController per evitare loop percettivi
        // ripetendo lo stesso hit più volte a rapida distanza.
        private const float LightAudioVolume = 0.55f;
        private const float LightAudioPitch = 1.15f;

        private const float MediumAudioVolume = 0.85f;
        private const float MediumAudioPitch = 1.00f;

        private const float HardAudioVolume = 1.00f;
        private const float HardAudioPitch = 0.85f;

        /// <summary>
        /// Parametri completi di shake per una data severity.
        /// Struct passata per valore — nessuna allocazione.
        /// </summary>
        public readonly struct ShakeParams
        {
            public readonly float Amplitude;
            public readonly float Duration;
            public readonly float Frequency;

            public ShakeParams(float amplitude, float duration, float frequency)
            {
                Amplitude = amplitude;
                Duration = duration;
                Frequency = frequency;
            }
        }

        /// <summary>
        /// Parametri audio per una data severity. Usati da
        /// ImpactAudioController per configurare PlayOneShot con volume+pitch
        /// appropriati.
        /// </summary>
        public readonly struct AudioParams
        {
            public readonly float Volume;
            public readonly float Pitch;

            public AudioParams(float volume, float pitch)
            {
                Volume = volume;
                Pitch = pitch;
            }
        }

        /// <summary>
        /// Classifica una radial impact velocity nella severity discreta
        /// corrispondente. Assume velocity ≥ ConfirmMaxVelocity (controllo
        /// upstream in ShipImpactHandler).
        /// </summary>
        public static ImpactSeverity Classify(float radialImpactVelocity)
        {
            if (radialImpactVelocity >= HardThreshold) return ImpactSeverity.Hard;
            if (radialImpactVelocity >= MediumThreshold) return ImpactSeverity.Medium;
            return ImpactSeverity.Light;
        }

        /// <summary>
        /// Parametri di shake per la severity data.
        /// </summary>
        public static ShakeParams GetShakeParams(ImpactSeverity severity)
        {
            switch (severity)
            {
                case ImpactSeverity.Hard:
                    return new ShakeParams(HardShakeAmplitude, HardShakeDuration, HardShakeFrequency);
                case ImpactSeverity.Medium:
                    return new ShakeParams(MediumShakeAmplitude, MediumShakeDuration, MediumShakeFrequency);
                default:
                    return new ShakeParams(LightShakeAmplitude, LightShakeDuration, LightShakeFrequency);
            }
        }

        /// <summary>
        /// Parametri audio per la severity data.
        /// </summary>
        public static AudioParams GetAudioParams(ImpactSeverity severity)
        {
            switch (severity)
            {
                case ImpactSeverity.Hard:
                    return new AudioParams(HardAudioVolume, HardAudioPitch);
                case ImpactSeverity.Medium:
                    return new AudioParams(MediumAudioVolume, MediumAudioPitch);
                default:
                    return new AudioParams(LightAudioVolume, LightAudioPitch);
            }
        }

        /// <summary>
        /// Etichetta debug leggibile per log.
        /// </summary>
        public static string DebugLabel(ImpactSeverity severity)
        {
            switch (severity)
            {
                case ImpactSeverity.Hard:   return "HARD";
                case ImpactSeverity.Medium: return "MEDIUM";
                default:                    return "LIGHT";
            }
        }
    }
}

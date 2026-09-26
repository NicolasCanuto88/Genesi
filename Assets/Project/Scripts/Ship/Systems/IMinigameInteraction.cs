using System.Collections;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// IMinigameInteraction — seam "QB" (Rev BN · strategy interaction)
    ///
    /// Il MODELLO D'INTERAZIONE di un ProgressiveMinigame (cosa fa il giocatore per
    /// far salire la barra) vive dietro questa interfaccia. La base resta padrona di:
    /// lifecycle, progresso/decay, soglie 50/75/100, grace, timer, UI-shell, hook di
    /// ruolo e win-mode (Path H). Ogni archetipo porta la propria meccanica:
    ///   - MashSliderInteraction : default (mash + slider burst, GDD §9.8) — i 3 minigame vivi
    ///   - (BO) interazione "sutura" : archetipo 4, tracking continuo — Corpsman
    ///
    /// CONTRATTO (ordine garantito dalla base):
    ///   1. Begin(host)  → a OGNI BeginSession, dopo l'accensione di rootCanvas e prima
    ///                     che la sessione diventi attiva. Resetta stato e UI propri.
    ///                     NON abilita input e NON avvia routine (invariante Rev BL).
    ///   2. Enable()     → UNA volta per sessione, al flip di fine grace (punto singolo
    ///                     Rev BL). Qui si cablano input e routine.
    ///   3. Tick(dt)     → ogni frame post-grace mentre la sessione è attiva, DOPO decay
    ///                     e controllo floor, PRIMA dell'aggiornamento UI della barra.
    ///   4. Disable()    → a ogni chiusura (successo / interruzione / timer / floor).
    ///                     Deve essere idempotente e sicuro anche se Enable non è mai
    ///                     stato chiamato (interruzione durante il grace), e può arrivare
    ///                     RIENTRANTE dentro una chiamata a host.ApplyPoints (soglia 100%
    ///                     → chiusura) — mai assumere di essere "fuori" dalla catena.
    ///
    /// REGOLA D'ORO: l'interazione NON scrive mai il progresso. Riporta punti solo via
    /// IMinigameHost.ApplyPoints, che centralizza gli invarianti (moltiplicatore di
    /// ruolo sui soli guadagni positivi, clamp 0–100, feedback PRIMA delle soglie,
    /// effetto di soglia una sola volta, nessun punto a sessione inattiva o in grace).
    /// Così nessuna strategia futura può aggirare l'invariante materiali o Rev BL.
    ///
    /// Le FASI di un'interazione (es. sutura per condizione) non passano da qui: il
    /// derivato che la crea (CreateInteraction) ne tiene un riferimento tipizzato.
    /// L'interfaccia non vincola il tipo concreto: classe C# semplice o componente.
    /// </summary>
    public interface IMinigameInteraction
    {
        /// <summary>Inizio sessione: reset stato/UI. Nessun input, nessuna routine.</summary>
        void Begin(IMinigameHost host);

        /// <summary>Fine grace (una volta per sessione): cabla input e routine.</summary>
        void Enable();

        /// <summary>Frame post-grace a sessione attiva (dopo decay + floor, prima della UI).</summary>
        void Tick(float deltaTime);

        /// <summary>Chiusura: sgancia input, ferma routine, spegne la propria UI. Idempotente.</summary>
        void Disable();

        /// <summary>Riga per l'overlay di debug della base (standard Rev BA).</summary>
        string DebugLine { get; }
    }

    /// <summary>
    /// Servizi che la base (ProgressiveMinigame) offre all'interazione.
    /// Implementato ESPLICITAMENTE dalla base: non inquina l'API dei derivati.
    /// </summary>
    public interface IMinigameHost
    {
        /// <summary>True tra BeginSession e la chiusura.</summary>
        bool IsActive { get; }

        /// <summary>Colori di feedback correnti della base (letti al momento dell'uso).</summary>
        MinigamePalette Palette { get; }

        /// <summary>
        /// Unico canale verso il progresso. Ordine garantito:
        ///   1. rawPoints &gt; 0 → × moltiplicatore di ruolo (Rev U); negativi invariati
        ///   2. progresso = clamp(progresso + punti, 0, 100)
        ///   3. feedback di stato (se non null) — PRIMA delle soglie, così il
        ///      messaggio di soglia/completamento prevale
        ///   4. controllo soglie (effetto one-shot; al 100% può chiudere la sessione)
        /// Ignorato se la sessione non è attiva o è in grace (invariante Rev BL).
        /// </summary>
        void ApplyPoints(float rawPoints, string feedback, Color feedbackColor, float feedbackDuration);

        /// <summary>Avvia una coroutine sul MonoBehaviour della base (temporizzazione Unity invariata).</summary>
        Coroutine StartHostedRoutine(IEnumerator routine);

        /// <summary>Ferma una coroutine avviata con StartHostedRoutine. Null-safe.</summary>
        void StopHostedRoutine(Coroutine routine);
    }

    /// <summary>Snapshot dei colori di feedback della base.</summary>
    public readonly struct MinigamePalette
    {
        public readonly Color Good;
        public readonly Color Warning;
        public readonly Color Critical;
        public readonly Color Neutral;

        public MinigamePalette(Color good, Color warning, Color critical, Color neutral)
        {
            Good = good;
            Warning = warning;
            Critical = critical;
            Neutral = neutral;
        }
    }
}

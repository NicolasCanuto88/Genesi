using UnityEngine;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiCollisionMath — Milestone 3 Fase 3 Blocco 3.2.c.
    /// Classe statica pura, stateless. Zero dipendenze da NGO, singleton,
    /// Time.deltaTime. Testabile mentalmente in isolamento.
    ///
    /// RESPONSABILITÀ:
    ///   Fonte unica di verità per il pattern "clamp posizionale hard + slide
    ///   tangenziale" contro un POI. Estratto dal DockingController (Rev W
    ///   D8, invariante Rev X: mesh POI fisicamente inattraversabile) in
    ///   Rev AA per essere riusato dal PoiCollisionResolver fuori dal
    ///   Docking (Manual / Coasting / Autopilot).
    ///
    ///   Chiamare da:
    ///     - DockingController.RunDockingTick (Docking, POI singolo ancorato)
    ///     - PoiCollisionResolver.ResolveCollision (Manual/Coasting/Autopilot,
    ///       POI scelto server-side come il più vicino tra quelli che sforano)
    ///
    /// SEMANTICA:
    ///   Data una posizione corrente della nave, una posizione candidata (già
    ///   integrata da velocity * dt), il vettore velocity, la posizione del
    ///   POI, il raggio hard del POI e il raggio di collisione della nave:
    ///     - Il raggio EFFETTIVO di clamp è (poiRadius + shipRadius) —
    ///       formula fisica "distanza min tra centri = somma dei raggi".
    ///       Aggiunto in Rev AA hotfix dopo playtest 3.2.c.4: senza
    ///       shipRadius il clamp scattava solo quando il PUNTO
    ///       LogicalPosition entrava nella mesh POI, e l'intera metà avanti
    ///       della nave era già visibilmente compenetrata prima del clamp.
    ///     - Se candidatePos NON sfora (poiRadius + shipRadius) → passa
    ///       attraverso (nessuna modifica, hadCollision = false).
    ///     - Se candidatePos sfora → clampa al bordo esterno del raggio
    ///       effettivo, azzera radiale, preserva tangenziale (slide).
    ///       hadCollision = true.
    ///
    ///   La direzione radiale è calcolata da candidatePos verso l'esterno
    ///   (candidateFromPoi.normalized). Se candidatePos è degenerata (~sulla
    ///   POI), fallback su currentPos; se anche quella è degenerata, fallback
    ///   sul vettore di fallback fornito (ultimo di default sensato, es.
    ///   l'asse di approccio del POI in Docking, o Vector3.up nel resolver
    ///   Manual).
    ///
    /// INVARIANTE (post-clamp):
    ///   distance(clampedPos, poiPos) &gt;= (poiRadius + shipRadius) sempre
    ///   (se useHardClamp è true). La geometria della nave non compenetra
    ///   più la mesh del POI.
    ///
    /// NB — semantica di radialImpactSpeed:
    ///   Valore restituito solo se hadCollision == true. È la MAGNITUDINE
    ///   della componente radiale della velocità verso il POI al momento
    ///   del contatto (sempre &gt;= 0). Consumer previsto:
    ///   OnHardCollision(radialImpactSpeed, poi) → ShipImpactHandler
    ///   applica la formula danno + trasferimento momento a partire da
    ///   questo scalare.
    /// </summary>
    public static class PoiCollisionMath
    {
        /// <summary>
        /// Soglia numerica sotto cui una distanza radiale è considerata
        /// degenere (fallback su ripiego geometrico). Applicata alle
        /// magnitudini candidateFromPoi e currentFromPoi.
        /// </summary>
        public const float DegenerateDistanceEpsilon = 1e-4f;

        /// <summary>
        /// Risultato del clamp contro un singolo POI. Struct value-type
        /// (nessuna allocazione heap in chiamate ad alta frequenza).
        /// </summary>
        public struct ClampResult
        {
            /// <summary>Posizione applicata alla nave (clampata o originale).</summary>
            public Vector3 ClampedPosition;

            /// <summary>Velocità applicata alla nave (con radiale azzerata se collisione).</summary>
            public Vector3 ClampedVelocity;

            /// <summary>true se è avvenuta collisione (candidate sforava hardRadius e la velocità puntava verso il POI).</summary>
            public bool HadCollision;

            /// <summary>Magnitudine della componente radiale verso il POI al contatto (0 se HadCollision == false).</summary>
            public float RadialImpactSpeed;

            /// <summary>Direzione radiale outward (nave→esterno) usata per il clamp. Vector3.zero se HadCollision == false.</summary>
            public Vector3 RadialDirOutward;
        }

        /// <summary>
        /// Applica il pattern clamp+slide contro un singolo POI.
        ///
        /// PARAMETRI:
        ///   currentPos       — posizione della nave prima dell'integrazione (u logiche).
        ///   candidatePos     — posizione candidata dopo integrazione (currentPos + velocity*dt).
        ///   velocity         — velocità corrente della nave (u/s logiche).
        ///   poiPos           — posizione del POI (u logiche).
        ///   poiRadius        — HardCollisionRadius del POI (u logiche).
        ///   shipRadius       — ShipCollisionRadius della nave (u logiche). Il raggio
        ///                      EFFETTIVO usato per il clamp è (poiRadius + shipRadius),
        ///                      per rispettare "distanza min tra centri = somma dei raggi".
        ///                      Passare 0 se il chiamante vuole ignorare la geometria
        ///                      della nave (comportamento pre-Rev AA, sconsigliato).
        ///   useHardClamp     — se false, il clamp non viene applicato (candidate passa
        ///                      inalterata anche se sfora); ritorna comunque HadCollision
        ///                      per notifiche. Utile per debug o test edge.
        ///   fallbackRadial   — direzione radiale di fallback in caso di doppia
        ///                      degenerazione (candidate e current entrambe ~sulla POI).
        ///                      Passare l'asse di approccio in Docking, Vector3.up nel
        ///                      resolver Manual (o qualunque direzione unitaria sensata
        ///                      nel contesto chiamante).
        ///
        /// RITORNO:
        ///   ClampResult con posizione/velocità post-clamp + flag di collisione.
        /// </summary>
        public static ClampResult ClampAgainstPoi(
            Vector3 currentPos,
            Vector3 candidatePos,
            Vector3 velocity,
            Vector3 poiPos,
            float poiRadius,
            float shipRadius,
            bool useHardClamp,
            Vector3 fallbackRadial)
        {
            // Raggio effettivo del clamp (formula fisica: somma dei raggi).
            float effectiveRadius = poiRadius + shipRadius;

            ClampResult result = new ClampResult
            {
                ClampedPosition = candidatePos,
                ClampedVelocity = velocity,
                HadCollision = false,
                RadialImpactSpeed = 0f,
                RadialDirOutward = Vector3.zero,
            };

            Vector3 candidateFromPoi = candidatePos - poiPos;
            float candidateDist = candidateFromPoi.magnitude;

            // Nessuno sforamento → passa attraverso inalterata.
            if (candidateDist >= effectiveRadius) return result;

            // useHardClamp = false: non correggere la posizione (utile in debug),
            // ma segnala comunque la collisione se la velocità puntava verso il POI.
            // Semantica strict-equal all'implementazione originale del DockingController.
            if (!useHardClamp)
            {
                // Direzione radiale outward (stessa logica di fallback del ramo attivo).
                Vector3 radialDirNoClamp = ResolveRadialDirection(
                    candidateFromPoi, candidateDist,
                    currentPos - poiPos,
                    fallbackRadial);

                float radialSpeedNoClamp = Vector3.Dot(velocity, radialDirNoClamp);
                if (radialSpeedNoClamp < 0f)
                {
                    result.HadCollision = true;
                    result.RadialImpactSpeed = -radialSpeedNoClamp;
                    result.RadialDirOutward = radialDirNoClamp;
                }
                return result;
            }

            // ── Clamp posizionale hard attivo ────────────────────────────
            Vector3 radialDir = ResolveRadialDirection(
                candidateFromPoi, candidateDist,
                currentPos - poiPos,
                fallbackRadial);

            // Posizione clampata al bordo esterno del raggio EFFETTIVO
            // (poiRadius + shipRadius) — la geometria della nave resta fuori.
            result.ClampedPosition = poiPos + radialDir * effectiveRadius;
            result.RadialDirOutward = radialDir;

            // Decompone velocità: radiale (positivo = outward) + tangenziale.
            // Se radiale < 0 la nave stava puntando dentro la mesh → azzera radiale,
            // preserva tangenziale (slide). Se radiale >= 0 la nave è già in uscita:
            // il clamp precedente ha già lavorato, non toccare la velocità.
            float radialSpeed = Vector3.Dot(velocity, radialDir);
            if (radialSpeed < 0f)
            {
                result.HadCollision = true;
                result.RadialImpactSpeed = -radialSpeed;
                result.ClampedVelocity = velocity - radialDir * radialSpeed;
            }

            return result;
        }

        /// <summary>
        /// Risolve la direzione radiale outward con la stessa catena di fallback
        /// del DockingController Rev W (righe 601-613): prima candidateFromPoi
        /// normalizzata, poi currentFromPoi normalizzata, infine fallbackRadial
        /// (già assunto unitario, ma normalizziamo difensivamente).
        /// </summary>
        private static Vector3 ResolveRadialDirection(
            Vector3 candidateFromPoi, float candidateDist,
            Vector3 currentFromPoi,
            Vector3 fallbackRadial)
        {
            if (candidateDist > DegenerateDistanceEpsilon)
            {
                return candidateFromPoi / candidateDist;
            }

            float currentDist = currentFromPoi.magnitude;
            if (currentDist > DegenerateDistanceEpsilon)
            {
                return currentFromPoi / currentDist;
            }

            // Ultimo fallback: il vettore fornito dal chiamante. Normalizziamo
            // difensivamente per tolleranza (l'invariante è che sia unitario).
            float fbMag = fallbackRadial.magnitude;
            if (fbMag > DegenerateDistanceEpsilon)
            {
                return fallbackRadial / fbMag;
            }

            // Nessuna direzione utile: ritorna up mondo — meglio che NaN.
            return Vector3.up;
        }
    }
}
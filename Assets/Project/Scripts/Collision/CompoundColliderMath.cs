using System.Collections.Generic;
using UnityEngine;

namespace SpaceSurvivor.Collision
{
    /// <summary>
    /// CompoundColliderMath — Rev AB (Blocco 3.2.d — D5), aggiornato Rev AD
    /// (D12, F-C — nave = compound anche in Docking).
    /// Classe statica pura, stateless. Zero dipendenze da NGO, singleton,
    /// Time.deltaTime. Testabile mentalmente in isolamento.
    ///
    /// RESPONSABILITÀ:
    ///   Fonte unica di verità per il pattern "clamp posizionale + slide"
    ///   quando entrambi gli oggetti (Nave, POI) sono descritti da un
    ///   COMPOUND di volumi primitivi (OBB / Sphere). Sostituisce il
    ///   PoiCollisionMath.ClampAgainstPoi (sfera-sfera, Rev AA) rimosso in
    ///   Rev AB.
    ///
    /// ALGORITMO (Rev AA workshop, Q4 = C — closest-point unificato):
    ///   1. Trasforma tutti i volumi local → world (per oggetto A e oggetto B).
    ///   2. Per ogni coppia (volA, volB) invoca il dispatcher primitivo:
    ///        OBB×OBB      → ComputeContactObbObb    (SAT per depth+axis)
    ///        OBB×Sphere   → ComputeContactObbSphere (closest-point analitico)
    ///        Sphere×OBB   → simmetrica (swap + flip normal)
    ///        Sphere×Sphere→ ComputeContactSphSph    (distanza centri)
    ///      Ogni dispatcher ritorna PairContact { depth, normalOutwardFromA }
    ///      con depth &gt; 0 = compenetrazione.
    ///   3. Vincitore = coppia con depth MASSIMA (coppia più critica).
    ///   4. Se vincitore.depth &gt; 0 → clamp+slide sull'asse
    ///      vincitore.normalOutwardFromA:
    ///        - ClampedPosition = candidatePosA + normal * depth
    ///        - ClampedVelocity = velocity - normal * min(0, dot(velocity, -normal))
    ///          (azzera radiale verso B, preserva tangenziale)
    ///   5. Il chiamante usa il risultato: ShipMovement scrive LogicalPosition,
    ///      PropulsionSystem riceve la nuova velocità scalare via Dot(vel, forward).
    ///
    /// SEMANTICA NORMAL:
    ///   normalOutwardFromA punta DA B VERSO A (spinge A fuori da B). Push-out
    ///   applicato: A si sposta di normal * depth. Coerente col pattern Rev AA
    ///   (radialDir outward = ship→esterno POI).
    ///
    /// LISTA VUOTA volumesA (guard, dead code post-Rev AD):
    ///   Se volumesA è null o vuota, il dispatcher tratta A come un PUNTO
    ///   (posizione = candidatePosA). Fino a Rev AC questa era l'invariante
    ///   Docking ("nave = punto contro volumi POI"). Rev AD (F-C) rimuove
    ///   quell'invariante: ora TUTTI i consumer passano ShipVolumes non-null.
    ///   Il branch aIsPoint resta come GUARD PROTETTIVO — se un futuro
    ///   consumer passa lista vuota per errore o intenzione, il math helper
    ///   degrada elegantemente a Point×* invece di NullReferenceException.
    ///   NB: point-vs-OBB ha singolarità nota (nessun primo contatto — un
    ///   punto ha misura zero), quindi la geometria del math non è
    ///   affidabile in quel regime. È un guard, non un caso d'uso.
    ///
    /// NB — semantica di RadialImpactSpeed:
    ///   È la MAGNITUDINE della componente della velocità di A verso B al
    ///   momento del contatto (sempre &gt;= 0). Consumer previsto:
    ///   OnHardCollision(radialImpactSpeed, poi) → ShipImpactHandler.
    ///
    /// FUNZIONE AUSILIARIA (Rev AD, F-C, QB=B2):
    ///   ComputeCompoundExtentAlongAxis(volumes, rotation, worldAxis) ritorna
    ///   la max projection del compound lungo un asse world. Usata dal
    ///   DockingController per calcolare quanto la nave sporge verso il POI
    ///   lungo l'asse di approccio, così da aggiustare automaticamente il
    ///   target di conferma anchor senza ricalibrare i prefab POI.
    /// </summary>
    public static class CompoundColliderMath
    {
        /// <summary>
        /// Soglia numerica sotto cui una magnitudine è considerata degenere.
        /// </summary>
        public const float DegenerateEpsilon = 1e-4f;

        /// <summary>
        /// Risultato di una singola coppia di primitivi (post-dispatcher).
        /// depth &lt;= 0 = nessuna compenetrazione (normalOutwardFromA non valido).
        /// depth &gt; 0 = compenetrazione, push-out = normalOutwardFromA * depth.
        /// </summary>
        public struct PairContact
        {
            /// <summary>Profondità di compenetrazione (unità logiche). &gt; 0 = compenetrazione.</summary>
            public float Depth;

            /// <summary>Direzione outward: da B verso A, unitaria. Valida solo se Depth &gt; 0.</summary>
            public Vector3 NormalOutwardFromA;
        }

        /// <summary>
        /// Risultato del clamp compound. Struct value-type (zero alloc heap).
        /// </summary>
        public struct ClampResult
        {
            /// <summary>Posizione applicata a A (clampata o originale).</summary>
            public Vector3 ClampedPosition;

            /// <summary>Velocità applicata a A (radiale azzerata se collisione).</summary>
            public Vector3 ClampedVelocity;

            /// <summary>true se è avvenuta collisione (una coppia con Depth &gt; 0).</summary>
            public bool HadCollision;

            /// <summary>Magnitudine componente radiale di velocity verso B al contatto (&gt;= 0).</summary>
            public float RadialImpactSpeed;

            /// <summary>Direzione outward (B→A) usata per il clamp. Vector3.zero se HadCollision == false.</summary>
            public Vector3 RadialDirOutward;
        }

        // =====================================================================
        // API TOP-LEVEL — CLAMP AGAINST COMPOUND
        // =====================================================================

        /// <summary>
        /// Applica il pattern clamp+slide di A contro il compound di B.
        ///
        /// PARAMETRI:
        ///   currentPosA       — posizione di A prima dell'integrazione (u logiche).
        ///   candidatePosA     — posizione candidata di A dopo integrazione
        ///                       (currentPosA + velocity*dt).
        ///   rotationA         — rotation logica di A (usata per trasformare
        ///                       volumesA local→world).
        ///   volumesA          — lista volumi di A in LOCAL space. Se null o
        ///                       vuota, A è trattato come PUNTO (GUARD
        ///                       post-Rev AD: nessun consumer usa più questa
        ///                       modalità intenzionalmente; degrada elegantemente
        ///                       per evitare NullRef in caso di uso improprio).
        ///   worldPosB         — posizione logica di B (u logiche).
        ///   worldRotB         — rotation logica di B.
        ///   volumesB          — lista volumi di B in LOCAL space. Se null o
        ///                       vuota → nessuna coppia da testare, HadCollision
        ///                       = false.
        ///   velocity          — velocità corrente di A (u/s logiche).
        ///   useHardClamp      — se false, il clamp non viene applicato ma
        ///                       HadCollision + RadialImpactSpeed vengono
        ///                       comunque emessi (per notifiche o debug).
        ///   fallbackNormal    — direzione outward di fallback se il dispatcher
        ///                       non riesce a determinarne una (edge case
        ///                       degenere: centri coincidenti). Passare una
        ///                       direzione unitaria sensata nel contesto
        ///                       (es. asse di approccio in Docking, Vector3.up
        ///                       nel resolver Manual).
        ///
        /// RITORNO: ClampResult con posizione/velocità post-clamp + flag.
        /// </summary>
        public static ClampResult ClampAgainstCompound(
            Vector3 currentPosA,
            Vector3 candidatePosA,
            Quaternion rotationA,
            IReadOnlyList<CompoundVolume> volumesA,
            Vector3 worldPosB,
            Quaternion worldRotB,
            IReadOnlyList<CompoundVolume> volumesB,
            Vector3 velocity,
            bool useHardClamp,
            Vector3 fallbackNormal)
        {
            ClampResult result = new ClampResult
            {
                ClampedPosition = candidatePosA,
                ClampedVelocity = velocity,
                HadCollision = false,
                RadialImpactSpeed = 0f,
                RadialDirOutward = Vector3.zero,
            };

            if (volumesB == null || volumesB.Count == 0) return result;

            // Trova la coppia con depth massima.
            PairContact maxPair = ComputeMaxPenetration(
                candidatePosA, rotationA, volumesA,
                worldPosB, worldRotB, volumesB,
                fallbackNormal);

            if (maxPair.Depth <= 0f)
            {
                // Nessuna compenetrazione: passa attraverso.
                return result;
            }

            // ── Compenetrazione rilevata ──────────────────────────────────
            Vector3 normalOut = maxPair.NormalOutwardFromA;

            // Robustness: se il dispatcher ha ritornato normal degenere,
            // usa fallback (già normalizzato dal chiamante o normalizzato qui).
            if (normalOut.sqrMagnitude < DegenerateEpsilon * DegenerateEpsilon)
            {
                float fbMag = fallbackNormal.magnitude;
                normalOut = fbMag > DegenerateEpsilon ? fallbackNormal / fbMag : Vector3.up;
            }

            // Radiale della velocità verso B: dot(velocity, -normal).
            // Se positivo, A stava andando dentro B (radial < 0 outward).
            float radialInward = -Vector3.Dot(velocity, normalOut);

            // useHardClamp = false: non tocco posizione, ma emetto HadCollision
            // se velocity puntava verso B (comportamento simmetrico a
            // PoiCollisionMath Rev AA).
            if (!useHardClamp)
            {
                if (radialInward > 0f)
                {
                    result.HadCollision = true;
                    result.RadialImpactSpeed = radialInward;
                    result.RadialDirOutward = normalOut;
                }
                return result;
            }

            // ── Clamp posizionale hard ────────────────────────────────────
            result.ClampedPosition = candidatePosA + normalOut * maxPair.Depth;
            result.RadialDirOutward = normalOut;

            if (radialInward > 0f)
            {
                // Azzera componente radiale (verso B), preserva tangenziale.
                // v_tangent = v - dot(v, normalOut) * normalOut se dot < 0
                //           = v + radialInward * normalOut
                result.ClampedVelocity = velocity + normalOut * radialInward;
                result.HadCollision = true;
                result.RadialImpactSpeed = radialInward;
            }
            // else: A si stava già allontanando da B (dot(v, normalOut) >= 0),
            // il clamp posizionale è stato applicato ma la velocità è
            // già "in uscita" — non toccare.

            return result;
        }

        // =====================================================================
        // API — MAX PENETRATION (usata dal resolver per selezione POI vincitore)
        // =====================================================================

        /// <summary>
        /// Calcola la coppia di volumi con depth di compenetrazione massima
        /// tra il compound di A e quello di B. Ritorna PairContact con
        /// Depth &lt;= 0 se nessuna coppia compenetra.
        ///
        /// Usato da:
        ///   - ClampAgainstCompound internamente
        ///   - PoiCollisionResolver per selezionare quale POI ha la
        ///     compenetrazione più critica quando più POI sforano
        ///     contemporaneamente.
        /// </summary>
        public static PairContact ComputeMaxPenetration(
            Vector3 worldPosA,
            Quaternion worldRotA,
            IReadOnlyList<CompoundVolume> volumesA,
            Vector3 worldPosB,
            Quaternion worldRotB,
            IReadOnlyList<CompoundVolume> volumesB,
            Vector3 fallbackNormal)
        {
            PairContact best = new PairContact
            {
                Depth = 0f,
                NormalOutwardFromA = Vector3.zero,
            };

            if (volumesB == null || volumesB.Count == 0) return best;

            // Se volumesA è vuota → A è un PUNTO (candidatePosA). Uso una
            // "lista virtuale" di 1 elemento: Sphere di raggio 0 al centro.
            // GUARD post-Rev AD (F-C): nessun consumer usa più questa modalità
            // intenzionalmente (DockingController ora passa ShipVolumes come
            // Manual/Autopilot). Il branch resta per degradazione elegante:
            // se un futuro chiamante passa lista vuota per errore, si
            // ottiene Point×* invece di NullReferenceException. Ma NB:
            // Point×OBB ha singolarità geometrica nota (un punto non ha
            // primo contatto con la superficie di un OBB) → il risultato
            // può essere inaffidabile in movimento veloce.
            bool aIsPoint = (volumesA == null || volumesA.Count == 0);

            int countA = aIsPoint ? 1 : volumesA.Count;

            for (int i = 0; i < countA; i++)
            {
                // Volume A in world space (o punto se aIsPoint).
                CompoundVolumeType typeA;
                Vector3 centerA;
                Quaternion rotA;
                Vector3 halfA;
                float radA;

                if (aIsPoint)
                {
                    typeA = CompoundVolumeType.Sphere;
                    centerA = worldPosA;
                    rotA = Quaternion.identity;
                    halfA = Vector3.zero;
                    radA = 0f;
                }
                else
                {
                    CompoundVolume vA = volumesA[i];
                    typeA = vA.type;
                    centerA = worldPosA + worldRotA * vA.localPosition;
                    rotA = worldRotA * Quaternion.Euler(vA.localEulerAngles);
                    halfA = vA.HalfExtents;
                    radA = vA.Radius;
                }

                for (int j = 0; j < volumesB.Count; j++)
                {
                    CompoundVolume vB = volumesB[j];
                    Vector3 centerB = worldPosB + worldRotB * vB.localPosition;
                    Quaternion rotB = worldRotB * Quaternion.Euler(vB.localEulerAngles);
                    Vector3 halfB = vB.HalfExtents;
                    float radB = vB.Radius;

                    PairContact pair = DispatchPair(
                        typeA, centerA, rotA, halfA, radA,
                        vB.type, centerB, rotB, halfB, radB,
                        fallbackNormal);

                    if (pair.Depth > best.Depth)
                    {
                        best = pair;
                    }
                }
            }

            return best;
        }

        // =====================================================================
        // API — COMPOUND EXTENT ALONG AXIS (Rev AD, F-C, QB=B2)
        // =====================================================================

        /// <summary>
        /// Calcola la MASSIMA proiezione dei volumi del compound lungo un
        /// asse world (unitario). Usata per determinare quanto un compound
        /// sporge in una data direzione, senza dover computare la bounding
        /// box completa.
        ///
        /// CASO D'USO PRINCIPALE (Rev AD, F-C):
        ///   Il DockingController deve sapere quanto la nave sporge lungo
        ///   l'asse di approccio (-approachAxisWorld) per aggiustare il
        ///   target di conferma anchor. Con nave = compound multi-volume,
        ///   il "bordo nave verso il POI" non è più il centro logico ma
        ///   il centro + extent lungo l'asse.
        ///
        /// PARAMETRI:
        ///   volumes           — lista volumi in LOCAL space (rispetto a
        ///                       LogicalPosition + rotation del compound).
        ///                       Se null o vuota → ritorna 0 (compound = punto).
        ///   rotation          — rotation logica del compound (per trasformare
        ///                       volumi local → world).
        ///   worldAxis         — asse world (unitario) su cui proiettare.
        ///                       Se magnitude &lt; DegenerateEpsilon, ritorna 0.
        ///
        /// RITORNO:
        ///   Max, su tutti i volumi, di:
        ///     dot(volumeCenterOffsetWorld, worldAxis) + volumeExtentAlongAxis
        ///   dove volumeExtentAlongAxis è:
        ///     - per Sphere: radius
        ///     - per OBB:    somma_k |halfExt[k] * dot(axisLocal[k]_world, worldAxis)|
        ///
        /// NB: Il valore ritornato è SEMPRE &gt;= 0 (max projection). Per
        /// ottenere l'extent nella direzione OPPOSTA, passare -worldAxis.
        /// Per ottenere l'extent bidirezionale (bounding), sommare i due.
        /// </summary>
        public static float ComputeCompoundExtentAlongAxis(
            IReadOnlyList<CompoundVolume> volumes,
            Quaternion rotation,
            Vector3 worldAxis)
        {
            if (volumes == null || volumes.Count == 0) return 0f;

            // Normalizza worldAxis se necessario. Guard contro asse degenere.
            float axisMagSq = worldAxis.sqrMagnitude;
            if (axisMagSq < DegenerateEpsilon * DegenerateEpsilon) return 0f;
            Vector3 axisN = axisMagSq > 1f + DegenerateEpsilon || axisMagSq < 1f - DegenerateEpsilon
                ? worldAxis / Mathf.Sqrt(axisMagSq)
                : worldAxis;

            float maxProjection = 0f;

            for (int i = 0; i < volumes.Count; i++)
            {
                CompoundVolume v = volumes[i];

                // Centro volume in world (rispetto al centro logico del compound,
                // che è l'origine del sistema local).
                Vector3 centerOffsetWorld = rotation * v.localPosition;
                float centerProj = Vector3.Dot(centerOffsetWorld, axisN);

                float extent;
                if (v.type == CompoundVolumeType.Sphere)
                {
                    extent = v.Radius;
                }
                else
                {
                    // OBB: proiezione della half-diagonal lungo axisN.
                    // Gli assi locali dell'OBB sono le colonne di
                    // (rotation * Quaternion.Euler(v.localEulerAngles)).
                    Quaternion volRotWorld = rotation * Quaternion.Euler(v.localEulerAngles);
                    Vector3 axLocX = volRotWorld * Vector3.right;
                    Vector3 axLocY = volRotWorld * Vector3.up;
                    Vector3 axLocZ = volRotWorld * Vector3.forward;
                    Vector3 h = v.HalfExtents;

                    extent = Mathf.Abs(h.x * Vector3.Dot(axLocX, axisN))
                           + Mathf.Abs(h.y * Vector3.Dot(axLocY, axisN))
                           + Mathf.Abs(h.z * Vector3.Dot(axLocZ, axisN));
                }

                float projection = centerProj + extent;
                if (projection > maxProjection) maxProjection = projection;
            }

            return maxProjection;
        }

        // =====================================================================
        // DISPATCHER — sceglie il primitivo per la coppia
        // =====================================================================

        private static PairContact DispatchPair(
            CompoundVolumeType typeA, Vector3 centerA, Quaternion rotA, Vector3 halfA, float radA,
            CompoundVolumeType typeB, Vector3 centerB, Quaternion rotB, Vector3 halfB, float radB,
            Vector3 fallbackNormal)
        {
            // Sphere × Sphere
            if (typeA == CompoundVolumeType.Sphere && typeB == CompoundVolumeType.Sphere)
            {
                return ContactSphSph(centerA, radA, centerB, radB, fallbackNormal);
            }

            // OBB × Sphere
            if (typeA == CompoundVolumeType.OBB && typeB == CompoundVolumeType.Sphere)
            {
                return ContactObbSphere(centerA, rotA, halfA, centerB, radB, invertNormal: false, fallbackNormal);
            }

            // Sphere × OBB → simmetrico (invert normal per riportare a "da B verso A")
            if (typeA == CompoundVolumeType.Sphere && typeB == CompoundVolumeType.OBB)
            {
                // ContactObbSphere calcola normal da OBB → Sphere. Qui l'OBB è B,
                // la Sphere è A, quindi normal_from_B = ContactObbSphere(B, A).normal.
                // Ma il metodo restituisce normal outward dal primo argomento (OBB),
                // quindi va invertito per ottenere B→A.
                return ContactObbSphere(centerB, rotB, halfB, centerA, radA, invertNormal: true, fallbackNormal);
            }

            // OBB × OBB
            return ContactObbObb(centerA, rotA, halfA, centerB, rotB, halfB, fallbackNormal);
        }

        // =====================================================================
        // PRIMITIVE — Sphere vs Sphere
        // =====================================================================

        /// <summary>
        /// Closest-point Sphere vs Sphere: caso analitico.
        /// distanza tra centri &lt; radA + radB → compenetrazione.
        /// normal outward da B verso A = (centerA - centerB).normalized.
        /// depth = (radA + radB) - distance.
        /// </summary>
        private static PairContact ContactSphSph(
            Vector3 centerA, float radA,
            Vector3 centerB, float radB,
            Vector3 fallbackNormal)
        {
            Vector3 delta = centerA - centerB;
            float dist = delta.magnitude;
            float sumR = radA + radB;

            if (dist >= sumR)
            {
                return new PairContact { Depth = 0f, NormalOutwardFromA = Vector3.zero };
            }

            Vector3 normal;
            if (dist > DegenerateEpsilon)
            {
                normal = delta / dist;
            }
            else
            {
                float fbMag = fallbackNormal.magnitude;
                normal = fbMag > DegenerateEpsilon ? fallbackNormal / fbMag : Vector3.up;
            }

            return new PairContact
            {
                Depth = sumR - dist,
                NormalOutwardFromA = normal,
            };
        }

        // =====================================================================
        // PRIMITIVE — OBB vs Sphere
        // =====================================================================

        /// <summary>
        /// Closest-point OBB vs Sphere: caso analitico stabile.
        ///
        /// 1. Trasforma centro sfera nel LOCAL frame dell'OBB.
        /// 2. Closest point sull'OBB = clamp del centro sfera locale in
        ///    [-halfExtents, +halfExtents] su ogni asse.
        /// 3. Trasforma closest point back in world.
        /// 4. delta = centerSphere - closestPointWorld. Se |delta| &lt; radius
        ///    → compenetrazione. Normal outward = delta.normalized (da OBB
        ///    verso Sphere), depth = radius - |delta|.
        /// 5. Edge case: se centro sfera è DENTRO l'OBB (tutti gli assi già
        ///    dentro halfExtents), delta è ~zero. In quel caso: normal =
        ///    asse col minor "margine" dentro l'OBB (uscita più veloce);
        ///    depth = margine + radius.
        ///
        /// PARAMETRO invertNormal:
        ///   Se false, normal è outward dall'OBB (OBB→Sphere).
        ///   Se true, normal è outward dalla Sphere (Sphere→OBB), usato per
        ///   la simmetria Sphere-OBB dove l'ordine A-B è invertito.
        /// </summary>
        private static PairContact ContactObbSphere(
            Vector3 obbCenter, Quaternion obbRot, Vector3 obbHalfExt,
            Vector3 sphCenter, float sphRadius,
            bool invertNormal,
            Vector3 fallbackNormal)
        {
            // Trasforma centro sfera in local space dell'OBB.
            Quaternion obbRotInv = Quaternion.Inverse(obbRot);
            Vector3 sphLocal = obbRotInv * (sphCenter - obbCenter);

            // Closest point sull'OBB in local (clamp componente per componente).
            Vector3 closestLocal = new Vector3(
                Mathf.Clamp(sphLocal.x, -obbHalfExt.x, obbHalfExt.x),
                Mathf.Clamp(sphLocal.y, -obbHalfExt.y, obbHalfExt.y),
                Mathf.Clamp(sphLocal.z, -obbHalfExt.z, obbHalfExt.z));

            Vector3 deltaLocal = sphLocal - closestLocal;
            float distSq = deltaLocal.sqrMagnitude;

            // Caso normale: sfera fuori dall'OBB, con eventuale compenetrazione.
            if (distSq > DegenerateEpsilon * DegenerateEpsilon)
            {
                float dist = Mathf.Sqrt(distSq);
                if (dist >= sphRadius)
                {
                    return new PairContact { Depth = 0f, NormalOutwardFromA = Vector3.zero };
                }

                // Normal in world, outward da OBB verso Sphere.
                Vector3 normalLocal = deltaLocal / dist;
                Vector3 normalWorld = obbRot * normalLocal;

                return new PairContact
                {
                    Depth = sphRadius - dist,
                    NormalOutwardFromA = invertNormal ? -normalWorld : normalWorld,
                };
            }

            // Edge case: centro sfera dentro l'OBB. Cerco l'asse col margine
            // minimo → uscita più veloce.
            float mx = obbHalfExt.x - Mathf.Abs(sphLocal.x);
            float my = obbHalfExt.y - Mathf.Abs(sphLocal.y);
            float mz = obbHalfExt.z - Mathf.Abs(sphLocal.z);

            Vector3 normalLocalOut;
            float minMargin;

            if (mx <= my && mx <= mz)
            {
                normalLocalOut = new Vector3(Mathf.Sign(sphLocal.x), 0f, 0f);
                if (normalLocalOut.x == 0f) normalLocalOut.x = 1f;
                minMargin = mx;
            }
            else if (my <= mz)
            {
                normalLocalOut = new Vector3(0f, Mathf.Sign(sphLocal.y), 0f);
                if (normalLocalOut.y == 0f) normalLocalOut.y = 1f;
                minMargin = my;
            }
            else
            {
                normalLocalOut = new Vector3(0f, 0f, Mathf.Sign(sphLocal.z));
                if (normalLocalOut.z == 0f) normalLocalOut.z = 1f;
                minMargin = mz;
            }

            Vector3 normalWorldInside = obbRot * normalLocalOut;
            float depthInside = minMargin + sphRadius;

            return new PairContact
            {
                Depth = depthInside,
                NormalOutwardFromA = invertNormal ? -normalWorldInside : normalWorldInside,
            };
        }

        // =====================================================================
        // PRIMITIVE — OBB vs OBB (SAT — Separating Axis Theorem)
        // =====================================================================

        /// <summary>
        /// OBB vs OBB via SAT (Separating Axis Theorem).
        ///
        /// 15 assi candidati:
        ///   - 3 face normals di A (assi locali di A)
        ///   - 3 face normals di B (assi locali di B)
        ///   - 9 cross products (edge A × edge B)
        ///
        /// Per ogni asse candidato:
        ///   - Proietta entrambi gli OBB sull'asse (intervallo [minA, maxA],
        ///     [minB, maxB]).
        ///   - Se gli intervalli NON si sovrappongono → asse separatore trovato,
        ///     nessuna compenetrazione. Early exit.
        ///   - Altrimenti calcola overlap. L'asse con overlap MINIMO è l'asse
        ///     di push-out (MTV — Minimum Translation Vector).
        ///
        /// Direzione MTV: verifica il segno tramite dot(centerA - centerB, axis).
        /// Se positivo → axis punta da B verso A (già outward). Altrimenti,
        /// invertire.
        ///
        /// Robustness:
        ///   - Cross product con vettori paralleli → axis ≈ zero. Skip
        ///     (asse degenerato, ridondante rispetto ai face normals).
        ///   - Se tutti i 15 assi indicano overlap, il minimo overlap tra i
        ///     face normals è la scelta più stabile (edge-cross axis può dare
        ///     minimo spurio in geometrie quasi allineate). Preferisco face
        ///     normals in caso di parità entro un epsilon.
        /// </summary>
        private static PairContact ContactObbObb(
            Vector3 centerA, Quaternion rotA, Vector3 halfA,
            Vector3 centerB, Quaternion rotB, Vector3 halfB,
            Vector3 fallbackNormal)
        {
            // Assi locali di A e B in world space (colonne delle matrici di rotazione).
            Vector3[] axesA = new Vector3[3];
            axesA[0] = rotA * Vector3.right;
            axesA[1] = rotA * Vector3.up;
            axesA[2] = rotA * Vector3.forward;

            Vector3[] axesB = new Vector3[3];
            axesB[0] = rotB * Vector3.right;
            axesB[1] = rotB * Vector3.up;
            axesB[2] = rotB * Vector3.forward;

            float[] halfArr_A = new float[3] { halfA.x, halfA.y, halfA.z };
            float[] halfArr_B = new float[3] { halfB.x, halfB.y, halfB.z };

            Vector3 t = centerA - centerB;

            float minOverlap = float.MaxValue;
            Vector3 minAxis = Vector3.zero;
            bool minIsFaceAxis = false;

            // Face axes di A (indice 0-2)
            for (int i = 0; i < 3; i++)
            {
                if (!TestAxis(axesA[i], t, axesA, axesB, halfArr_A, halfArr_B, out float overlap))
                    return NoContact();
                if (overlap < minOverlap - DegenerateEpsilon)
                {
                    minOverlap = overlap;
                    minAxis = axesA[i];
                    minIsFaceAxis = true;
                }
            }

            // Face axes di B (indice 3-5)
            for (int i = 0; i < 3; i++)
            {
                if (!TestAxis(axesB[i], t, axesA, axesB, halfArr_A, halfArr_B, out float overlap))
                    return NoContact();
                if (overlap < minOverlap - DegenerateEpsilon)
                {
                    minOverlap = overlap;
                    minAxis = axesB[i];
                    minIsFaceAxis = true;
                }
            }

            // Edge cross products (indice 6-14)
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Vector3 axis = Vector3.Cross(axesA[i], axesB[j]);
                    float axisMagSq = axis.sqrMagnitude;
                    if (axisMagSq < DegenerateEpsilon * DegenerateEpsilon) continue; // paralleli, skip
                    axis = axis / Mathf.Sqrt(axisMagSq);

                    if (!TestAxis(axis, t, axesA, axesB, halfArr_A, halfArr_B, out float overlap))
                        return NoContact();

                    // Preferisco face axes a parità (più stabili numericamente):
                    // richiedo overlap strettamente minore di più di un epsilon.
                    if (overlap < minOverlap - DegenerateEpsilon && !minIsFaceAxis)
                    {
                        minOverlap = overlap;
                        minAxis = axis;
                        minIsFaceAxis = false;
                    }
                    else if (overlap < minOverlap - DegenerateEpsilon)
                    {
                        // Nuovo minimo più netto di un face axis? Solo se
                        // significativamente migliore. Altrimenti mantengo il face.
                        // (Nella pratica: se cross-axis batte face di > epsilon,
                        //  la geometria è genuinamente edge-on, accetto.)
                        minOverlap = overlap;
                        minAxis = axis;
                        minIsFaceAxis = false;
                    }
                }
            }

            // Segno dell'asse: deve puntare da B verso A.
            if (Vector3.Dot(minAxis, t) < 0f) minAxis = -minAxis;

            // Robustness: se minAxis è degenere (edge case numerico), fallback.
            if (minAxis.sqrMagnitude < DegenerateEpsilon * DegenerateEpsilon)
            {
                float fbMag = fallbackNormal.magnitude;
                minAxis = fbMag > DegenerateEpsilon ? fallbackNormal / fbMag : Vector3.up;
            }

            return new PairContact
            {
                Depth = minOverlap,
                NormalOutwardFromA = minAxis,
            };
        }

        /// <summary>
        /// Testa un asse SAT: proietta entrambi gli OBB sull'asse, verifica
        /// sovrapposizione. Se separatori, ritorna false (nessuna
        /// compenetrazione) e overlap è indefinito. Se sovrapposti, ritorna
        /// true e overlap è la profondità di sovrapposizione (&gt; 0).
        /// </summary>
        private static bool TestAxis(
            Vector3 axis, Vector3 t,
            Vector3[] axesA, Vector3[] axesB,
            float[] halfA, float[] halfB,
            out float overlap)
        {
            // Proiezione OBB su asse = somma dei contributi dei suoi 3 assi:
            //   rA = |halfA.x * dot(axisA.x, axis)| + |halfA.y * dot(axisA.y, axis)| + ...
            float rA = 0f;
            float rB = 0f;
            for (int k = 0; k < 3; k++)
            {
                rA += halfA[k] * Mathf.Abs(Vector3.Dot(axesA[k], axis));
                rB += halfB[k] * Mathf.Abs(Vector3.Dot(axesB[k], axis));
            }

            float distProj = Mathf.Abs(Vector3.Dot(t, axis));

            // Sovrapposizione se distanza proiettata < somma dei raggi proiettati.
            if (distProj > rA + rB)
            {
                overlap = 0f;
                return false; // asse separatore trovato
            }

            overlap = (rA + rB) - distProj;
            return true;
        }

        private static PairContact NoContact()
        {
            return new PairContact { Depth = 0f, NormalOutwardFromA = Vector3.zero };
        }
    }
}
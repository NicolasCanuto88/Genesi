using UnityEngine;

/// <summary>
/// Stato di un volo: il tratto corrente (dal lancio o dall'ultimo rimbalzo) è una parabola
/// esatta, PositionAt(SegmentOrigin, SegmentVelocity, gravità, SegmentTime).
/// </summary>
public struct ThrowFlightState
{
    public Vector3 SegmentOrigin;     // inizio del tratto corrente (lancio o ultimo rimbalzo)
    public Vector3 SegmentVelocity;   // velocità all'inizio del tratto
    public float SegmentTime;         // secondi trascorsi nel tratto corrente
    public float TotalTime;           // secondi di volo dall'inizio
    public int Bounces;               // rimbalzi già fatti

    public Vector3 Position(float gravity)
        => ThrowBallistics.PositionAt(SegmentOrigin, SegmentVelocity, gravity, SegmentTime);

    public Vector3 Velocity(float gravity)
        => ThrowBallistics.VelocityAt(SegmentVelocity, gravity, SegmentTime);
}

/// <summary>Esito di un passo di volo.</summary>
public enum ThrowStepResult : byte
{
    Flying = 0,    // nessun contatto, volo non scaduto
    Contact = 1,   // la sfera ha toccato un collider (ThrowContact valido)
    Expired = 2    // durata massima raggiunta in aria
}

/// <summary>Contatto della sfera con un collider.</summary>
public struct ThrowContact
{
    public Vector3 Center;     // centro della sfera nel momento del contatto
    public Vector3 Normal;     // normale della superficie colpita
    public Vector3 Velocity;   // velocità nel momento del contatto
    public Collider Collider;  // collider colpito (mai uno di chi lancia)
}

/// <summary>
/// ThrowBallistics — matematica del volo dei lanciabili (Rev BS-a · Q52-a · Q54-a · Q62-a).
/// Funzioni pure, senza stato: le usano tutti e tre gli attori con lo STESSO algoritmo.
///   - Server (ThrowableSystem): è l'autorità. Avanza ogni volo a ogni frame e decide
///     contatti, rimbalzi e detonazione.
///   - Client (ThrowableSystem): anima la copia visiva sulla stessa parabola e si ferma al
///     contatto in attesa dell'evento del server (la copia non attraversa le pareti).
///   - Chi mira (PlayerThrower): simula l'intero volo per disegnare arco e punto d'arrivo.
///
/// SPAZIO (Q53-a): riferimento della nave, cioè lo spazio Unity (la nave non si muove mai).
/// Gravità sempre verso −Y della scena. Vale dentro la nave, ad attracco avvenuto (velocità
/// forzata a 0 in Docking/Docked) e in EVA vicino allo scafo.
///
/// COLLISIONI: SphereCast a corde di al più MaxChordLength metri lungo la parabola, contro la
/// maschera dello SO, trigger sempre ignorati. Contano pareti, porte, console e i CORPI dei
/// giocatori (oggi il CharacterController; domani i collider delle mesh vere, senza modifiche
/// qui). I collider figli di ignoreRoot (chi lancia, Q62-a) non contano mai. I collider già
/// sovrapposti alla sfera all'inizio di una corda sono ignorati (distanza 0): servono per
/// ripartire puliti da un rimbalzo.
/// </summary>
public static class ThrowBallistics
{
    /// <summary>Lunghezza massima, in metri, di una corda controllata con un singolo sweep.</summary>
    public const float MaxChordLength = 0.5f;

    /// <summary>Distacco, in metri, dalla superficie dopo un rimbalzo.</summary>
    public const float BounceSeparation = 0.01f;

    /// <summary>Corde massime per passo: limita il costo di un dt anomalo (hitch).</summary>
    private const int MaxChordsPerStep = 64;

    public static Vector3 GravityVector(float gravity) => new Vector3(0f, -gravity, 0f);

    public static Vector3 PositionAt(Vector3 origin, Vector3 velocity, float gravity, float t)
        => origin + velocity * t + GravityVector(gravity) * (0.5f * t * t);

    public static Vector3 VelocityAt(Vector3 velocity, float gravity, float t)
        => velocity + GravityVector(gravity) * t;

    /// <summary>
    /// Direzione di lancio: lo sguardo alzato di pitchOffsetDegrees (alzo finale tra −89° e +89°).
    /// Con lo sguardo verticale la direzione orizzontale non esiste: si lancia lungo lo sguardo.
    /// </summary>
    public static Vector3 LaunchDirection(Vector3 lookDirection, float pitchOffsetDegrees)
    {
        if (lookDirection.sqrMagnitude < 1e-8f) return Vector3.forward;

        Vector3 dir = lookDirection.normalized;
        Vector3 horizontal = new Vector3(dir.x, 0f, dir.z);
        if (horizontal.sqrMagnitude < 1e-6f) return dir;
        horizontal.Normalize();

        float elevation = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
        float raised = Mathf.Clamp(elevation + pitchOffsetDegrees, -89f, 89f) * Mathf.Deg2Rad;
        return horizontal * Mathf.Cos(raised) + Vector3.up * Mathf.Sin(raised);
    }

    /// <summary>Stato iniziale di un volo.</summary>
    public static ThrowFlightState Begin(Vector3 origin, Vector3 velocity)
    {
        return new ThrowFlightState
        {
            SegmentOrigin = origin,
            SegmentVelocity = velocity,
            SegmentTime = 0f,
            TotalTime = 0f,
            Bounces = 0
        };
    }

    /// <summary>
    /// Avanza il volo di dt secondi. Ritorna Contact al primo collider toccato (lo stato si
    /// ferma al contatto), Expired se il volo raggiunge maxFlightSeconds in aria, altrimenti
    /// Flying. Il tempo dopo un contatto non viene consumato.
    /// </summary>
    public static ThrowStepResult Advance(ref ThrowFlightState state, float dt, float gravity, float radius,
                                          float maxFlightSeconds, int mask, Transform ignoreRoot,
                                          RaycastHit[] buffer, out ThrowContact contact)
    {
        contact = default;

        float remaining = maxFlightSeconds - state.TotalTime;
        if (remaining <= 0f) return ThrowStepResult.Expired;
        if (dt <= 0f) return ThrowStepResult.Flying;

        bool expires = dt >= remaining;
        if (expires) dt = remaining;

        float t0 = state.SegmentTime;
        Vector3 from = PositionAt(state.SegmentOrigin, state.SegmentVelocity, gravity, t0);
        Vector3 to = PositionAt(state.SegmentOrigin, state.SegmentVelocity, gravity, t0 + dt);

        int chords = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(from, to) / MaxChordLength), 1, MaxChordsPerStep);
        Vector3 a = from;
        for (int i = 1; i <= chords; i++)
        {
            float ta = t0 + dt * (i - 1) / chords;
            float tb = t0 + dt * i / chords;
            Vector3 b = PositionAt(state.SegmentOrigin, state.SegmentVelocity, gravity, tb);

            if (Sweep(a, b, radius, mask, ignoreRoot, buffer, out RaycastHit hit, out float fraction))
            {
                float tHit = ta + (tb - ta) * fraction;
                contact.Center = a + (b - a) * fraction;
                contact.Normal = hit.normal;
                contact.Velocity = VelocityAt(state.SegmentVelocity, gravity, tHit);
                contact.Collider = hit.collider;

                state.TotalTime += tHit - t0;
                state.SegmentTime = tHit;
                return ThrowStepResult.Contact;
            }
            a = b;
        }

        state.SegmentTime = t0 + dt;
        state.TotalTime += dt;
        return expires ? ThrowStepResult.Expired : ThrowStepResult.Flying;
    }

    /// <summary>
    /// Prova a rimbalzare sul contatto: velocità riflessa × restituzione, un nuovo tratto che
    /// parte appena staccato dalla superficie. false (stato invariato) se i rimbalzi sono finiti
    /// o la velocità riflessa è sotto minBounceSpeed: allora il contatto è una detonazione.
    /// </summary>
    public static bool TryBounce(ref ThrowFlightState state, in ThrowContact contact, int maxBounces,
                                 float restitution, float minBounceSpeed)
    {
        if (state.Bounces >= maxBounces) return false;

        Vector3 reflected = Vector3.Reflect(contact.Velocity, contact.Normal) * restitution;
        if (reflected.magnitude < minBounceSpeed) return false;

        state.SegmentOrigin = contact.Center + contact.Normal * BounceSeparation;
        state.SegmentVelocity = reflected;
        state.SegmentTime = 0f;
        state.Bounces++;
        return true;
    }

    /// <summary>
    /// Sweep di una sfera da "from" a "to". Ritorna il collider più vicino, esclusi i figli di
    /// ignoreRoot e quelli già sovrapposti alla partenza; fraction è la quota del tratto
    /// percorsa al contatto (0–1).
    /// </summary>
    public static bool Sweep(Vector3 from, Vector3 to, float radius, int mask, Transform ignoreRoot,
                             RaycastHit[] buffer, out RaycastHit hit, out float fraction)
    {
        hit = default;
        fraction = 0f;

        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance < 1e-5f || buffer == null || buffer.Length == 0) return false;

        int count = Physics.SphereCastNonAlloc(from, radius, delta / distance, buffer, distance, mask,
                                               QueryTriggerInteraction.Ignore);

        bool found = false;
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            RaycastHit candidate = buffer[i];
            Collider collider = candidate.collider;
            if (collider == null) continue;
            if (ignoreRoot != null && collider.transform.IsChildOf(ignoreRoot)) continue;   // Q62-a
            if (candidate.distance <= 0f) continue;   // già sovrapposto alla partenza
            if (candidate.distance < nearest)
            {
                nearest = candidate.distance;
                hit = candidate;
                found = true;
            }
        }

        if (found) fraction = Mathf.Clamp01(nearest / distance);
        return found;
    }

    /// <summary>
    /// Simula l'intero volo a passi di stepSeconds (arco di mira). Scrive in points l'origine e
    /// la posizione dopo ogni passo, fino a points.Length; i rimbalzi seguono le stesse regole del
    /// server. endPoint è il punto di detonazione previsto, endNormal la normale della superficie
    /// (Vector3.up se il volo scade in aria). Ritorna il numero di punti scritti.
    /// </summary>
    public static int SimulatePath(Vector3 origin, Vector3 velocity, ThrowableData data, Transform ignoreRoot,
                                   RaycastHit[] buffer, Vector3[] points, float stepSeconds,
                                   out Vector3 endPoint, out Vector3 endNormal, out bool endedOnContact)
    {
        endPoint = origin;
        endNormal = Vector3.up;
        endedOnContact = false;
        if (data == null || points == null || points.Length == 0) return 0;

        float step = Mathf.Max(0.005f, stepSeconds);
        ThrowFlightState state = Begin(origin, velocity);
        int count = 0;
        points[count++] = origin;

        while (count < points.Length)
        {
            ThrowStepResult result = Advance(ref state, step, data.Gravity, data.ProjectileRadius,
                                             data.MaxFlightSeconds, data.CollisionMask, ignoreRoot, buffer,
                                             out ThrowContact contact);
            if (result == ThrowStepResult.Contact)
            {
                points[count++] = contact.Center;
                if (TryBounce(ref state, contact, data.MaxBounces, data.Restitution, data.MinBounceSpeed))
                    continue;

                endPoint = contact.Center;
                endNormal = contact.Normal;
                endedOnContact = true;
                return count;
            }

            Vector3 position = state.Position(data.Gravity);
            points[count++] = position;
            endPoint = position;
            if (result == ThrowStepResult.Expired) return count;
        }

        return count;
    }
}

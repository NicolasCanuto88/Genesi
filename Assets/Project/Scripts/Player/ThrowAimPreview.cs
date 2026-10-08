using SpaceSurvivor.Ship;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// ThrowAimPreview — arco di mira e marcatore del punto d'arrivo di un lanciabile (Rev BV-c ·
/// workshop Quartermaster, Q104-a). Estratto da PlayerThrower (Rev BS-a · Q60-a) senza cambiarne il
/// comportamento: lo usano la bomba curativa (PlayerThrower) e la Bubble Shield
/// (PlayerQuartermasterGadgets).
///
/// COSA FA: crea a richiesta un LineRenderer figlio del giocatore e il marcatore dal prefab del
/// lanciabile, entrambi senza collider (un collider fermerebbe gli sweep), e a ogni Update simula
/// il volo con lo stesso calcolo del server (ThrowBallistics.SimulatePath): quello che si vede è
/// quello che succede, salvo chi si muove dopo il lancio.
///
/// SOLO OWNER: chi mira vede l'arco; gli altri vedono soltanto il lancio.
///
/// VISIBILITÀ: Renderer.enabled, mai SetActive (invariante dei componenti visivi).
///
/// NON È UN COMPONENTE: classe semplice posseduta da chi mira. Il proprietario chiama Show/Update
/// durante la mira, Hide alla fine, Dispose al despawn.
/// </summary>
public sealed class ThrowAimPreview
{
    private const float PreviewStepSeconds = 1f / 30f;
    private const int MaxPreviewPoints = 128;

    private readonly Transform owner;     // radice del giocatore: genitore dell'arco e corpo da ignorare
    private readonly string arcName;

    private ThrowableData data;           // lanciabile per cui sono stati costruiti arco e marcatore
    private LineRenderer arcLine;
    private GameObject aimMarker;
    private bool warnedMissingArcMaterial;
    private readonly Vector3[] previewPoints = new Vector3[MaxPreviewPoints];
    private readonly RaycastHit[] previewHits = new RaycastHit[16];

    /// <param name="owner">Radice del giocatore che mira (Transform del Player).</param>
    /// <param name="arcName">Nome del GameObject dell'arco, per riconoscerlo in Hierarchy.</param>
    public ThrowAimPreview(Transform owner, string arcName)
    {
        this.owner = owner;
        this.arcName = string.IsNullOrEmpty(arcName) ? "ThrowAimArc" : arcName;
    }

    /// <summary>
    /// Prepara arco e marcatore per il lanciabile indicato (li ricrea solo se cambia il lanciabile)
    /// e disegna subito il primo arco.
    /// </summary>
    public void Show(ThrowableData throwable, Transform cameraTransform)
    {
        if (throwable == null || cameraTransform == null) return;
        Ensure(throwable);
        Update(cameraTransform);
    }

    /// <summary>Ridisegna l'arco dalla camera di chi mira. Da chiamare a ogni frame della mira.</summary>
    public void Update(Transform cameraTransform)
    {
        if (data == null || cameraTransform == null) return;

        Vector3 origin = cameraTransform.position;
        Vector3 velocity = ThrowBallistics.LaunchDirection(cameraTransform.forward, data.AimPitchOffsetDegrees) *
                           data.LaunchSpeed;

        int count = ThrowBallistics.SimulatePath(origin, velocity, data, owner, previewHits, previewPoints,
                                                 PreviewStepSeconds, out Vector3 endPoint, out Vector3 endNormal,
                                                 out bool _);

        // Il primo punto è dentro la camera: l'arco parte dal secondo.
        int first = count > 2 ? 1 : 0;
        int visible = count - first;
        if (arcLine != null)
        {
            arcLine.positionCount = Mathf.Max(0, visible);
            for (int i = 0; i < visible; i++)
                arcLine.SetPosition(i, previewPoints[first + i]);
            arcLine.enabled = visible >= 2;
        }

        if (aimMarker != null)
        {
            aimMarker.transform.SetPositionAndRotation(endPoint + endNormal * 0.01f,
                                                       Quaternion.FromToRotation(Vector3.up, endNormal));
            SetMarkerVisible(true);
        }
    }

    /// <summary>Nasconde arco e marcatore (fine della mira, con o senza lancio).</summary>
    public void Hide()
    {
        if (arcLine != null) arcLine.enabled = false;
        SetMarkerVisible(false);
    }

    /// <summary>Distrugge arco e marcatore (despawn del giocatore).</summary>
    public void Dispose()
    {
        if (arcLine != null) Object.Destroy(arcLine.gameObject);
        if (aimMarker != null) Object.Destroy(aimMarker);
        arcLine = null;
        aimMarker = null;
        data = null;
    }

    private void Ensure(ThrowableData throwable)
    {
        if (arcLine == null)
        {
            var arcObject = new GameObject(arcName);
            arcObject.transform.SetParent(owner, false);
            arcLine = arcObject.AddComponent<LineRenderer>();
            arcLine.useWorldSpace = true;
            arcLine.positionCount = 0;
            arcLine.shadowCastingMode = ShadowCastingMode.Off;
            arcLine.receiveShadows = false;
            arcLine.numCapVertices = 2;
            arcLine.enabled = false;
        }

        if (throwable == data) return;

        // Lanciabile nuovo: materiale e spessore dell'arco, marcatore dal suo prefab.
        data = throwable;
        warnedMissingArcMaterial = false;

        arcLine.widthMultiplier = data.AimArcWidth;
        if (data.AimArcMaterial != null)
            arcLine.sharedMaterial = data.AimArcMaterial;
        else if (!warnedMissingArcMaterial)
        {
            warnedMissingArcMaterial = true;
            Debug.LogWarning($"[ThrowAimPreview] {data.name} senza Aim Arc Material: arco con il materiale di " +
                             "default.");
        }

        if (aimMarker != null) Object.Destroy(aimMarker);
        aimMarker = null;
        if (data.AimMarkerPrefab != null)
        {
            aimMarker = Object.Instantiate(data.AimMarkerPrefab);
            ThrowableSystem.DisableColliders(aimMarker);
            SetMarkerVisible(false);
        }
    }

    /// <summary>Renderer.enabled, non SetActive (invariante dei componenti visivi).</summary>
    private void SetMarkerVisible(bool visible)
    {
        if (aimMarker == null) return;
        Renderer[] renderers = aimMarker.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            renderers[i].enabled = visible;
    }
}

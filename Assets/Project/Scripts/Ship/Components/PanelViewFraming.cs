using System.Collections;
using UnityEngine;

namespace SpaceSurvivor.Ship
{
    /// <summary>
    /// PanelViewFraming — Rev BY (Q135-a). Inquadratura del monitor per i pannelli a minigame
    /// (RepairPanel, StabilizationPanel), come nelle postazioni (MedicalStation.TransitionToStation).
    ///
    /// ENTRATA (Begin): prima il giocatore scivola davanti al pannello (playerSnapPoint, a velocità
    /// snapTransitionSpeed), poi la camera si gira verso il monitor (cameraLookAtPoint, a velocità
    /// cameraTransitionSpeed). Senza snap point il giocatore resta dov'è e la camera si gira e basta;
    /// senza look-at point la camera non si muove. Del snap point conta solo la direzione orizzontale:
    /// il corpo resta dritto.
    ///
    /// USCITA (End): lo sguardo resta sul monitor. La direzione orizzontale della camera passa al
    /// corpo e l'inclinazione a PlayerController (SetLookPitch, Rev BW-c), come fa la scala: quando
    /// PlayerController torna attivo la visuale non scatta. Il giocatore resta dove è scivolato.
    /// End vale sia per l'uscita normale sia per l'uscita forzata a terra (Q136-a); non tocca
    /// PlayerController.enabled né il CharacterController, che restano al pannello.
    ///
    /// CAMERA: prima di scrivere la rotazione locale in assoluto si annulla lo shake in corso
    /// (CameraShaker.CancelShake, regola Rev BW-b), così il residuo non resta nella posa.
    ///
    /// Locale al client che usa il pannello, come tutto il resto dei pannelli: niente rete.
    ///
    /// SETUP: sullo stesso GameObject del pannello; due oggetti vuoti figli (snap point davanti al
    /// monitor, con l'asse Z blu verso il monitor; look-at point al centro dello schermo).
    /// </summary>
    [DisallowMultipleComponent]
    public class PanelViewFraming : MonoBehaviour
    {
        [Header("Inquadratura (Rev BY · Q135-a)")]
        [Tooltip("Facoltativo. Dove si porta il giocatore davanti al pannello. Conta solo la direzione " +
                 "orizzontale (asse Z blu verso il monitor). Vuoto = il giocatore resta dov'è.")]
        [SerializeField] private Transform playerSnapPoint;

        [Tooltip("Punto al centro dello schermo verso cui si gira la camera. Vuoto = la camera non si muove.")]
        [SerializeField] private Transform cameraLookAtPoint;

        [Tooltip("Velocità dello scivolamento del giocatore (1/s), come nelle postazioni.")]
        [Min(0.1f)]
        [SerializeField] private float snapTransitionSpeed = 5f;

        [Tooltip("Velocità della rotazione della camera verso il monitor (1/s), come nelle postazioni.")]
        [Min(0.1f)]
        [SerializeField] private float cameraTransitionSpeed = 8f;

        private Transform _body;
        private Transform _camera;
        private PlayerController _playerController;
        private Coroutine _routine;

        /// <summary>True fra Begin ed End.</summary>
        public bool IsFraming => _body != null;

        /// <summary>
        /// Inizio dell'inquadratura per chi ha appena aperto il pannello. Il pannello ha già spento
        /// PlayerController e CharacterController.
        /// </summary>
        public void Begin(GameObject interactor)
        {
            if (interactor == null) return;
            if (IsFraming) End();

            _body = interactor.transform;
            _playerController = interactor.GetComponent<PlayerController>();
            Camera cam = interactor.GetComponentInChildren<Camera>();
            _camera = cam != null ? cam.transform : null;

            CameraShaker.LocalInstance?.CancelShake();

            _routine = StartCoroutine(FramingRoutine());
        }

        /// <summary>
        /// Fine dell'inquadratura (uscita normale o a terra). Lo sguardo resta dove si trova: direzione
        /// orizzontale al corpo, inclinazione a PlayerController. Idempotente.
        /// </summary>
        public void End()
        {
            if (!IsFraming) return;

            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }

            HandOverGaze();

            _body = null;
            _camera = null;
            _playerController = null;
        }

        private void OnDisable()
        {
            // Pannello spento a metà: la camera non resta in una posa che PlayerController non conosce.
            End();
        }

        private IEnumerator FramingRoutine()
        {
            // 1. Il giocatore scivola davanti al pannello.
            if (playerSnapPoint != null && _body != null)
            {
                Vector3 startPos = _body.position;
                Quaternion startRot = _body.rotation;
                Vector3 targetPos = playerSnapPoint.position;
                Quaternion targetRot = YawOnly(playerSnapPoint.rotation, startRot);

                float progress = 0f;
                while (progress < 1f)
                {
                    if (_body == null) { _routine = null; yield break; }   // giocatore uscito dalla sessione
                    progress += Time.deltaTime * snapTransitionSpeed;
                    _body.position = Vector3.Lerp(startPos, targetPos, progress);
                    _body.rotation = Quaternion.Lerp(startRot, targetRot, progress);
                    yield return null;
                }

                if (_body == null) { _routine = null; yield break; }
                _body.position = targetPos;
                _body.rotation = targetRot;
            }

            // 2. La camera si gira verso il monitor (stesso schema di MedicalStation).
            if (cameraLookAtPoint != null && _camera != null && _camera.parent != null)
            {
                Vector3 direction = cameraLookAtPoint.position - _camera.position;
                if (direction.sqrMagnitude > 1e-6f)
                {
                    Quaternion worldTarget = Quaternion.LookRotation(direction.normalized);
                    Quaternion localTarget = Quaternion.Inverse(_camera.parent.rotation) * worldTarget;
                    Quaternion localStart = _camera.localRotation;

                    float progress = 0f;
                    while (progress < 1f)
                    {
                        if (_camera == null) { _routine = null; yield break; }   // giocatore uscito dalla sessione
                        progress += Time.deltaTime * cameraTransitionSpeed;
                        // Scrittura in assoluto a ogni frame: uno shake partito durante la rotazione
                        // (urto) va chiuso prima, altrimenti il suo residuo resta sulla camera (Rev BW-b).
                        CameraShaker.LocalInstance?.CancelShake();
                        _camera.localRotation = Quaternion.Lerp(localStart, localTarget, progress);
                        yield return null;
                    }

                    if (_camera == null) { _routine = null; yield break; }
                    CameraShaker.LocalInstance?.CancelShake();
                    _camera.localRotation = localTarget;
                }
            }

            _routine = null;
        }

        /// <summary>
        /// Lo sguardo passa al giocatore: la direzione orizzontale della camera diventa la rotazione del
        /// corpo, l'inclinazione va a PlayerController (che la riscrive sulla camera senza rotazione
        /// orizzontale). Senza camera o PlayerController non fa nulla.
        /// </summary>
        private void HandOverGaze()
        {
            if (_body == null || _camera == null || _playerController == null) return;

            CameraShaker.LocalInstance?.CancelShake();

            Vector3 forward = _camera.forward;
            Vector3 horizontal = Vector3.ProjectOnPlane(forward, Vector3.up);

            // Guardando quasi in verticale la direzione orizzontale non è definita: il corpo resta com'è.
            if (horizontal.sqrMagnitude > 1e-6f)
                _body.rotation = Quaternion.LookRotation(horizontal.normalized, Vector3.up);

            // Positivo = in basso (convenzione di PlayerController.LookPitch).
            float pitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            _playerController.SetLookPitch(pitch);
        }

        /// <summary>Solo la rotazione attorno alla verticale; se la direzione è verticale, quella di ripiego.</summary>
        private static Quaternion YawOnly(Quaternion rotation, Quaternion fallback)
        {
            Vector3 horizontal = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return horizontal.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(horizontal.normalized, Vector3.up)
                : fallback;
        }
    }
}

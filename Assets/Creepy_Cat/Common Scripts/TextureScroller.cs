// Code by Creepy Cat (C) 2021/2025
// URP version — _BaseMap with Built-In fallback
// black.creepy.cat@gmail.com

using UnityEngine;

namespace creepycat.scifikitvol4
{
    [AddComponentMenu("creepycat/Toolbox/Texture Scroller")]
    [RequireComponent(typeof(Renderer))]
    public class TextureScroller : MonoBehaviour
    {
        [Header("Paramètres de défilement")]
        [Tooltip("Vitesse de scroll horizontal (X) en unités par seconde")]
        public float scrollSpeedX = 0.5f;
        [Tooltip("Vitesse de scroll vertical (Y) en unités par seconde")]
        public float scrollSpeedY = 0.3f;

        [Header("Options")]
        [Tooltip("Propriété texture du shader : _BaseMap (URP) ou _MainTex (Built-In)")]
        public string texturePropertyName = "_BaseMap"; // ← URP par défaut
        [Tooltip("Appliquer le scroll en espace local ou mondial")]
        public bool useLocalSpace = false;

        private Renderer _renderer;
        private Material _materialInstance;
        private Vector2 _offset;

        void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (_renderer == null) return;

            // Instanciation propre — sharedMaterial pour éviter la double-instance
            _materialInstance = Instantiate(_renderer.sharedMaterial);
            _renderer.material = _materialInstance;

            // Détection automatique de la propriété texture
            // (corrige la valeur Inspector si le matériau est encore Built-In)
            if (!_materialInstance.HasProperty(texturePropertyName))
            {
                string fallback = texturePropertyName == "_BaseMap" ? "_MainTex" : "_BaseMap";
                if (_materialInstance.HasProperty(fallback))
                {
                    Debug.LogWarning($"[TextureScroller] '{texturePropertyName}' introuvable sur {_materialInstance.name}, " +
                                     $"fallback sur '{fallback}'.");
                    texturePropertyName = fallback;
                }
                else
                {
                    Debug.LogError($"[TextureScroller] Aucune propriété texture connue sur {_materialInstance.name}. " +
                                   "Vérifiez votre shader.");
                }
            }
        }

        void Update()
        {
            if (_materialInstance == null) return;

            float offsetX = scrollSpeedX * Time.time;
            float offsetY = scrollSpeedY * Time.time;

            if (useLocalSpace && transform.lossyScale != Vector3.one)
            {
                Vector3 inv = new Vector3(
                    1f / transform.lossyScale.x,
                    1f / transform.lossyScale.y,
                    1f / transform.lossyScale.z);
                _offset = new Vector2(offsetX * inv.x, offsetY * inv.y);
            }
            else
            {
                _offset = new Vector2(offsetX, offsetY);
            }

            _materialInstance.SetTextureOffset(texturePropertyName, _offset);
        }

        [ContextMenu("Réinitialiser offset texture")]
        public void ResetOffset()
        {
            if (_materialInstance != null)
                _materialInstance.SetTextureOffset(texturePropertyName, Vector2.zero);
        }

        public void SetScrollSpeed(float x, float y)
        {
            scrollSpeedX = x;
            scrollSpeedY = y;
        }

        void OnDestroy()
        {
            if (_materialInstance != null)
            {
                Destroy(_materialInstance);
                _materialInstance = null;
            }
        }
    }
}
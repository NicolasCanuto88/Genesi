// Code by Creepy Cat (C) 2021/2025
// URP version — _BaseMap with Built-In fallback
// black.creepy.cat@gmail.com

using UnityEngine;
using System.Collections;

namespace creepycat.scifikitvol4
{
    public class AnimateSpriteSheet : MonoBehaviour
    {
        public int Columns = 8;
        public int Rows = 8;
        public float FramesPerSecond = 25f;
        public bool RunOnce = true;

        private Material instanceMaterial;
        private string texturePropName = "_BaseMap"; // URP par défaut

        void Awake()
        {
            var rend = GetComponent<Renderer>();
            if (rend == null) return;

            instanceMaterial = Instantiate(rend.sharedMaterial);
            rend.material = instanceMaterial;

            // Détection automatique Built-In / URP
            if (!instanceMaterial.HasProperty("_BaseMap"))
            {
                if (instanceMaterial.HasProperty("_MainTex"))
                    texturePropName = "_MainTex"; // fallback Built-In
                else
                {
                    Debug.LogWarning($"[AnimateSpriteSheet] Aucune propriété texture connue sur {instanceMaterial.name}.");
                    return;
                }
            }

            Vector2 size = new Vector2(1f / Columns, 1f / Rows);
            instanceMaterial.SetTextureScale(texturePropName, size);
        }

        void OnEnable()
        {
            if (instanceMaterial == null) return;
            StartCoroutine(PlayAnimation());
        }

        private IEnumerator PlayAnimation()
        {
            Vector2 offset = Vector2.zero;
            float frameTime = 1f / FramesPerSecond;

            do
            {
                for (int row = Rows - 1; row >= 0; row--)
                {
                    offset.y = (float)row / Rows;
                    for (int col = 0; col < Columns; col++)
                    {
                        offset.x = (float)col / Columns;
                        instanceMaterial.SetTextureOffset(texturePropName, offset);
                        yield return new WaitForSeconds(frameTime);
                    }
                }
            }
            while (!RunOnce);

            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (instanceMaterial != null)
            {
                Destroy(instanceMaterial);
                instanceMaterial = null;
            }
        }
    }
}
using UnityEngine;

public class TurretShootLifetime : MonoBehaviour
{
    [Tooltip("Temps en secondes avant destruction automatique")]
    public float lifetime = 5f;

    void OnEnable()
    {
        Invoke("DestroySelf", lifetime);
    }

    void DestroySelf()
    {
        Destroy(gameObject);
    }

    // Optionnel : destruction à l'impact
    void OnCollisionEnter(Collision collision)
    {
        // Ajoute ici effets d'impact, dégâts, etc.
        Debug.Log ("Object hit!");
        Destroy(gameObject);
    }
}
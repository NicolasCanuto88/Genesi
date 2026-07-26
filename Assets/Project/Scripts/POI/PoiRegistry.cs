using System.Collections.Generic;

namespace SpaceSurvivor.Poi
{
    /// <summary>
    /// PoiRegistry — Milestone 3, Blocco 3, Sottofase 2b.
    ///
    /// Registro server-only dei PoiInstance attualmente attivi nella sessione.
    /// Popolato automaticamente dai PoiInstance stessi in OnNetworkSpawn /
    /// OnNetworkDespawn — non serve nessun setup Editor.
    ///
    /// AMBITO: SOLO SERVER.
    ///   I client NON registrano i loro PoiInstance qui. La lista dei POI
    ///   noti a un client si ottiene iterando NetworkManager.SpawnManager
    ///   .SpawnedObjects (o iscrivendosi ai singoli PoiInstance via
    ///   NetworkVariable). Il ScannerSystem (server-authoritative) è
    ///   l'unico consumer previsto di questa struttura.
    ///
    /// COERENZA: ogni Register va bilanciato da un Unregister. In pratica il
    /// PoiInstance chiama Register in OnNetworkSpawn (se IsServer) e
    /// Unregister in OnNetworkDespawn (se IsServer). Idempotente su
    /// double-register/unregister difensivamente.
    ///
    /// CONCORRENZA: NGO invoca callback network dal main thread → nessun
    /// lock necessario. Se in futuro fossero invocati da altri thread, il
    /// dizionario andrebbe protetto — per ora non serve.
    /// </summary>
    public static class PoiRegistry
    {
        // NetworkObjectId (ulong, unico per sessione) → PoiInstance.
        // Uso NetworkObjectId invece di GameObject.GetInstanceID() perché è
        // stabile e ha lo stesso valore su server e client (utile in futuro
        // se il registry andasse esteso client-side).
        private static readonly Dictionary<ulong, PoiInstance> _byNetworkId
            = new Dictionary<ulong, PoiInstance>();

        /// <summary>
        /// Iterabile stabile dei POI registrati. Il ScannerSystem itera
        /// questa collezione ogni tick di scan per calcolare distanza logica.
        ///
        /// ATTENZIONE: non modificare la collezione durante l'iterazione.
        /// Se serve raccogliere POI da rimuovere durante iter, accumulare
        /// in una lista temporanea e chiamare Unregister dopo.
        /// </summary>
        public static IEnumerable<PoiInstance> All => _byNetworkId.Values;

        /// <summary>Numero di POI attualmente registrati. Usato dal
        /// PoiSpawner per rispettare maxActivePoi.</summary>
        public static int Count => _byNetworkId.Count;

        /// <summary>
        /// Registra un PoiInstance. Chiamato da PoiInstance.OnNetworkSpawn
        /// se IsServer. Idempotente: chiamate multiple sullo stesso oggetto
        /// non fanno danno.
        /// </summary>
        public static void Register(PoiInstance poi)
        {
            if (poi == null) return;
            if (poi.NetworkObject == null) return;

            ulong id = poi.NetworkObject.NetworkObjectId;
            _byNetworkId[id] = poi; // sovrascrivere è sicuro (idempotenza)
        }

        /// <summary>
        /// Rimuove un PoiInstance dal registro. Chiamato da
        /// PoiInstance.OnNetworkDespawn se IsServer. Idempotente.
        /// </summary>
        public static void Unregister(PoiInstance poi)
        {
            if (poi == null) return;
            if (poi.NetworkObject == null) return;

            ulong id = poi.NetworkObject.NetworkObjectId;
            _byNetworkId.Remove(id);
        }

        /// <summary>
        /// Svuota il registro. Utile in caso di rientro in MainMenu / cambio
        /// scena / disconnessione host. Non chiamato automaticamente in 2b —
        /// da agganciare a un evento di teardown della sessione se emerge un
        /// bug di POI "fantasma" tra sessioni consecutive.
        /// </summary>
        public static void Clear()
        {
            _byNetworkId.Clear();
        }
    }
}

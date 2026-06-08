using UnityEngine;
using TMPro;

/// <summary>
/// CrewHPEntry — Milestone 2
/// Componente da aggiungere a ogni GameObject CrewRow nella gerarchia UI
/// della Medical Station.
///
/// Contiene nome, barra HP, testo HP e badge status per un singolo membro crew.
/// In M2: dati impostati manualmente via SetData() con valori stub.
/// In M3: aggiornato da PlayerHealthSystem con dati reali (NetworkVariable).
///
/// ⚠️ Dipende da: PlayerHealthSystem (M3) per dati reali multiplayer.
/// </summary>
public class CrewHPEntry : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI   nameLabel;
    [SerializeField] private SciFiSegmentedBar hpBar;
    [SerializeField] private TextMeshProUGUI   hpText;
    [SerializeField] private TextMeshProUGUI   statusBadge;

    public void SetData(string crewName, float currentHP, float maxHP, Color statusColor)
    {
        float percent = maxHP > 0f ? currentHP / maxHP : 0f;

        if (nameLabel != null) nameLabel.text = crewName;
        if (hpBar     != null) hpBar.SetValue(percent);
        if (hpText    != null) hpText.text    = $"{currentHP:F0} / {maxHP:F0}";

        if (statusBadge == null) return;

        if (percent <= 0f)
        {
            statusBadge.text  = "MORTO";
            statusBadge.color = new Color(0.4f, 0.4f, 0.4f);
        }
        else if (percent < 0.20f)
        {
            statusBadge.text  = "CRITICO";
            statusBadge.color = new Color(1f, 0.2f, 0f);
        }
        else if (percent < 0.40f)
        {
            statusBadge.text  = "FERITO";
            statusBadge.color = new Color(1f, 0.67f, 0f);
        }
        else
        {
            statusBadge.text  = "OK";
            statusBadge.color = statusColor;
        }
    }
}

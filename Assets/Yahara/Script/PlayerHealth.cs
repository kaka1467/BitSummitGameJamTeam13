using UnityEngine;
using UnityEngine.UI;

public class PlayerHealth : MonoBehaviour
{
    public int maxHP = 100;
    public int currentHP;

    public Image hpFill;

    void Start()
    {
        currentHP = maxHP;
        UpdateHPBar();
    }

    public void TakeDamage(int damage)
    {
        currentHP -= damage;

        if (currentHP < 0)
            currentHP = 0;

        UpdateHPBar();

        if (currentHP == 0)
        {
            GameManager.instance.GameOver();
        }
    }

    public void Heal(int amount)
    {
        currentHP += amount;

        if (currentHP > maxHP)
            currentHP = maxHP;

        UpdateHPBar();
    }

    void UpdateHPBar()
    {
        hpFill.fillAmount = (float)currentHP / maxHP;
    }
}
using UnityEngine;
using UnityEngine.UI;

public class PlayerHealth : MonoBehaviour
{
    public static PlayerHealth Instance { get; private set; }

    public int maxHP = 100;
    public int currentHP;

    [Header("HP ゲージ PNG")]
    public Image hpFill; // HPバー本体 (Image Type: Filled / Horizontal)

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        currentHP = maxHP;
        UpdateHPBar();
    }

    public void TakeDamage(int damage)
    {
        currentHP -= damage;
        if (currentHP < 0) currentHP = 0;

        UpdateHPBar();

        if (currentHP == 0)
        {
            GameManager.instance.GameOver("CHILD_DEAD");
        }
    }

    public void Heal(int amount)
    {
        currentHP += amount;
        if (currentHP > maxHP) currentHP = maxHP;

        UpdateHPBar();
    }

    void UpdateHPBar()
    {
        float ratio = (float)currentHP / maxHP;
        hpFill.fillAmount = ratio;
    }
}
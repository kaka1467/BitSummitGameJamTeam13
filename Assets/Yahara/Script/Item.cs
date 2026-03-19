using UnityEngine;


public class Item : MonoBehaviour
{
    public ItemType itemType;

    public int scoreAmount = 10;
    public int healAmount = 10;

    public int damageAmount = 10;
    public float timeAmount = 10f;

    public float boostDuration = 0f;
    public float boostMultiplier = 0f;

    // このアイテムがフィーバー時の影響を受けるか
    public bool isMagnetable = true;

}
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 子機の「本編中に鳴る音」の安定ID。
///  ・通信（親機への通知）と対応表（AudioClip）はこのIDで結ぶ。
///  ・clip.name では識別しない（同名素材の衝突を避けるため、対応表は AudioClip の参照一致で引く）。
///  ・送受信で使う値なので、既存の値は変更しない（追加は空いている番号で足す）。
/// </summary>
public enum GameAudioId
{
    /// <summary>未登録・無効。</summary>
    None = 0,

    /// <summary>本編BGM（BGMController の音源・ループ）。親機では「状態」として同期する。</summary>
    BgmMain = 1,

    /// <summary>アイテム取得（Score／Boost の Open_Energy_Drink）。</summary>
    SeItemEnergyDrink = 10,

    /// <summary>アイテム取得（MoreScore の Stronger_Energy_Drink）。</summary>
    SeItemMoreScore = 11,

    /// <summary>時間減少アイテム（DecrementClock の Decrease_Time）。</summary>
    SeItemClock = 12,

    /// <summary>フィーバーアイテム（Fever の FeverTime_BGM・単発再生）。</summary>
    SeItemFever = 13,

    /// <summary>巨大障害物／QTE（QTE_2_Hawk_Sound）。</summary>
    SeItemObstacle = 14,

    /// <summary>ラウドアイテムの音楽（BGMAitem が PlayBGM 経由で単発再生する Obstructon）。</summary>
    SeLoudItemMusic = 15,

    /// <summary>被弾（PlayerAnimator の Damage BGM）。</summary>
    SePlayerDamage = 16,
}

/// <summary>
/// 音ID → AudioClip の対応表。
///  ・子機（通知元）と親機（再生側）で「同じアセットを参照する」ことで、対応表のずれを無くす。
///  ・親機側でも参照を持つため、素材はビルドに含まれる。
///  ・未登録IDは例外にしない（呼び出し側が1回だけ警告して読み飛ばす）。
/// </summary>
[CreateAssetMenu(fileName = "GameAudioCatalog", menuName = "Team13/Game Audio Catalog")]
public class GameAudioCatalog : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("この音の安定ID。送受信で使うため、既存の値は変更しない。")]
        public GameAudioId id = GameAudioId.None;

        [Tooltip("本編BGMとして状態同期する（ON: 再通知で先頭に戻さない／OFF: 単発SEとして鳴らす）。")]
        public bool isBgm;

        [Tooltip("親機で再生するときの音量の基準値（1=子機と同じ相対音量）。親機全体の音量とは別に掛かる。")]
        [Range(0f, 2f)]
        public float volumeScale = 1f;

        [Tooltip("実際の音素材。親機側でもこの参照を使う。")]
        public AudioClip clip;
    }

    [SerializeField]
    private Entry[] entries = Array.Empty<Entry>();

    private Dictionary<GameAudioId, Entry> idMap;
    private Dictionary<AudioClip, Entry> clipMap;

    /// <summary>登録されている項目（読み取り専用）。</summary>
    public IReadOnlyList<Entry> Entries => entries;

    /// <summary>IDから項目を引く（未登録・clip未設定なら null）。</summary>
    public Entry Find(GameAudioId id)
    {
        BuildMaps();
        return idMap.TryGetValue(id, out Entry entry) ? entry : null;
    }

    /// <summary>AudioClipの参照から項目を引く（同名素材でも衝突しない）。</summary>
    public Entry Find(AudioClip clip)
    {
        if (clip == null) return null;
        BuildMaps();
        return clipMap.TryGetValue(clip, out Entry entry) ? entry : null;
    }

    /// <summary>再生可能な項目（IDとclipの両方が有効）からclipを引く。</summary>
    public AudioClip ResolveClip(GameAudioId id)
    {
        Entry entry = Find(id);
        return entry != null ? entry.clip : null;
    }

    /// <summary>そのIDが「本編BGM（状態同期）」として登録されているか。未登録は false。</summary>
    public bool IsBgm(GameAudioId id)
    {
        Entry entry = Find(id);
        return entry != null && entry.isBgm;
    }

    private void BuildMaps()
    {
        if (idMap != null) return;

        idMap = new Dictionary<GameAudioId, Entry>();
        clipMap = new Dictionary<AudioClip, Entry>();

        if (entries == null) return;

        for (int i = 0; i < entries.Length; i++)
        {
            Entry entry = entries[i];
            if (entry == null || entry.id == GameAudioId.None) continue;

            // 同じIDは後勝ち（設定ミスの検出はエディタ検証に任せる）
            idMap[entry.id] = entry;

            // 同じ素材を複数のIDへ割り当てても壊れないように、最初の1件だけを逆引きに使う
            if (entry.clip != null && !clipMap.ContainsKey(entry.clip))
                clipMap[entry.clip] = entry;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // エディタで編集した内容を実行中にも即反映する（マップを作り直すだけ）。
        idMap = null;
        clipMap = null;
    }
#endif
}

using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>パターン内の1個が「どの種類のアイテムか」。具体的なPrefabは ItemPool の登録内容から自動で選ぶ。</summary>
public enum PatternItemKind
{
    /// <summary>得点の低い Score（登録されている Score 型Prefabのうち scoreAmount が最小）。</summary>
    LowScore,
    /// <summary>得点の高い Score（同じく scoreAmount が最大。Score型が1種類だけなら LowScore と同じ）。</summary>
    HighScore,
    /// <summary>悪いアイテム（Clock＝減点）。サイレン(BGM)は時間条件があるためパターンには使わない。</summary>
    Bad,
    Fever,
    Boost,
}

/// <summary>そのパターンをいつ使うか。</summary>
public enum PatternUse
{
    /// <summary>通常時のみ（フィーバー中は選ばない）。</summary>
    NormalOnly,
    /// <summary>フィーバー中のみ（フィーバー開始時に1回出す。悪いアイテムは入れない前提）。</summary>
    FeverOnly,
}

[Serializable]
public class PatternSlot
{
    public PatternItemKind kind = PatternItemKind.LowScore;

    [Tooltip("レーン番号（0=上, 1=中, 2=下。lanesY の並び順）。範囲外は端に丸める。")]
    [Min(0)] public int lane = 1;

    [Tooltip("先頭からの列番号。1列あたりの間隔は SpawnPattern.columnSpacing。0 が最初に画面へ入る列。")]
    [Min(0f)] public float column = 0f;
}

/// <summary>
/// 短い配置パターン1つ分の定義。slots が空で restSeconds が正なら「休憩区間」（アイテムは出さず、通常出現を薄くするだけ）。
/// </summary>
[Serializable]
public class SpawnPattern
{
    public string name = "Pattern";
    public bool enabled = true;

    [Tooltip("選ばれやすさ（相対重み）。0 以下なら選ばれない。")]
    [Min(0f)] public float weight = 1f;

    public PatternUse use = PatternUse.NormalOnly;

    [Tooltip("本編開始からこの秒数が過ぎるまでは選ばない。")]
    [Min(0f)] public float minElapsedSeconds = 0f;

    [Tooltip("列と列の間隔（ワールド単位）。アイテムの実サイズより狭くはならない（自動で広げる）。")]
    [Min(0.05f)] public float columnSpacing = 0.5f;

    [Tooltip("ONなら、毎回50%で上下を反転する（上段⇔下段）。中段だけのパターンでは効果なし。")]
    public bool allowVerticalFlip = true;

    [Tooltip("このパターンの後（配置が画面を抜けた後）に続ける休憩区間の長さ（秒）。0 で休憩なし。")]
    [Min(0f)] public float restSeconds = 0f;

    [Tooltip("休憩区間中の通常出現の濃さ（1=通常、0.2=通常の2割）。")]
    [Range(0.05f, 1f)] public float restDensity = 0.25f;

    public List<PatternSlot> slots = new List<PatternSlot>();
}

/// <summary>既定の配置パターン（Inspectorで編集・追加・削除できる。コード定義ランパターン有効時はこの内容を使う）。</summary>
public static class ItemSpawnPatternDefaults
{
    public static List<SpawnPattern> Create()
    {
        var list = new List<SpawnPattern>();

        // ── 序盤から登場する基本レーン（長めデフォルト＋時々短め＋超ロング） ─────────
        // 1. 基本のロングレーン（8連）
        list.Add(Pattern("DefaultRibbon_Mid", 1.25f, PatternUse.NormalOnly, 0f, 1.0f, false, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 8)));

        // 2. ロングレーン（9連）＋末尾に悪いアイテム（取ってすぐ避ける）
        list.Add(Pattern("LongRibbon_TailBad", 1.35f, PatternUse.NormalOnly, 0f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 9),
            Slot(PatternItemKind.Bad, 0, 10.2f)));

        // 2b. 超ロングレーン（14連：低得点11＋高得点3）＋末尾に悪いアイテム
        list.Add(Pattern("MegaRibbon_Straight", 1.1f, PatternUse.NormalOnly, 5f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 11),
            Row(PatternItemKind.HighScore, 1, 11f, 3),
            Slot(PatternItemKind.Bad, 1, 15.2f)));

        // 3. レーンが途中で途切れて悪いアイテムを避け、別レーンへ続く（6連→6連）
        list.Add(Pattern("BreakAndDodge_MidToEdge", 1.4f, PatternUse.NormalOnly, 0f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 6),
            Slot(PatternItemKind.Bad, 1, 7.2f),
            Row(PatternItemKind.LowScore, 0, 8.2f, 6)));

        // 4. 2段レーン＋末尾に高得点（5連→5連）
        list.Add(Pattern("TwoLane_JumpLong", 1.25f, PatternUse.NormalOnly, 0f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 5),
            Slot(PatternItemKind.Bad, 0, 6.2f),
            Row(PatternItemKind.LowScore, 2, 7.0f, 4),
            Slot(PatternItemKind.HighScore, 2, 11.0f)));

        // 4b. 時々来る短めアクセントレーン（4連＋末尾悪いアイテム）
        list.Add(Pattern("ShortAccent_Quick", 0.6f, PatternUse.NormalOnly, 0f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 4),
            Slot(PatternItemKind.Bad, 0, 5.2f)));

        // 5. なめらかな階段レーン＋直前レーンの追い出し悪いアイテム（長め）
        list.Add(Pattern("StairWave_Early", 1.05f, PatternUse.NormalOnly, 8f, 1.10f, true, 0f,
            Seq(PatternItemKind.LowScore, 0, 0, 1, 2, 2, 2, 2, 1, 0, 0, 0),
            Slot(PatternItemKind.Bad, 0, 3.0f),
            Slot(PatternItemKind.Bad, 2, 8.0f)));

        // ── 中盤（12〜35秒以降）：長めの分岐・ハイリスク高得点・超ロング ───────────────
        // 6. 安全な低得点ロングレーン vs 悪いアイテムの後ろの高得点レーン
        list.Add(Pattern("RiskReward_Fork", 1.3f, PatternUse.NormalOnly, 20f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 2, 0f, 8),
            Slot(PatternItemKind.Bad, 0, 0.5f),
            Row(PatternItemKind.HighScore, 0, 2.0f, 6)));

        // 6b. 超ロング2段スイッチレーン（8連→悪い回避→8連：計16連）
        list.Add(Pattern("MegaRibbon_SwitchMid", 1.05f, PatternUse.NormalOnly, 20f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 8),
            Slot(PatternItemKind.Bad, 0, 9.2f),
            Row(PatternItemKind.LowScore, 2, 10.0f, 5),
            Row(PatternItemKind.HighScore, 2, 15.0f, 3)));

        // 7. 2回途切れて避けながら進む長めジグザグレーン（5連→6連→5連）
        list.Add(Pattern("DoubleBreak_Weave", 1.3f, PatternUse.NormalOnly, 25f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 5),
            Slot(PatternItemKind.Bad, 0, 6.2f),
            Row(PatternItemKind.LowScore, 1, 7.0f, 6),
            Slot(PatternItemKind.Bad, 1, 14.2f),
            Row(PatternItemKind.HighScore, 2, 15.0f, 5)));

        // 8. 長い中央レーン（10連：左右に悪いアイテムのプレッシャー＋末尾に悪いアイテム）
        list.Add(Pattern("LongCorridor_FlankBad", 1.3f, PatternUse.NormalOnly, 30f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 7),
            Row(PatternItemKind.HighScore, 1, 7f, 3),
            Slot(PatternItemKind.Bad, 0, 2.0f),
            Slot(PatternItemKind.Bad, 2, 5.5f),
            Slot(PatternItemKind.Bad, 0, 8.5f),
            Slot(PatternItemKind.Bad, 1, 11.2f)));

        // 9. 中央から上下へ分岐（片方は高得点6連、片方は悪いアイテム多めの罠レーン）
        list.Add(Pattern("Fork_MidToGoodOrBad", 1.35f, PatternUse.NormalOnly, 18f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 4),
            Slot(PatternItemKind.Bad, 1, 5.2f),
            Row(PatternItemKind.HighScore, 0, 6.2f, 6),
            Slot(PatternItemKind.LowScore, 2, 6.2f),
            Slot(PatternItemKind.Bad, 2, 7.6f),
            Slot(PatternItemKind.Bad, 2, 9.2f),
            Slot(PatternItemKind.Bad, 2, 10.8f)));

        // 9b. 2択の分かれ道（良いアイテム8連ぎっしり vs 悪いアイテム多めレーン）
        list.Add(Pattern("Fork_GoodVsBad", 1.4f, PatternUse.NormalOnly, 12f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 6),
            Row(PatternItemKind.HighScore, 0, 6f, 2),
            Slot(PatternItemKind.LowScore, 2, 0f),
            Slot(PatternItemKind.Bad, 2, 1.5f),
            Slot(PatternItemKind.Bad, 2, 3.5f),
            Slot(PatternItemKind.Bad, 2, 5.5f),
            Slot(PatternItemKind.Bad, 2, 7.2f)));

        // 9c. 時々来る短めホップ（3連→悪い回避→4連）
        list.Add(Pattern("ShortAccent_Hop", 0.55f, PatternUse.NormalOnly, 20f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 3),
            Slot(PatternItemKind.Bad, 1, 4.2f),
            Row(PatternItemKind.HighScore, 0, 5.0f, 4)));

        // ── 後半（55秒〜90秒以降）：悪いアイテムが増えつつも、長めのレーンが途切れない ──
        // 10. 後半スラローム（6連→末尾悪い→6連）
        list.Add(Pattern("Late_SlalomLanes", 1.6f, PatternUse.NormalOnly, 55f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 6),
            Slot(PatternItemKind.Bad, 2, 2.5f),
            Slot(PatternItemKind.Bad, 0, 7.2f),
            Row(PatternItemKind.HighScore, 2, 8.0f, 6),
            Slot(PatternItemKind.Bad, 1, 10.5f),
            Slot(PatternItemKind.Bad, 2, 15.2f)));

        // 10b. 後半の分かれ道（片方は高得点7連＋末尾悪い、片方は悪いアイテム密集の罠）
        list.Add(Pattern("Late_ForkGoodVsTrap", 1.65f, PatternUse.NormalOnly, 55f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 3),
            Row(PatternItemKind.HighScore, 0, 3.8f, 7),
            Slot(PatternItemKind.Bad, 0, 12.0f),
            Slot(PatternItemKind.Bad, 2, 3.8f),
            Slot(PatternItemKind.Bad, 2, 5.6f),
            Slot(PatternItemKind.Bad, 2, 7.4f),
            Slot(PatternItemKind.Bad, 2, 9.2f),
            Slot(PatternItemKind.HighScore, 2, 10.6f)));

        // 11. 後半ロングトンネル（中央10連、上下に悪いアイテムの壁、末尾に悪いアイテム）
        list.Add(Pattern("Late_LongTunnel", 1.65f, PatternUse.NormalOnly, 60f, 1.0f, false, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 7),
            Row(PatternItemKind.HighScore, 1, 7f, 3),
            Slot(PatternItemKind.Bad, 0, 1.5f),
            Slot(PatternItemKind.Bad, 2, 1.5f),
            Slot(PatternItemKind.Bad, 0, 5.5f),
            Slot(PatternItemKind.Bad, 2, 5.5f),
            Slot(PatternItemKind.Bad, 0, 9.0f),
            Slot(PatternItemKind.Bad, 2, 9.0f),
            Slot(PatternItemKind.Bad, 1, 11.2f)));

        // 11b. 後半メガコリドー（13連の超ロングレーン＋上下の悪いアイテム＋末尾悪いアイテム）
        list.Add(Pattern("Late_MegaCorridor", 1.45f, PatternUse.NormalOnly, 65f, 1.0f, false, 0f,
            Row(PatternItemKind.LowScore, 1, 0f, 9),
            Row(PatternItemKind.HighScore, 1, 9f, 4),
            Slot(PatternItemKind.Bad, 0, 2.0f),
            Slot(PatternItemKind.Bad, 2, 4.5f),
            Slot(PatternItemKind.Bad, 0, 7.5f),
            Slot(PatternItemKind.Bad, 2, 10.0f),
            Slot(PatternItemKind.Bad, 1, 14.2f)));

        // 12. 後半2レーン壁越えダッシュ（6連→2レーン壁→7連）
        list.Add(Pattern("Late_WallBreakDash", 1.7f, PatternUse.NormalOnly, 75f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 6),
            Slot(PatternItemKind.Bad, 0, 7.2f),
            Slot(PatternItemKind.Bad, 1, 7.2f),
            Row(PatternItemKind.HighScore, 2, 8.2f, 7),
            Slot(PatternItemKind.Bad, 2, 16.2f)));

        // 13. 後半3段ステップ（5連→5連→6連を乗り換え、各末尾に悪いアイテム）
        list.Add(Pattern("Late_TripleStep", 1.75f, PatternUse.NormalOnly, 90f, 1.0f, true, 0f,
            Row(PatternItemKind.LowScore, 2, 0f, 5),
            Slot(PatternItemKind.Bad, 2, 6.2f),
            Row(PatternItemKind.LowScore, 1, 7.0f, 5),
            Slot(PatternItemKind.Bad, 1, 13.2f),
            Slot(PatternItemKind.Bad, 2, 13.2f),
            Row(PatternItemKind.HighScore, 0, 14.0f, 6),
            Slot(PatternItemKind.Bad, 0, 21.2f)));

        // ── 短い息継ぎ（ごくまれに挟むだけ） ──────────────────────────────────────────
        SpawnPattern shortRest = Pattern("ShortBreather", 0.15f, PatternUse.NormalOnly, 20f, 1.0f, false, 0.9f);
        shortRest.restDensity = 0.35f;
        list.Add(shortRest);

        // ── フィーバー中に稼ぐ配置（悪いアイテムなし・密なロングレーン） ───────────────
        list.Add(Pattern("Fever_ScoreWall", 1f, PatternUse.FeverOnly, 0f, 1.0f, false, 0f,
            Row(PatternItemKind.LowScore, 0, 0f, 9),
            Row(PatternItemKind.LowScore, 1, 0f, 9),
            Row(PatternItemKind.LowScore, 2, 0f, 9)));

        list.Add(Pattern("Fever_HighWave", 1f, PatternUse.FeverOnly, 0f, 1.05f, false, 0f,
            Seq(PatternItemKind.HighScore, 1, 0, 0, 0, 1, 2, 2, 2, 1, 0, 0, 0, 1)));

        return list;
    }

    private static SpawnPattern Pattern(string name, float weight, PatternUse use, float minElapsed,
        float spacing, bool flip, float rest, params List<PatternSlot>[] rows)
    {
        var p = new SpawnPattern
        {
            name = name,
            weight = weight,
            use = use,
            minElapsedSeconds = minElapsed,
            columnSpacing = spacing,
            allowVerticalFlip = flip,
            restSeconds = rest,
        };

        if (rows != null)
        {
            foreach (var row in rows) p.slots.AddRange(row);
        }

        return p;
    }

    private static List<PatternSlot> Slot(PatternItemKind kind, int lane, float column)
    {
        return new List<PatternSlot>(1)
        {
            new PatternSlot { kind = kind, lane = lane, column = column }
        };
    }

    /// <summary>同じレーンに startColumn から count 個を1列おきに並べる。</summary>
    private static List<PatternSlot> Row(PatternItemKind kind, int lane, float startColumn, int count)
    {
        var row = new List<PatternSlot>(count);
        for (int i = 0; i < count; i++)
            row.Add(new PatternSlot { kind = kind, lane = lane, column = startColumn + i });
        return row;
    }

    /// <summary>列0から1列ずつ、指定したレーン順に並べる（上下移動用）。</summary>
    private static List<PatternSlot> Seq(PatternItemKind kind, params int[] lanes)
    {
        var row = new List<PatternSlot>(lanes.Length);
        for (int i = 0; i < lanes.Length; i++)
            row.Add(new PatternSlot { kind = kind, lane = lanes[i], column = i });
        return row;
    }
}

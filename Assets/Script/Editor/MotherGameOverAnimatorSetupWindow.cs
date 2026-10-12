using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// MotherGameOverAnimatorSetupWindow：
/// ゲームオーバー演出で使う「母親の Animator Controller」と「関連 Animation Clip」を、
/// 実コード（MotherAnimationPlayer / MotherGameOverSequenceController）の要求に合わせて
/// 検証・セットアップする Editor ツール。
///
/// 方針：
///   ・メニュー実行だけではアセットを書き換えない（「検証」と「適用」を分離する）。
///   ・Controller は UnityEditor.Animations の API で編集する（YAML を直接編集しない）。
///   ・FBX のクリップ設定は ModelImporter API で編集し、他のクリップ定義は維持する。
///   ・GUID / fileID を推測して書かない。
///   ・冪等（2回適用してもステート／パラメーター／遷移を重複作成しない）。
///   ・対象ステートが特定できない場合は、曖昧なまま変更しない。
///
/// このクラスは Assets/Script/Editor 配下にあるため、ランタイムビルドには含まれない。
/// </summary>
public class MotherGameOverAnimatorSetupWindow : EditorWindow
{
    private const string MenuPath = "Tools/親機/ゲームオーバーAnimatorセットアップ";

    // ── 実コードから確定した「必要なパラメーターと型」───────────────────────
    //   MotherAnimationPlayer の const / MotherSuspicionSystem の Peek 発火名に対応。
    private static readonly (string name, AnimatorControllerParameterType type)[] RequiredParameters =
    {
        // 通常
        ("Walk", AnimatorControllerParameterType.Bool),
        ("Peek_Door", AnimatorControllerParameterType.Trigger),
        ("Peek_Windows", AnimatorControllerParameterType.Trigger),
        ("Chore_Start", AnimatorControllerParameterType.Trigger),
        ("Chore_Peek_Trigger", AnimatorControllerParameterType.Trigger),
        ("Chore_ExitRequested", AnimatorControllerParameterType.Bool),
        ("Door_OpenTrigger", AnimatorControllerParameterType.Trigger),
        ("Door_OpenSpeed", AnimatorControllerParameterType.Float),
        ("WalkSpeed", AnimatorControllerParameterType.Float),
        // ゲームオーバー（Animator.Play ではなく Transition で入る）
        ("GameOverActive", AnimatorControllerParameterType.Bool),
        ("WalkFast", AnimatorControllerParameterType.Bool),
        ("JumpscareTrigger", AnimatorControllerParameterType.Trigger),
        ("GameOverStopTrigger", AnimatorControllerParameterType.Trigger),
    };

    // 実コードが使うステート名（MotherAnimationPlayer の SerializeField 既定値／シーン上書き値）
    private const string DefaultIdle = "Idle";
    private const string DefaultWalk = "Walk";
    private const string DefaultWalkFast = "Walk_Fast";
    private const string DefaultJumpScare = "Jumpscare";
    private const string DefaultDoorOpen = "Door_Open";
    private const string DefaultChoreLoop = "Chore_Loop";
    private const string DefaultChorePeek = "Chore_Peek";
    private const string DefaultChoreEnd = "Chore_End";

    // ── 期待する遷移（from → to : 条件） ─────────────────────────────────────
    private enum CondKind { None, Trigger, BoolTrue, BoolFalse }

    private struct TransitionSpec
    {
        public string from;
        public string to;
        public CondKind cond;
        public string param;

        public TransitionSpec(string from, string to, CondKind cond, string param)
        {
            this.from = from; this.to = to; this.cond = cond; this.param = param;
        }

        public string Describe() =>
            cond switch
            {
                CondKind.Trigger => $"{from} → {to} （{param} Trigger）",
                CondKind.BoolTrue => $"{from} → {to} （{param} == true）",
                CondKind.BoolFalse => $"{from} → {to} （{param} == false）",
                _ => $"{from} → {to}"
            };
    }

    // ウィンドウの状態
    [SerializeField] private AnimatorController _controllerOverride;
    [SerializeField] private string _reportText = "";
    [SerializeField] private string _planText = "";
    [SerializeField] private AnimationClip _walkFastMotionChoice;
    [SerializeField] private AnimationClip _jumpScareMotionChoice;

    [Tooltip("追加する遷移（Peek_Door/Peek_Window → Door_Open）の Transition Duration（秒）。\n" +
             "Has Exit Time は OFF 固定。reachTimeoutSeconds は『追加遷移自体の所要時間の上限』として照合します" +
             "（Door_Open 到達時間全体を保証するものではありません）。")]
    [SerializeField] private float _newTransitionDuration = 0.25f;

    [Tooltip("追加する遷移の Transition Offset。遷移先ステートの**正規化された**開始位置（0=先頭。秒ではありません）。\n" +
             "今回の追加遷移は 0 を推奨します（0以外はDoor_Openをクリップ途中から再生します）。")]
    [SerializeField] private float _newTransitionOffset = 0f;

    private AnimatorController _autoResolved;
    private bool _autoResolvedTried;
    private Vector2 _scrollReport;
    private Vector2 _scrollPlan;

    private enum Severity { Ok, Info, Warn, Missing }

    private class Finding
    {
        public Severity severity;
        public string text;
        public Finding(Severity s, string t) { severity = s; text = t; }
    }

    private class PlanItem
    {
        public string description;
        public Action apply;
    }

    [MenuItem(MenuPath)]
    private static void Open()
    {
        var window = GetWindow<MotherGameOverAnimatorSetupWindow>(false, "ゲームオーバーAnimator", true);
        window.minSize = new Vector2(640f, 520f);
        window.Show();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  GUI
    // ──────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.HelpBox(
            "ゲームオーバー演出で使う母親の Animator Controller と関連クリップを検証・セットアップします。\n" +
            "・まず「検証」で現在値・変更予定・不足を確認してください。\n" +
            "・アセットを書き換えるのは「適用」を押したときだけです（Undo 対応）。\n" +
            "・対象は MotherAnimationPlayer の Animator が参照する Controller です（名前では決めません）。",
            MessageType.Info);

        EditorGUILayout.Space(4f);

        using (new EditorGUILayout.HorizontalScope())
        {
            _controllerOverride = (AnimatorController)EditorGUILayout.ObjectField(
                new GUIContent("対象Controller", "未設定なら MotherAnimationPlayer の Animator 参照から自動解決します。"),
                _controllerOverride, typeof(AnimatorController), false);

            if (GUILayout.Button("シーンから解決", GUILayout.Width(120f)))
            {
                _autoResolvedTried = false;
                _autoResolved = null;
                var resolved = ResolveControllerFromScene(out string note);
                _controllerOverride = resolved;
                _autoResolved = resolved;
                _autoResolvedTried = true;
                _reportText = note;
            }
        }

        EditorGUILayout.Space(2f);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("検証（変更しない）", GUILayout.Height(26f)))
                RunValidate();

            bool canApply = BuildContext() != null;
            using (new EditorGUI.DisabledScope(!canApply))
            {
                if (GUILayout.Button("適用（変更を書き込む）", GUILayout.Height(26f)))
                    RunApply();
            }
        }

        EditorGUILayout.Space(4f);

        DrawClipChoice(
            "Walk_Fast の Motion（早歩き）",
            "早歩きステートに割り当てる Animation Clip。専用クリップが無い場合は Walk クリップを選び、WalkSpeed 倍率で早く見せる運用が可能です。",
            ref _walkFastMotionChoice);

        DrawClipChoice(
            "Jumpscare の Motion（ジャンプスケア）",
            "ジャンプスケアステートに割り当てる Animation Clip。候補が複数あるときは自動で選びません（ここで明示的に選択してください）。",
            ref _jumpScareMotionChoice);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("ゲームオーバー遷移の設定（Idle中心：捕獲開始→Idle→Walk_Fast⇄Idle→Door_Open／Jumpscare）", EditorStyles.boldLabel);
        _newTransitionDuration = EditorGUILayout.FloatField(
            new GUIContent("Transition Duration (秒)",
                "追加する遷移の Duration（固定秒）。Has Exit Time は OFF 固定。reachTimeoutSeconds は追加遷移自体の所要時間の上限として照合します。"),
            _newTransitionDuration);
        _newTransitionOffset = EditorGUILayout.FloatField(
            new GUIContent("Transition Offset（正規化）",
                "遷移先ステート（Door_Open）の正規化された開始位置（0=先頭）。秒ではありません。今回の追加遷移は 0 を推奨します。"),
            _newTransitionOffset);

        EditorGUILayout.LabelField(
            "共通: Has Exit Time=OFF / InterruptionSource=None / 自己遷移=禁止 / " +
            $"reachTimeoutSeconds: {GetReachTimeoutSeconds():F2}s");

        EditorGUILayout.Space(6f);

        EditorGUILayout.LabelField("検証結果", EditorStyles.boldLabel);
        _scrollReport = EditorGUILayout.BeginScrollView(_scrollReport, GUILayout.MinHeight(150f));
        EditorGUILayout.TextArea(_reportText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("変更予定", EditorStyles.boldLabel);
        _scrollPlan = EditorGUILayout.BeginScrollView(_scrollPlan, GUILayout.MinHeight(110f));
        EditorGUILayout.TextArea(_planText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void DrawClipChoice(string label, string tooltip, ref AnimationClip choice)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(240f));

            var candidates = GetCandidateClips();
            if (candidates.Count == 0)
            {
                EditorGUILayout.LabelField("（候補クリップが見つかりません。Controller を解決してください）");
                return;
            }

            string[] names = new string[candidates.Count];
            int current = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                names[i] = candidates[i].name;
                if (candidates[i] == choice) current = i;
            }

            int selected = EditorGUILayout.Popup(current, names);
            if (selected >= 0 && selected < candidates.Count)
                choice = candidates[selected];
        }
    }

    /// <summary>候補クリップ：Controller の各ステートが参照する Clip の元モデルから全 AnimationClip を集める。</summary>
    private List<AnimationClip> GetCandidateClips()
    {
        var result = new List<AnimationClip>();
        var seen = new HashSet<AnimationClip>();

        var ctx = BuildContext();
        if (ctx == null || ctx.stateMachine == null) return result;

        var modelPaths = new HashSet<string>();
        foreach (var child in ctx.stateMachine.states)
        {
            var clip = child.state != null ? child.state.motion as AnimationClip : null;
            if (clip == null) continue;
            string p = AssetDatabase.GetAssetPath(clip);
            if (!string.IsNullOrEmpty(p)) modelPaths.Add(p);
        }

        foreach (string path in modelPaths)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is AnimationClip c && !c.name.StartsWith("__preview__") && seen.Add(c))
                    result.Add(c);
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  対象Controllerの解決
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>MotherAnimationPlayer の Animator 参照から対象 Controller を解決する（名前で決めない）。</summary>
    private AnimatorController ResolveControllerFromScene(out string note)
    {
        var sb = new StringBuilder();

        var players = UnityEngine.Object.FindObjectsByType<MotherAnimationPlayer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (players == null || players.Length == 0)
        {
            sb.AppendLine("MotherAnimationPlayer が読み込み中のシーンに見つかりません。");
            sb.AppendLine("ParentGameScene を開いてから実行するか、上の欄に Controller を手動で設定してください。");
            note = sb.ToString();
            return null;
        }

        if (players.Length > 1)
        {
            sb.AppendLine($"MotherAnimationPlayer が {players.Length} 個あります。先頭を使用します：");
            foreach (var p in players)
                sb.AppendLine($"  ・{p.name} (scene={p.gameObject.scene.name})");
        }

        var player = players[0];
        var so = new SerializedObject(player);
        var animatorProp = so.FindProperty("animator");
        var animator = animatorProp != null ? animatorProp.objectReferenceValue as Animator : null;

        if (animator == null)
        {
            sb.AppendLine($"MotherAnimationPlayer '{player.name}' の animator が未設定です。");
            note = sb.ToString();
            return null;
        }

        var runtimeController = animator.runtimeAnimatorController;
        if (runtimeController == null)
        {
            sb.AppendLine($"Animator '{animator.name}' の Controller が未設定です。");
            note = sb.ToString();
            return null;
        }

        string path = AssetDatabase.GetAssetPath(runtimeController);

        // AnimatorOverrideController の場合は、元Controllerと差し替えクリップを報告する。
        if (runtimeController is AnimatorOverrideController ov)
        {
            sb.AppendLine("※ AnimatorOverrideController が使われています。");
            sb.AppendLine($"  元Controller: {AssetDatabase.GetAssetPath(ov.runtimeAnimatorController)}");
            if (ov.runtimeAnimatorController is AnimatorController baseController)
                path = AssetDatabase.GetAssetPath(baseController);
        }

        var controllerAsset = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        sb.AppendLine($"対象Controller: {path}");
        sb.AppendLine($"Animator: '{animator.name}' (scene={animator.gameObject.scene.name})");

        note = sb.ToString();
        return controllerAsset;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  コンテキスト構築（実コードの値と Controller の実体を突き合わせる）
    // ──────────────────────────────────────────────────────────────────────────

    private class SetupContext
    {
        public AnimatorController controller;
        public string controllerPath;
        public AnimatorStateMachine stateMachine;
        public MotherAnimationPlayer player;

        public string idle, walk, walkFast, jumpScare, doorOpen, choreLoop, chorePeek, choreEnd;

        /// <summary>MotherAnimationPlayer.reachTimeoutSeconds（Door_Open 開始確認の上限）。</summary>
        public float reachTimeout = 1f;

        public List<Finding> findings = new List<Finding>();
        public List<PlanItem> plan = new List<PlanItem>();

        public AnimatorState State(string name) => FindState(stateMachine, name);
    }

    private SetupContext BuildContext()
    {
        var controller = _controllerOverride;
        if (controller == null)
        {
            // 毎回のシーン検索を避けるため、自動解決の結果をキャッシュする。
            if (!_autoResolvedTried)
            {
                _autoResolvedTried = true;
                _autoResolved = ResolveControllerFromScene(out _);
            }
            controller = _autoResolved;
        }

        if (controller == null) return null;

        var ctx = new SetupContext
        {
            controller = controller,
            controllerPath = AssetDatabase.GetAssetPath(controller),
        };

        if (controller.layers == null || controller.layers.Length == 0)
            return ctx;

        ctx.stateMachine = controller.layers[0].stateMachine;

        // 実コードの値（MotherAnimationPlayer の SerializeField）を読み取る。
        // シーンにインスタンスが無い場合は、実コードの既定値を用いる（値を勝手に確定しない）。
        ctx.player = UnityEngine.Object.FindObjectsByType<MotherAnimationPlayer>(
            FindObjectsInactive.Include, FindObjectsSortMode.None) is { Length: > 0 } found
            ? found[0]
            : null;

        ctx.idle = ReadPlayerString(ctx.player, "idleStateName", DefaultIdle);
        ctx.walk = ReadPlayerString(ctx.player, "walkStateName", DefaultWalk);
        ctx.walkFast = ReadPlayerString(ctx.player, "walkFastStateName", DefaultWalkFast);
        ctx.jumpScare = ReadPlayerString(ctx.player, "jumpScareStateName", DefaultJumpScare);
        ctx.doorOpen = ReadPlayerString(ctx.player, "doorOpenStateName", DefaultDoorOpen);
        ctx.choreLoop = ReadPlayerString(ctx.player, "choreLoopStateName", DefaultChoreLoop);
        ctx.chorePeek = ReadPlayerString(ctx.player, "choreLookStateName", DefaultChorePeek);
        ctx.choreEnd = ReadPlayerString(ctx.player, "choreEndStateName", DefaultChoreEnd);

        // Door_Open 開始確認の上限（追加遷移の Duration をこの範囲に収める）。
        ctx.reachTimeout = ReadPlayerFloat(ctx.player, "reachTimeoutSeconds", 1f);

        return ctx;
    }

    private static string ReadPlayerString(MotherAnimationPlayer player, string field, string fallback)
    {
        if (player == null) return fallback;
        var so = new SerializedObject(player);
        var prop = so.FindProperty(field);
        return prop != null && !string.IsNullOrEmpty(prop.stringValue) ? prop.stringValue : fallback;
    }

    private static float ReadPlayerFloat(MotherAnimationPlayer player, string field, float fallback)
    {
        if (player == null) return fallback;
        var so = new SerializedObject(player);
        var prop = so.FindProperty(field);
        return prop != null ? prop.floatValue : fallback;
    }

    /// <summary>UI 表示用：reachTimeoutSeconds を解決する（未解決なら実コード既定の 1s）。</summary>
    private float GetReachTimeoutSeconds()
    {
        var ctx = BuildContext();
        return ctx != null ? ctx.reachTimeout : 1f;
    }

    private static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        if (sm == null || string.IsNullOrEmpty(name)) return null;
        foreach (var child in sm.states)
        {
            if (child.state != null && child.state.name == name)
                return child.state;
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  検証 / 適用
    // ──────────────────────────────────────────────────────────────────────────

    private void RunValidate()
    {
        var ctx = BuildContext();
        if (ctx == null)
        {
            _reportText = "対象Controllerを解決できません。ParentGameScene を開くか、対象Controller を手動で設定してください。";
            _planText = "";
            return;
        }

        ValidateAndPlan(ctx);
        _reportText = FormatFindings(ctx);
        _planText = FormatPlan(ctx);
    }

    private void RunApply()
    {
        var ctx = BuildContext();
        if (ctx == null)
        {
            _reportText = "対象Controllerを解決できません。";
            return;
        }

        ValidateAndPlan(ctx);

        if (ctx.plan.Count == 0)
        {
            _reportText = FormatFindings(ctx);
            _planText = "変更はありません（既にセットアップ済みです）。";
            return;
        }

        // Controller への変更は Undo 対応で1回だけ登録する。
        Undo.RegisterCompleteObjectUndo(ctx.controller, "ゲームオーバー Animator セットアップ");

        var applied = new List<string>();
        foreach (var item in ctx.plan)
        {
            try
            {
                item.apply();
                applied.Add(item.description);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MotherGameOverAnimatorSetup] 適用に失敗: {item.description}\n{e}");
            }
        }

        EditorUtility.SetDirty(ctx.controller);
        AssetDatabase.SaveAssets();

        // 検証を再実行して最新状態を表示する。
        var reCtx = BuildContext();
        if (reCtx != null)
            ValidateAndPlan(reCtx);

        _reportText = (reCtx != null ? FormatFindings(reCtx) : "");
        _planText = $"適用しました（{applied.Count} 件）：\n" + string.Join("\n", applied.ConvertAll(s => "  ・" + s));
        Debug.Log($"[MotherGameOverAnimatorSetup] 適用完了 {applied.Count} 件: {ctx.controllerPath}");
    }

    // ── 検証と変更予定の生成 ────────────────────────────────────────────────

    private void ValidateAndPlan(SetupContext ctx)
    {
        ctx.findings.Clear();
        ctx.plan.Clear();

        var controller = ctx.controller;
        ctx.findings.Add(new Finding(Severity.Info, $"対象Controller: {ctx.controllerPath}"));
        if (ctx.player != null)
            ctx.findings.Add(new Finding(Severity.Info,
                $"MotherAnimationPlayer: '{ctx.player.name}' (scene={ctx.player.gameObject.scene.name}) から実ステート名を取得"));
        else
            ctx.findings.Add(new Finding(Severity.Warn,
                "MotherAnimationPlayer が読み込み中のシーンに無いため、ステート名は実コードの既定値で検証しています（ParentGameScene を開くと厳密に検証できます）"));

        if (controller.layers == null || controller.layers.Length == 0)
        {
            ctx.findings.Add(new Finding(Severity.Missing, "レイヤーが存在しません。"));
            return;
        }

        // レイヤー（コードは index 0 固定）
        var layer0 = controller.layers[0];
        ctx.findings.Add(layer0.name == "Base Layer"
            ? new Finding(Severity.Ok, $"Layer[0] = '{layer0.name}'（コードの Layer=0 と一致）")
            : new Finding(Severity.Warn, $"Layer[0] の名前が 'Base Layer' ではありません（'{layer0.name}'）。コードは index 0 を使うため名前は動作に影響しませんが要確認。"));
        if (controller.layers.Length > 1)
            ctx.findings.Add(new Finding(Severity.Info, $"レイヤーが {controller.layers.Length} 個あります（コードは Layer 0 のみ使用）。"));

        ValidateParameters(ctx);
        ValidateStatesAndMotions(ctx);
        ValidateTransitions(ctx);
        ValidateTransitionTiming(ctx);
        ValidateGameOverTransitions(ctx);
        ValidateNoAutoInterruption(ctx);
        ValidateDuplicates(ctx);
        ValidateClipSharingImpact(ctx);

        // クリップ選択（ユーザー操作）を反映する
        ValidateMotionChoice(ctx, ctx.walkFast, _walkFastMotionChoice, "Walk_Fast");
        ValidateMotionChoice(ctx, ctx.jumpScare, _jumpScareMotionChoice, "Jumpscare");
    }

    private void ValidateParameters(SetupContext ctx)
    {
        foreach (var (name, type) in RequiredParameters)
        {
            AnimatorControllerParameter found = null;
            foreach (var p in ctx.controller.parameters)
            {
                if (p.name == name) { found = p; break; }
            }

            if (found == null)
                ctx.findings.Add(new Finding(Severity.Missing, $"パラメーター '{name}'（{type}）がありません。"));
            else if (found.type != type)
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"パラメーター '{name}' の型が {found.type} です（コードは {type} を期待）。※勝手に削除・作り直しません。手動で確認してください。"));
            else
                ctx.findings.Add(new Finding(Severity.Ok, $"パラメーター '{name}'（{type}）OK"));
        }
    }

    private void ValidateStatesAndMotions(SetupContext ctx)
    {
        // コードが使うステート（必須）
        var required = new (string label, string name, string purpose)[]
        {
            ("Idle", ctx.idle, "通常待機"),
            ("Walk", ctx.walk, "通常歩き（WalkSpeed）"),
            ("Walk_Fast", ctx.walkFast, "早歩き（ゲームオーバー・Animator.Playで再生）"),
            ("Jumpscare", ctx.jumpScare, "ジャンプスケア（ゲームオーバー・ワンショット）"),
            ("Door_Open", ctx.doorOpen, "ドア開け（Door_OpenSpeed）"),
            ("Chore_Loop", ctx.choreLoop, "片付けループ"),
            ("Chore_Peek", ctx.chorePeek, "片付け視線"),
            ("Chore_End", ctx.choreEnd, "立ち上がり（ゲームオーバーD・ワンショット）"),
        };

        foreach (var (label, name, purpose) in required)
        {
            var st = ctx.State(name);
            if (st == null)
            {
                ctx.findings.Add(new Finding(Severity.Missing, $"ステート '{name}'（{label}: {purpose}）が見つかりません。"));
                continue;
            }

            var clip = st.motion as AnimationClip;
            if (st.motion == null)
                ctx.findings.Add(new Finding(Severity.Missing, $"ステート '{name}' の Motion が未設定です。"));
            else if (clip == null)
                ctx.findings.Add(new Finding(Severity.Warn, $"ステート '{name}' の Motion が AnimationClip ではありません（{st.motion.GetType().Name}）。"));
            else
            {
                string clipPath = AssetDatabase.GetAssetPath(clip);
                ctx.findings.Add(new Finding(Severity.Ok, $"ステート '{name}' Motion = '{clip.name}' ({clipPath})"));
                ReportTakeName(ctx, label, clip);
            }
        }

        // 速度パラメーターの割り当て（Walk_Fast / Door_Open は設定、Jumpscare は無効）
        var wf = ctx.State(ctx.walkFast);
        if (wf != null)
        {
            if (!wf.speedParameterActive || wf.speedParameter != "WalkSpeed")
            {
                ctx.findings.Add(new Finding(Severity.Missing,
                    $"'{ctx.walkFast}' の Speed Multiplier に 'WalkSpeed' が割り当てられていません（現在: active={wf.speedParameterActive}, param='{wf.speedParameter}'）。"));
                var target = wf;
                ctx.plan.Add(new PlanItem
                {
                    description = $"'{ctx.walkFast}' の Speed Multiplier を 'WalkSpeed' に設定",
                    apply = () => { target.speedParameterActive = true; target.speedParameter = "WalkSpeed"; }
                });
            }
            else ctx.findings.Add(new Finding(Severity.Ok, $"'{ctx.walkFast}' の Speed Multiplier = 'WalkSpeed' OK"));
        }

        var door = ctx.State(ctx.doorOpen);
        if (door != null)
        {
            if (!door.speedParameterActive || door.speedParameter != "Door_OpenSpeed")
                ctx.findings.Add(new Finding(Severity.Missing,
                    $"'{ctx.doorOpen}' の Speed Multiplier に 'Door_OpenSpeed' が割り当てられていません（現在: active={door.speedParameterActive}, param='{door.speedParameter}'）。"));
            else ctx.findings.Add(new Finding(Severity.Ok, $"'{ctx.doorOpen}' の Speed Multiplier = 'Door_OpenSpeed' OK"));
        }

        var js = ctx.State(ctx.jumpScare);
        if (js != null)
        {
            if (js.speedParameterActive)
            {
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"'{ctx.jumpScare}' の Speed Multiplier が有効です（'{js.speedParameter}'）。ワンショットはステートSpeed=1が原則です。"));
                var target = js;
                ctx.plan.Add(new PlanItem
                {
                    description = $"'{ctx.jumpScare}' の Speed Multiplier を無効化（ステートSpeed=1）",
                    apply = () => { target.speedParameterActive = false; }
                });
            }
            else ctx.findings.Add(new Finding(Severity.Ok, $"'{ctx.jumpScare}' の Speed Multiplier は無効（ステートSpeed=1）OK"));
        }

        // ループ設定：Walk_Fast=ON、Jumpscare/Door_Open/Chore_End=OFF
        ValidateClipLoop(ctx, ctx.walkFast, true);
        ValidateClipLoop(ctx, ctx.jumpScare, false);
        ValidateClipLoop(ctx, ctx.doorOpen, false);
        ValidateClipLoop(ctx, ctx.choreEnd, false);
    }

    /// <summary>クリップ名と元テイク名が違う場合は注記する（Motion の取り違え検知の補助）。</summary>
    private void ReportTakeName(SetupContext ctx, string label, AnimationClip clip)
    {
        if (!TryGetImporterLoop(clip, out _, out _, out _, out int index, out ModelImporterClipAnimation[] clips))
            return;
        if (index < 0 || clips == null) return;

        var entry = clips[index];
        if (!string.IsNullOrEmpty(entry.takeName) && entry.takeName != clip.name)
            ctx.findings.Add(new Finding(Severity.Info,
                $"'{label}' のクリップ '{clip.name}' の元テイク(takeName)='{entry.takeName}' です（名前と元アニメが異なります。意図どおりか確認してください）。"));
    }

    private void ValidateClipLoop(SetupContext ctx, string stateName, bool shouldLoop)
    {
        var st = ctx.State(stateName);
        var clip = st != null ? st.motion as AnimationClip : null;
        if (clip == null) return; // Motion 未設定は別項目で報告済み

        if (!TryGetImporterLoop(clip, out bool loop, out string modelPath, out _, out _, out _))
        {
            ctx.findings.Add(new Finding(Severity.Warn,
                $"'{stateName}' の Clip '{clip.name}' のインポート設定を取得できません（モデル以外のClipの可能性）。Loop={(shouldLoop ? "ON" : "OFF")} を手動確認してください。"));
            return;
        }

        if (loop == shouldLoop)
        {
            ctx.findings.Add(new Finding(Severity.Ok, $"'{stateName}' Clip '{clip.name}' Loop Time={loop} OK"));
            return;
        }

        ctx.findings.Add(new Finding(Severity.Missing,
            $"'{stateName}' Clip '{clip.name}' の Loop Time が {loop} です（{(shouldLoop ? "ON" : "OFF")} にすべき）。"));

        string impact = DescribeClipShare(ctx, clip);
        ctx.plan.Add(new PlanItem
        {
            description = $"Clip '{clip.name}' の Loop Time を {shouldLoop} に変更（{modelPath}）{impact}",
            apply = () => SetImporterClipLoop(modelPath, clip.name, shouldLoop)
        });
    }

    private string DescribeClipShare(SetupContext ctx, AnimationClip clip)
    {
        var users = new List<string>();
        foreach (var child in ctx.stateMachine.states)
        {
            if (child.state != null && child.state.motion == clip)
                users.Add(child.state.name);
        }
        return users.Count > 1 ? $"  ※'{clip.name}' は {string.Join(", ", users)} で共有（他のステートにも影響）" : "";
    }

    // ── 遷移の検証（ゲームオーバーに必要な Door_Open 到達性を含む） ──────────

    private const string PeekDoorState = "Peek_Door";
    private const string PeekWindowState = "Peek_Window";

    private void ValidateTransitions(SetupContext ctx)
    {
        // 通常挙動を維持するための遷移（表示のみ・自動追加しない）
        var specs = new List<TransitionSpec>
        {
            new TransitionSpec(ctx.idle, ctx.walk, CondKind.BoolTrue, "Walk"),
            new TransitionSpec(ctx.walk, ctx.idle, CondKind.BoolFalse, "Walk"),
            new TransitionSpec(ctx.idle, ctx.doorOpen, CondKind.Trigger, "Door_OpenTrigger"),
            new TransitionSpec(ctx.walk, ctx.doorOpen, CondKind.Trigger, "Door_OpenTrigger"),
            // ※ Peek_Door/Peek_Window → Door_Open は ValidateDoorOpenFromPeek で詳細（Duration/Offset等）と共に扱う
            new TransitionSpec(ctx.idle, ctx.choreLoop, CondKind.Trigger, "Chore_Start"),
            new TransitionSpec(ctx.walk, ctx.choreLoop, CondKind.Trigger, "Chore_Start"),
            new TransitionSpec(ctx.choreLoop, ctx.chorePeek, CondKind.Trigger, "Chore_Peek_Trigger"),
            new TransitionSpec(ctx.choreLoop, ctx.choreEnd, CondKind.BoolTrue, "Chore_ExitRequested"),
            new TransitionSpec(ctx.chorePeek, ctx.choreEnd, CondKind.BoolTrue, "Chore_ExitRequested"),
        };

        foreach (var spec in specs)
        {
            var from = ctx.State(spec.from);
            var to = ctx.State(spec.to);

            if (from == null || to == null)
            {
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"遷移 {spec.Describe()} を検証できません（ステート '{spec.from}' または '{spec.to}' が未検出）。"));
                continue;
            }

            if (HasTransition(from, to, spec.cond, spec.param))
                ctx.findings.Add(new Finding(Severity.Ok, $"遷移あり: {spec.Describe()}"));
            else
                ctx.findings.Add(new Finding(Severity.Missing,
                    $"遷移がありません: {spec.Describe()}（通常挙動のため自動追加しません。手動で確認してください）"));
        }
    }

    // ── 追加する遷移（Peek → Door_Open）の詳細検証 ──────────────────────────

    /// <summary>
    /// 追加遷移の Duration / Offset / 条件 / Exit Time と、reachTimeoutSeconds との整合・優先度を表示する。
    /// （この関数は表示のみ。実際の追加は ValidateDoorOpenFromPeekState の予定で行う）
    /// </summary>
    private void ValidateTransitionTiming(SetupContext ctx)
    {
        float requested = Mathf.Max(0f, _newTransitionDuration);
        float offset = Mathf.Max(0f, _newTransitionOffset);
        float safe = EffectiveTransitionDuration(ctx);

        ctx.findings.Add(new Finding(Severity.Info, "--- 追加する遷移の共通設定 ---"));

        if (requested >= ctx.reachTimeout)
            ctx.findings.Add(new Finding(Severity.Warn,
                $"Duration={requested:F3}s が reachTimeoutSeconds={ctx.reachTimeout:F2}s 以上です。安全側の {safe:F3}s で追加します。"));
        else
            ctx.findings.Add(new Finding(Severity.Ok,
                $"Duration={requested:F3}s < reachTimeoutSeconds={ctx.reachTimeout:F2}s"));

        ctx.findings.Add(new Finding(Severity.Info,
            $"追加遷移の共通値: Has Exit Time=OFF / Duration={safe:F3}s（固定秒） / " +
            $"Offset={offset:F3}（遷移先の正規化開始位置。0=先頭） / Has Fixed Duration=ON / InterruptionSource=None"));

        ctx.findings.Add(new Finding(Severity.Warn,
            "※ Duration のクランプは『追加遷移自体の所要時間の上限』であり、対象ステート到達時間の全体を保証しません。" +
            "進行中の既存遷移（Peek/Chore への遷移など）の完了待ちが加算される可能性があります（実プレイ未確認）。"));

        if (offset != 0f)
            ctx.findings.Add(new Finding(Severity.Warn,
                $"Offset={offset:F3}（0以外）です。遷移先を途中から再生します。今回の追加遷移は Offset=0（先頭から）を推奨します。"));
    }

    /// <summary>
    /// ゲームオーバー用の遷移（Idle中心）を検証し、不足を追加予定にする。
    ///  1) 捕獲開始状態 → Idle（GameOverActive==true ∧ GameOverStopTrigger）/ HasExitTime=OFF
    ///  2) Idle → Walk_Fast（GameOverActive==true ∧ WalkFast==true）/ HasExitTime=OFF
    ///  3) Walk_Fast → Idle（GameOverActive==true ∧ WalkFast==false）/ HasExitTime=OFF
    ///  4) Idle → Door_Open（既存 Door_OpenTrigger）/ 既存遷移を再利用
    ///  5) Idle → Jumpscare（GameOverActive==true ∧ JumpscareTrigger）/ HasExitTime=OFF
    ///  ※ ゲームオーバー用の Any State 遷移は使わない。
    /// </summary>
    private void ValidateGameOverTransitions(SetupContext ctx)
    {
        ctx.findings.Add(new Finding(Severity.Info, "--- ゲームオーバー遷移の検証（Idle中心）---"));

        if (ctx.State(ctx.idle) == null)
        {
            ctx.findings.Add(new Finding(Severity.Missing, $"停止状態（{ctx.idle}）が見つかりません。"));
            return;
        }

        var stopConds = new[] { ("GameOverActive", AnimatorConditionMode.If), ("GameOverStopTrigger", AnimatorConditionMode.If) };
        var walkFastConds = new[] { ("GameOverActive", AnimatorConditionMode.If), ("WalkFast", AnimatorConditionMode.If) };
        var stopFromFastConds = new[] { ("GameOverActive", AnimatorConditionMode.If), ("WalkFast", AnimatorConditionMode.IfNot) };
        var jumpConds = new[] { ("GameOverActive", AnimatorConditionMode.If), ("JumpscareTrigger", AnimatorConditionMode.If) };

        // 1) 捕獲開始状態 → Idle
        //    「実際に捕獲が発生しうる状態」を実コードから列挙する：
        //      Peek_Door/Peek_Window（通常覗き）・Chore_Peek/Chore_Loop（片付け）・Walk（接近中）・Door_Open（片付けドア開け）
        foreach (var from in new[] { PeekDoorState, PeekWindowState, ctx.chorePeek, ctx.choreLoop, ctx.walk, ctx.doorOpen })
            PlanStateTransition(ctx, from, ctx.idle, stopConds,
                $"{from} → Idle（GameOverActive==true ∧ GameOverStopTrigger）");

        // 2) Idle → Walk_Fast
        PlanStateTransition(ctx, ctx.idle, ctx.walkFast, walkFastConds,
            "Idle → Walk_Fast（GameOverActive==true ∧ WalkFast==true）");

        // 3) Walk_Fast → Idle
        PlanStateTransition(ctx, ctx.walkFast, ctx.idle, stopFromFastConds,
            "Walk_Fast → Idle（GameOverActive==true ∧ WalkFast==false）");

        // 4) Idle → Door_Open（既存 Door_OpenTrigger を再利用。無ければ不足として報告のみ）
        var idle = ctx.State(ctx.idle);
        var door = ctx.State(ctx.doorOpen);
        if (idle != null && door != null &&
            HasTransitionWithConditions(idle, door, new[] { ("Door_OpenTrigger", AnimatorConditionMode.If) }))
            ctx.findings.Add(new Finding(Severity.Ok, "既存遷移を再利用: Idle → Door_Open（Door_OpenTrigger）"));
        else
            ctx.findings.Add(new Finding(Severity.Missing,
                "Idle → Door_Open（Door_OpenTrigger）がありません（既存遷移を再利用する方針のため自動追加しません。手動確認してください）。"));

        // 5) Idle → Jumpscare
        PlanStateTransition(ctx, ctx.idle, ctx.jumpScare, jumpConds,
            "Idle → Jumpscare（GameOverActive==true ∧ JumpscareTrigger）");

        // 遷移図（線としてつながるか）の確認
        ctx.findings.Add(new Finding(Severity.Info,
            "遷移図: 捕獲開始(Peek/Chore/Walk/Door_Open) → Idle → Walk_Fast ⇄ Idle → Door_Open / Jumpscare。" +
            "Idle を中心に、Idle→Walk_Fast と Idle→Jumpscare が Animator 上で線としてつながっている必要があります。"));

        // 6) Jumpscare は自動復帰しない
        var js = ctx.State(ctx.jumpScare);
        if (js != null && js.transitions != null && js.transitions.Length > 0)
            ctx.findings.Add(new Finding(Severity.Warn,
                $"'{ctx.jumpScare}' に退出遷移が {js.transitions.Length} 本あります（通常状態へ自動復帰しないこと）。"));
    }

    /// <summary>指定ステート → 指定ステートの遷移を検証し、無ければ追加予定にする（Idle中心方式）。</summary>
    private void PlanStateTransition(SetupContext ctx, string fromName, string toName,
        (string param, AnimatorConditionMode mode)[] conditions, string label)
    {
        var from = ctx.State(fromName);
        var to = ctx.State(toName);
        if (from == null || to == null)
        {
            ctx.findings.Add(new Finding(Severity.Warn,
                $"ステート '{fromName}' または '{toName}' が見つからないため {label} を検証できません。"));
            return;
        }

        if (HasTransitionWithConditions(from, to, conditions))
        {
            ctx.findings.Add(new Finding(Severity.Ok, $"遷移あり: {label}"));
            return;
        }

        ctx.findings.Add(new Finding(Severity.Missing, $"遷移がありません: {label}"));
        float dur = EffectiveTransitionDuration(ctx);
        float off = Mathf.Max(0f, _newTransitionOffset);
        var f = from;
        var t = to;
        ctx.plan.Add(new PlanItem
        {
            description = $"遷移を追加: {label}（Has Exit Time=OFF / Duration={dur:F3}s / Offset={off:F3}）",
            apply = () => AddStateTransition(f, t, conditions, dur, off)
        });
    }

    // ── 追加遷移の生成ヘルパー ──────────────────────────────────────────────

    /// <summary>指定の条件セットを持つ from→to 遷移が既にあるか。</summary>
    private static bool HasTransitionWithConditions(AnimatorState from, AnimatorState to,
        (string param, AnimatorConditionMode mode)[] conditions)
    {
        if (from == null || to == null || from.transitions == null) return false;
        foreach (var t in from.transitions)
            if (t != null && t.destinationState == to && HasExactConditions(t, conditions)) return true;
        return false;
    }

    private static bool HasExactConditions(AnimatorStateTransition t,
        (string param, AnimatorConditionMode mode)[] conditions)
    {
        if (t.conditions == null || conditions == null) return false;
        if (t.conditions.Length != conditions.Length) return false;

        foreach (var (p, m) in conditions)
        {
            bool found = false;
            foreach (var c in t.conditions)
            {
                if (c.parameter == p && c.mode == m) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>指定ステート → 指定ステートの遷移を追加する（自己遷移は禁止）。</summary>
    private static void AddStateTransition(AnimatorState from, AnimatorState to,
        (string param, AnimatorConditionMode mode)[] conditions, float duration, float offset)
    {
        var t = from.AddTransition(to);
        ApplyTransitionSettings(t, conditions, duration, offset);
        t.canTransitionToSelf = false;
    }

    /// <summary>遷移の共通設定（Has Exit Time=OFF / 固定秒Duration / 正規化Offset / 条件）。</summary>
    private static void ApplyTransitionSettings(AnimatorStateTransition t,
        (string param, AnimatorConditionMode mode)[] conditions, float duration, float offset)
    {
        t.hasExitTime = false;
        t.exitTime = 0f;
        t.hasFixedDuration = true;
        t.duration = Mathf.Max(0f, duration);
        t.offset = Mathf.Max(0f, offset);
        t.interruptionSource = TransitionInterruptionSource.None;
        t.orderedInterruption = true;

        if (conditions != null)
        {
            foreach (var (p, m) in conditions)
                t.AddCondition(m, 0f, p);
        }
    }

    /// <summary>Duration を reachTimeoutSeconds 内に収まる安全な値へ丸める（表示と適用で同じ値を使う）。</summary>
    private float EffectiveTransitionDuration(SetupContext ctx)
    {
        float requested = Mathf.Max(0f, _newTransitionDuration);
        float maxSafe = Mathf.Max(0.01f, ctx.reachTimeout * 0.9f);
        return Mathf.Min(requested, maxSafe);
    }

    private static string DescribeConditions(AnimatorStateTransition t)
    {
        if (t.conditions == null || t.conditions.Length == 0) return "(なし)";
        var parts = new List<string>();
        foreach (var c in t.conditions)
            parts.Add($"{c.parameter} {c.mode} {c.threshold}");
        return string.Join(", ", parts);
    }

    private static string DescribeTransition(string from, string to, AnimatorStateTransition t)
    {
        // Duration: Has Fixed Duration=ON なら固定秒、OFF なら正規化時間（0〜1）。
        string durationText = t.hasFixedDuration ? $"{t.duration:F3}s（固定秒）" : $"{t.duration:F3}（正規化時間）";
        // Offset: 遷移先ステートの「正規化された開始位置」（秒ではない）。
        return $"{from} → {to} / 条件={DescribeConditions(t)} / Has Exit Time={t.hasExitTime}" +
               (t.hasExitTime ? $"(ExitTime={t.exitTime:F3})" : "") +
               $" / Duration={durationText} / Offset={t.offset:F3}（正規化開始位置）";
    }

    private static bool HasTransition(AnimatorState from, AnimatorState to, CondKind cond, string param)
    {
        if (from == null || to == null || from.transitions == null) return false;
        foreach (var t in from.transitions)
        {
            if (t == null || t.destinationState != to) continue;
            if (MatchesCondition(t, cond, param)) return true;
        }
        return false;
    }

    private static bool MatchesCondition(AnimatorStateTransition t, CondKind cond, string param)
    {
        if (cond == CondKind.None) return t.conditions == null || t.conditions.Length == 0;
        if (t.conditions == null) return false;
        foreach (var c in t.conditions)
        {
            if (c.parameter != param) continue;
            switch (cond)
            {
                case CondKind.Trigger:
                case CondKind.BoolTrue:
                    if (c.mode == AnimatorConditionMode.If) return true;
                    break;
                case CondKind.BoolFalse:
                    if (c.mode == AnimatorConditionMode.IfNot) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// ゲームオーバー用と「特定できる」Any State 遷移か。
    ///  ・宛先が Walk_Fast / Jumpscare / Idle で、かつゲームオーバー系パラメーターを条件に含むものに限定する。
    ///  ・通常用途の Any State 遷移は対象外（削除しない）。
    /// </summary>
    private static bool LooksLikeGameOverAnyState(AnimatorStateTransition t, SetupContext ctx)
    {
        if (t == null || t.destinationState == null) return false;

        string dest = t.destinationState.name;
        bool destIsGameOverState = dest == ctx.walkFast || dest == ctx.jumpScare || dest == ctx.idle;
        if (!destIsGameOverState) return false;

        return ConditionUsesAny(t, "GameOverActive")
               || ConditionUsesAny(t, "GameOverStopTrigger")
               || ConditionUsesAny(t, "WalkFast")
               || ConditionUsesAny(t, "JumpscareTrigger");
    }

    private static bool ConditionUsesAny(AnimatorStateTransition t, string parameterName)
    {
        if (t.conditions == null) return false;
        foreach (var c in t.conditions)
            if (c.parameter == parameterName) return true;
        return false;
    }

    // ── 自動割り込み・重複・共有の検証 ──────────────────────────────────────

    private void ValidateNoAutoInterruption(SetupContext ctx)
    {
        // 今回の Idle 中心方式では、ゲームオーバー用の Any State 遷移は使わない。
        //  ・既に追加済みの場合（ゲームオーバー用と特定できるもの）だけを削除対象として表示する。
        //  ・通常用途の Any State 遷移は削除しない。
        var any = ctx.stateMachine.anyStateTransitions;
        var gameOverAny = new List<AnimatorStateTransition>();
        if (any != null)
        {
            foreach (var t in any)
            {
                if (t == null || t.destinationState == null) continue;

                if (LooksLikeGameOverAnyState(t, ctx))
                    gameOverAny.Add(t);
                else
                    ctx.findings.Add(new Finding(Severity.Info,
                        $"通常用途の Any State 遷移: Any State → {t.destinationState.name}（条件={DescribeConditions(t)}）— 削除しません"));
            }
        }

        if (gameOverAny.Count == 0)
        {
            ctx.findings.Add(new Finding(Severity.Ok,
                "ゲームオーバー用の Any State 遷移はありません（Idle中心方式では使用しません）"));
        }
        else
        {
            foreach (var t in gameOverAny)
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"【削除対象】ゲームオーバー用 Any State 遷移: Any State → {t.destinationState.name}（条件={DescribeConditions(t)}）"));

            var sm = ctx.stateMachine;
            var list = gameOverAny;
            ctx.plan.Add(new PlanItem
            {
                description = $"ゲームオーバー用の Any State 遷移を削除（{gameOverAny.Count}件。通常用途の遷移は削除しません）",
                apply = () => { foreach (var t in list) sm.RemoveAnyStateTransition(t); }
            });
        }

        // Walk_Fast：退出遷移は「停止状態（Idle）へ戻る」ためのものだけを許可する。
        var wf = ctx.State(ctx.walkFast);
        if (wf != null && wf.transitions != null)
        {
            if (wf.transitions.Length == 0)
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"'{ctx.walkFast}' に退出遷移がありません（停止状態（Idle）への遷移が必要です）。"));
            else
                ctx.findings.Add(new Finding(Severity.Info,
                    $"'{ctx.walkFast}' の退出遷移={wf.transitions.Length}本（停止状態（Idle）への遷移のみが望ましい）"));
        }

        // Jumpscare：退出遷移なし（通常状態へ自動復帰しない）。
        var js = ctx.State(ctx.jumpScare);
        if (js != null)
        {
            if (js.transitions != null && js.transitions.Length > 0)
                ctx.findings.Add(new Finding(Severity.Warn,
                    $"'{ctx.jumpScare}' に退出遷移が {js.transitions.Length} 本あります（通常状態へ自動復帰しないこと）。"));
            else
                ctx.findings.Add(new Finding(Severity.Ok, $"'{ctx.jumpScare}' は退出遷移なし（自動復帰しない）OK"));
        }
    }

    private void ValidateDuplicates(SetupContext ctx)
    {
        var counts = new Dictionary<string, int>();
        foreach (var child in ctx.stateMachine.states)
        {
            if (child.state == null) continue;
            counts.TryGetValue(child.state.name, out int n);
            counts[child.state.name] = n + 1;
        }

        bool dup = false;
        foreach (var kv in counts)
        {
            if (kv.Value > 1)
            {
                dup = true;
                ctx.findings.Add(new Finding(Severity.Warn, $"同名ステート '{kv.Key}' が {kv.Value} 個あります（対象を特定できないため変更しません）。"));
            }
        }

        if (!dup) ctx.findings.Add(new Finding(Severity.Ok, "同名ステートの重複はありません"));
    }

    private void ValidateClipSharingImpact(SetupContext ctx)
    {
        foreach (var target in new[] { ctx.walkFast, ctx.jumpScare, ctx.doorOpen, ctx.choreEnd })
        {
            var st = ctx.State(target);
            var clip = st != null ? st.motion as AnimationClip : null;
            if (clip == null) continue;

            string share = DescribeClipShare(ctx, clip);
            if (!string.IsNullOrEmpty(share))
                ctx.findings.Add(new Finding(Severity.Warn, $"共有Clipの影響: '{target}' {share}"));
        }
    }

    /// <summary>ユーザーがクリップ選択を変更した場合だけ、Motion の変更を予定に加える。</summary>
    private void ValidateMotionChoice(SetupContext ctx, string stateName, AnimationClip choice, string label)
    {
        if (choice == null) return;

        var st = ctx.State(stateName);
        if (st == null) return;

        var current = st.motion as AnimationClip;
        if (current == choice) return;

        ctx.findings.Add(new Finding(Severity.Info,
            $"{label}: Motion を '{current?.name ?? "(未設定)"}' → '{choice.name}' に変更予定（ユーザー選択）"));

        var target = st;
        var c = choice;
        ctx.plan.Add(new PlanItem
        {
            description = $"'{stateName}' の Motion を '{c.name}' に変更",
            apply = () => { target.motion = c; }
        });
    }

    // ── ModelImporter クリップ設定の読み書き ────────────────────────────────

    private static ModelImporterClipAnimation[] GetImporterClips(ModelImporter importer)
    {
        var clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;
        return clips;
    }

    /// <summary>Clip が属するモデルのインポート定義から、Loop Time と該当インデックスを取得する。</summary>
    private static bool TryGetImporterLoop(AnimationClip clip, out bool loop, out string modelPath,
        out ModelImporter importer, out int index, out ModelImporterClipAnimation[] clips)
    {
        loop = false;
        index = -1;
        clips = null;
        modelPath = AssetDatabase.GetAssetPath(clip);
        importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return false;

        clips = GetImporterClips(importer);
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i].name == clip.name) { index = i; loop = clips[i].loopTime; return true; }
        }
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i].takeName == clip.name) { index = i; loop = clips[i].loopTime; return true; }
        }
        return false;
    }

    /// <summary>
    /// モデルの Clip の Loop Time を変更する。
    ///  ・他のクリップ定義は維持する（全件を保持したまま対象だけ編集する）。
    ///  ・元の値をログに残す（戻し方はログの指示で戻せる）。
    /// </summary>
    private static void SetImporterClipLoop(string modelPath, string clipName, bool loop)
    {
        if (AssetImporter.GetAtPath(modelPath) is not ModelImporter importer)
        {
            Debug.LogWarning($"[MotherGameOverAnimatorSetup] ModelImporter を取得できません: {modelPath}");
            return;
        }

        var clips = GetImporterClips(importer);
        bool found = false;
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i].name != clipName && clips[i].takeName != clipName) continue;

            var entry = clips[i];
            Debug.Log($"[MotherGameOverAnimatorSetup] {clipName} ({modelPath}): loopTime {entry.loopTime} → {loop}" +
                      $"（戻し方：ModelImporter の clipAnimations の該当 Clip の Loop Time を {entry.loopTime} に戻す）");
            entry.loopTime = loop;
            clips[i] = entry;
            found = true;
            break;
        }

        if (!found)
        {
            Debug.LogWarning($"[MotherGameOverAnimatorSetup] Clip '{clipName}' が {modelPath} のインポート定義に見つかりませんでした。");
            return;
        }

        importer.clipAnimations = clips; // 明示リストとして書き戻す（他の定義はそのまま）
        importer.SaveAndReimport();
    }

    // ── 表示用フォーマット ──────────────────────────────────────────────────

    private static string FormatFindings(SetupContext ctx)
    {
        int ok = 0, info = 0, warn = 0, miss = 0;
        foreach (var f in ctx.findings)
        {
            switch (f.severity)
            {
                case Severity.Ok: ok++; break;
                case Severity.Info: info++; break;
                case Severity.Warn: warn++; break;
                case Severity.Missing: miss++; break;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"=== 検証結果  OK={ok} 情報={info} 警告={warn} 不足={miss} ===");
        AppendGroup(sb, ctx.findings, Severity.Missing, "■ 不足（要対応）");
        AppendGroup(sb, ctx.findings, Severity.Warn, "■ 警告（要確認）");
        AppendGroup(sb, ctx.findings, Severity.Info, "■ 情報");
        AppendGroup(sb, ctx.findings, Severity.Ok, "■ OK");
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, List<Finding> findings, Severity sev, string header)
    {
        bool wroteHeader = false;
        foreach (var f in findings)
        {
            if (f.severity != sev) continue;
            if (!wroteHeader) { sb.AppendLine(); sb.AppendLine(header); wroteHeader = true; }
            sb.AppendLine("  ・" + f.text);
        }
    }

    private static string FormatPlan(SetupContext ctx)
    {
        if (ctx.plan.Count == 0) return "変更はありません（既にセットアップ済みです）。";

        var sb = new StringBuilder();
        sb.AppendLine($"{ctx.plan.Count} 件の変更を「適用」で書き込みます：");
        foreach (var p in ctx.plan)
            sb.AppendLine("  ・" + p.description);
        return sb.ToString();
    }
}

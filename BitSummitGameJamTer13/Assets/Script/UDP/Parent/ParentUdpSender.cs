        // 受理・適用判断は Coordinator（既存順序：AcceptsSnapshot → 受理可能な場合だけ Apply）。
        //  ・受理拒否は Apply 結果と混ぜない（この入口では検証済みフラグも更新しない）。
        ParentRankSyncCoordinator.SnapshotDecision decision =
            _rankSyncCoordinator.EvaluateSnapshot(snap, out RankSyncApply result);

        if (decision == ParentRankSyncCoordinator.SnapshotDecision.NotAccepted)
        {
            Debug.LogWarning($"[ParentUdpSender] RANK_SYNC ignored — source='{snap.SourceId}' does not match the confirmed peer ('{_peerGate.ConnectedSourceId}'). 接続確認前・旧同期元のデータは適用しません。");
            return;
        }
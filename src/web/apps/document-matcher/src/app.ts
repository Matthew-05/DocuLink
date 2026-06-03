import { ConfigView } from "./components/config-view/config-view.js";
import { ResultsView } from "./components/results-view/results-view.js";
import { initHostBridge, sendCreateLinks, sendStartMatching } from "./host-bridge.js";
import { runMatching } from "./matching-engine.js";
import type { MatcherDataLoadedPayload, MatcherReadyPayload, RowResult } from "./types/index.js";

export function mountApp(root: HTMLElement): void {
  root.className = "matcher-app";

  let configView: ConfigView | null = null;
  let resultsView: ResultsView | null = null;
  let activeOutputColNumbers: number[] = [];
  let rowResults: RowResult[] = [];

  initHostBridge({
    onMatcherReady(payload: MatcherReadyPayload) {
      configView = new ConfigView(
        root,
        payload.keyColumns,
        payload.outputColumns,
        payload.folders,
        payload.rowCount,
        {
          onStart(outputColNumbers, folderIds) {
            activeOutputColNumbers = outputColNumbers;
            rowResults = [];
            const total = Math.max(0, payload.rowCount - 1);
            configView?.remove();
            configView = null;

            resultsView = new ResultsView(root, total, {
              onClose() {
                window.close?.();
              },
            });

            sendStartMatching(outputColNumbers, folderIds);
          },
          onCancel() {
            window.close?.();
          },
        },
      );
    },

    async onMatcherDataLoaded(payload: MatcherDataLoadedPayload) {
      if (!resultsView) return;
      const view = resultsView;

      const requests = await runMatching(
        payload.pdfs,
        payload.rows,
        activeOutputColNumbers,
        (result: RowResult) => {
          rowResults.push(result);
          view.addRowResult(result);
        },
      );

      const matched = rowResults.filter((r) => r.status === "matched").length;
      const unmatched = rowResults.filter((r) => r.status === "unmatched").length;

      if (requests.length === 0) {
        view.setPhase("done", { matched, unmatched });
        return;
      }

      view.setPhase("creating");
      sendCreateLinks(requests);
    },

    onLinksCreated(_results) {
      if (!resultsView) return;
      const matched = rowResults.filter((r) => r.status === "matched").length;
      const unmatched = rowResults.filter((r) => r.status === "unmatched").length;
      resultsView.setPhase("done", { matched, unmatched });
    },
  });
}

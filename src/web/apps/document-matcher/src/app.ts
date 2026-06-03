import { StepFolders } from "./components/config-view/config-view.js";
import { ResultsView } from "./components/results-view/results-view.js";
import { StepIndicator } from "./components/step-indicator/step-indicator.js";
import { StepOutputColumns } from "./components/step-output-columns/step-output-columns.js";
import { StepSelectRanges } from "./components/step-select-ranges/step-select-ranges.js";
import {
  initHostBridge,
  sendCreateLinks,
  sendMatcherGeometryPrepared,
  sendMatcherLog,
  sendSelectionLocked,
  sendSelectionUnlocked,
  sendStartMatching,
} from "./host-bridge.js";
import { runMatching } from "./matching-engine.js";
import type {
  FolderInfo,
  MatcherDataLoadedPayload,
  MatcherReadyPayload,
  RowResult,
  SelectionInfo,
} from "./types/index.js";

type StepNumber = 1 | 2 | 3 | 4;

export function mountApp(root: HTMLElement): void {
  root.className = "matcher-app";
  root.innerHTML = "";

  const indicatorWrap = document.createElement("div");
  indicatorWrap.className = "matcher-app__indicator-wrap";
  root.appendChild(indicatorWrap);

  const stepContent = document.createElement("div");
  stepContent.className = "matcher-app__step-content";
  root.appendChild(stepContent);

  const indicator = new StepIndicator(indicatorWrap);

  let currentStep: StepNumber = 1;
  let selectionInfo: SelectionInfo | null = null;
  let folders: FolderInfo[] = [];
  let outputColNumbers: number[] = [];
  let rowResults: RowResult[] = [];

  let step1: StepSelectRanges | null = null;
  let step2: StepOutputColumns | null = null;
  let step3: StepFolders | null = null;
  let step4: ResultsView | null = null;

  function clearAll(): void {
    step1?.remove();
    step2?.remove();
    step3?.remove();
    step4?.remove();
    step1 = null;
    step2 = null;
    step3 = null;
    step4 = null;
  }

  function goToStep1(): void {
    if (!selectionInfo) return;
    clearAll();
    currentStep = 1;
    indicator.setStep(1);
    sendSelectionUnlocked();
    step1 = new StepSelectRanges(stepContent, selectionInfo, {
      onNext() {
        sendSelectionLocked();
        goToStep2();
      },
      onCancel: () => window.close?.(),
    });
  }

  function goToStep2(): void {
    if (!selectionInfo) return;
    clearAll();
    currentStep = 2;
    indicator.setStep(2);
    step2 = new StepOutputColumns(
      stepContent,
      selectionInfo.keyColumns,
      selectionInfo.outputColumns,
      {
        onBack: goToStep1,
        onNext(nums) {
          outputColNumbers = nums;
          goToStep3();
        },
      },
    );
  }

  function goToStep3(): void {
    clearAll();
    currentStep = 3;
    indicator.setStep(3);
    step3 = new StepFolders(stepContent, folders, {
      onBack: goToStep2,
      onStart(folderIds) {
        goToStep4(folderIds);
      },
    });
  }

  function goToStep4(folderIds: string[]): void {
    clearAll();
    currentStep = 4;
    indicator.setStep(4);
    rowResults = [];

    const total = Math.max(0, selectionInfo?.rowCount ?? 0);
    step4 = new ResultsView(stepContent, total, {
      onClose: () => window.close?.(),
    });

    sendStartMatching(outputColNumbers, folderIds);
  }

  initHostBridge({
    onMatcherReady(payload: MatcherReadyPayload) {
      selectionInfo = {
        rowCount: payload.rowCount,
        keyColumns: payload.keyColumns,
        outputColumns: payload.outputColumns,
      };
      folders = payload.folders;
      goToStep1();
    },

    onSelectionChanged(info: SelectionInfo) {
      if (currentStep !== 1) return;
      selectionInfo = info;
      step1?.update(info);
    },

    async onMatcherDataLoaded(payload: MatcherDataLoadedPayload) {
      if (!step4) return;
      const view = step4;

      try {
        sendMatcherLog(
          `matcher-data-loaded received rows=${payload.rows.length} pdfs=${payload.pdfs.length} outputs=${outputColNumbers.length}`,
        );
        sendMatcherLog(
          `matcher inputs geometryPdfs=${payload.pdfs.filter((pdf) => !!pdf.geometryBase64).length} preparePdfs=${payload.pdfs.filter((pdf) => !pdf.geometryBase64 && !!pdf.base64).length} keyCounts=${payload.rows.map((row) => row.keyValues.length).join(",")} keyLengths=${payload.rows.map((row) => row.keyValues.map((value) => value.length).join("/")).join(",")}`,
        );

        const requests = await runMatching(
          payload.pdfs,
          payload.rows,
          outputColNumbers,
          (result: RowResult) => {
            rowResults.push(result);
            view.addRowResult(result);
          },
          sendMatcherGeometryPrepared,
        );

        const matched = rowResults.filter((r) => r.status === "matched").length;
        const unmatched = rowResults.filter((r) => r.status === "unmatched").length;
        sendMatcherLog(`matching complete requests=${requests.length} matched=${matched} unmatched=${unmatched}`);

        if (requests.length === 0) {
          view.setPhase("done", { matched, unmatched });
          return;
        }

        view.setPhase("creating");
        sendMatcherLog(`sending create-links count=${requests.length}`);
        sendCreateLinks(requests);
      } catch (error) {
        const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
        sendMatcherLog(`matching failed ${message}`);
        view.setPhase("error");
      }
    },

    onLinksCreated() {
      if (!step4) return;
      const matched = rowResults.filter((r) => r.status === "matched").length;
      const unmatched = rowResults.filter((r) => r.status === "unmatched").length;
      step4.setPhase("done", { matched, unmatched });
    },
  });
}

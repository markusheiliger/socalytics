import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Typography from "@mui/material/Typography";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { AnalysisFactAdapter } from "../../../platform/adapters/analysisFactAdapter";
import type { FactStreamSummary } from "../../../platform/models/analysis";
import { FactStreamList } from "../FactStreamList";
import { formatMatchClock, formatMatchRange } from "../matchClock";
import { TimelineLane } from "./TimelineLane";
import {
  BINS_PER_VIEW,
  ZOOM_LEVELS,
  clampExtentStart,
  defaultZoom,
  deriveBins,
  moveExtentStart,
  overallExtent,
  zoomLevel,
  type ZoomId,
} from "./timelineBins";
import { deriveLaneState, laneAllowance } from "./timelineLane";
import { useLaneFacts, type LaneFactsView } from "./useLaneFacts";

/** The page state of the timeline; `zoom` stays undefined until the default zoom was chosen once (data-model.md). */
export interface TimelineView {
  zoom?: ZoomId;
  extentStartMs?: number;
}

interface AnalysisTimelineProps {
  adapter: AnalysisFactAdapter;
  runId: string;
  runState?: string;
  streams: readonly FactStreamSummary[];
  nodeStates?: Readonly<Record<string, string>>;
  view: TimelineView;
  onViewChange: (view: TimelineView) => void;
  onSelectStream: (nodeKey: string) => void;
  onShowFacts?: (nodeKey: string, fromMs: number, toMs: number) => void;
}

/** The selected range of one lane in absolute match time (data-model.md, Timeline Selection). */
interface TimelineSelection {
  nodeKey: string;
  fromMs: number;
  toMs: number;
}

interface LaneRunnerProps {
  adapter: AnalysisFactAdapter;
  runId: string;
  stream: FactStreamSummary;
  allowance: number;
  runState?: string;
  nodeState?: string;
  zoomId?: ZoomId;
  extentStartMs?: number;
  selection?: TimelineSelection;
  onSelect: (selection: TimelineSelection) => void;
  onClearSelection: () => void;
  onShowFacts?: (nodeKey: string, fromMs: number, toMs: number) => void;
  onReport: (nodeKey: string, view: LaneFactsView | undefined) => void;
  onAnnounce: (message: string) => void;
}

/** Loads one lane, reports its data to the timeline, and renders it. */
function LaneRunner({
  adapter,
  runId,
  stream,
  allowance,
  runState,
  nodeState,
  zoomId,
  extentStartMs,
  selection,
  onSelect,
  onClearSelection,
  onShowFacts,
  onReport,
  onAnnounce,
}: LaneRunnerProps) {
  const { t } = useTranslation("analysis");
  const lane = useLaneFacts(adapter, runId, stream, allowance);
  const { nodeKey } = stream;

  useEffect(() => {
    onReport(nodeKey, lane);
  }, [lane, nodeKey, onReport]);
  useEffect(() => () => onReport(nodeKey, undefined), [nodeKey, onReport]);

  const previous = useRef<{ status: string; updating: boolean } | undefined>(
    undefined,
  );
  useEffect(() => {
    const before = previous.current;
    previous.current = { status: lane.status, updating: lane.isUpdating };
    const capability = stream.capabilityId;
    if (lane.status === "unavailable" && before?.status !== "unavailable") {
      onAnnounce(
        t("timeline.live.laneUnavailable", {
          capability,
          interpolation: { escapeValue: false },
        }),
      );
    } else if (lane.status === "loaded" && before) {
      if (!before.updating && lane.isUpdating)
        onAnnounce(
          t("timeline.live.updating", {
            capability,
            interpolation: { escapeValue: false },
          }),
        );
      else if (before.status === "loading")
        onAnnounce(
          t("timeline.live.laneLoaded", {
            capability,
            interpolation: { escapeValue: false },
          }),
        );
      else if (before.updating && !lane.isUpdating)
        onAnnounce(t("timeline.live.updated"));
    }
  }, [lane.status, lane.isUpdating, stream.capabilityId, onAnnounce, t]);

  const effectiveRunState = runState ?? lane.runState;
  const state = deriveLaneState({
    status: lane.status,
    runState: effectiveRunState,
    loadedFactCount: lane.loadedFactCount,
    windows: lane.windows,
  });
  const bins = useMemo(
    () =>
      state === "ready" && zoomId !== undefined && extentStartMs !== undefined
        ? deriveBins({
            windows: lane.windows,
            facts: lane.facts,
            partial: lane.partial,
            extentStartMs,
            zoom: zoomLevel(zoomId),
          })
        : [],
    [state, zoomId, extentStartMs, lane.windows, lane.facts, lane.partial],
  );

  return (
    <TimelineLane
      stream={stream}
      state={state}
      bins={bins}
      loadedFactCount={lane.loadedFactCount}
      partial={lane.partial}
      updating={lane.isUpdating}
      windows={lane.windows}
      extentStartMs={extentStartMs}
      visibleMs={
        zoomId === undefined
          ? undefined
          : zoomLevel(zoomId).bins * zoomLevel(zoomId).binMs
      }
      onReload={lane.reload}
      runState={effectiveRunState}
      nodeState={nodeState}
      selection={selection}
      onSelect={(fromMs, toMs) => onSelect({ nodeKey, fromMs, toMs })}
      onClearSelection={onClearSelection}
      onShowFacts={(fromMs, toMs) => onShowFacts?.(nodeKey, fromMs, toMs)}
      onAnnounce={onAnnounce}
    />
  );
}

/** The timeline of a run: one lane per fact stream over a shared match-clock axis with zoom and extent controls (FR-001, FR-004, FR-008, FR-019). */
export function AnalysisTimeline({
  adapter,
  runId,
  runState,
  streams,
  nodeStates,
  view,
  onViewChange,
  onSelectStream,
  onShowFacts,
}: AnalysisTimelineProps) {
  const { t } = useTranslation("analysis");
  const [selection, setSelection] = useState<TimelineSelection | undefined>();
  const [reports, setReports] = useState<Record<string, LaneFactsView>>({});
  const [announcement, setAnnouncement] = useState("");
  const allowance = laneAllowance(streams.length);

  const report = useCallback(
    (nodeKey: string, laneView: LaneFactsView | undefined) => {
      setReports((current) => {
        if (laneView === undefined) {
          if (!(nodeKey in current)) return current;
          const { [nodeKey]: _removed, ...rest } = current;
          return rest;
        }
        return current[nodeKey] === laneView
          ? current
          : { ...current, [nodeKey]: laneView };
      });
    },
    [],
  );
  const announce = useCallback(
    (message: string) => setAnnouncement(message),
    [],
  );

  const settled = streams.every((stream) => {
    const lane = reports[stream.nodeKey];
    return lane !== undefined && lane.status !== "loading";
  });
  const extent = useMemo(
    () =>
      settled
        ? overallExtent(
            streams.flatMap((stream) => {
              const lane = reports[stream.nodeKey];
              return lane ? [{ windows: lane.windows, facts: lane.facts }] : [];
            }),
          )
        : undefined,
    [settled, streams, reports],
  );

  // The default zoom is chosen once, after every lane has left `loading`.
  useEffect(() => {
    if (view.zoom !== undefined || !settled || !extent) return;
    const zoom = defaultZoom(extent);
    onViewChange({
      zoom,
      extentStartMs: clampExtentStart(
        view.extentStartMs ?? extent.startMs,
        zoomLevel(zoom),
        extent,
      ),
    });
  }, [view, settled, extent, onViewChange]);

  const level = view.zoom === undefined ? undefined : zoomLevel(view.zoom);
  const extentStartMs =
    level && extent
      ? clampExtentStart(view.extentStartMs ?? extent.startMs, level, extent)
      : undefined;
  const levelText = level ? t(`timeline.zoom.${level.id}`) : "";
  const visibleEnd =
    level && extentStartMs !== undefined
      ? extentStartMs + level.bins * level.binMs
      : undefined;

  // A new run starts without a selection.
  useEffect(() => setSelection(undefined), [runId]);

  const clearSelection = useCallback(() => {
    setSelection(undefined);
    setAnnouncement(t("timeline.live.selectionCleared"));
  }, [t]);

  // The selection is stored in absolute match time and must stay fully visible.
  useEffect(() => {
    if (!selection || extentStartMs === undefined || visibleEnd === undefined)
      return;
    if (selection.fromMs < extentStartMs || selection.toMs > visibleEnd)
      clearSelection();
  }, [selection, extentStartMs, visibleEnd, clearSelection]);

  const chooseZoom = (id: ZoomId) => {
    if (!extent || id === view.zoom) return;
    setSelection(undefined);
    const next = zoomLevel(id);
    onViewChange({
      zoom: id,
      extentStartMs: clampExtentStart(
        extentStartMs ?? extent.startMs,
        next,
        extent,
      ),
    });
    setAnnouncement(
      t("timeline.live.zoomChanged", {
        level: t(`timeline.zoom.${id}`),
      }),
    );
  };
  const moveExtent = (direction: -1 | 1) => {
    if (!extent || !level || extentStartMs === undefined) return;
    const next = moveExtentStart(extentStartMs, direction, level, extent);
    onViewChange({ zoom: level.id, extentStartMs: next });
    setAnnouncement(
      t("timeline.live.extentChanged", {
        range: formatMatchRange(next, next + level.bins * level.binMs).text,
      }),
    );
  };

  const unavailable = settled && !extent;
  const lanes = streams.map((stream) => (
    <LaneRunner
      key={stream.nodeKey}
      adapter={adapter}
      runId={runId}
      stream={stream}
      allowance={allowance}
      runState={runState}
      nodeState={nodeStates?.[stream.nodeKey]}
      zoomId={level?.id}
      extentStartMs={extentStartMs}
      selection={selection?.nodeKey === stream.nodeKey ? selection : undefined}
      onSelect={setSelection}
      onClearSelection={clearSelection}
      onShowFacts={onShowFacts}
      onReport={report}
      onAnnounce={announce}
    />
  ));

  const tickEvery = 5;
  const ticks =
    level && extentStartMs !== undefined
      ? Array.from({ length: BINS_PER_VIEW / tickEvery + 1 }, (_, index) =>
          formatMatchClock(extentStartMs + index * tickEvery * level.binMs),
        )
      : [];

  return (
    <section aria-label={t("timeline.title")}>
      <Typography variant="h3" component="h3" sx={{ mt: 2 }}>
        {t("timeline.title")}
      </Typography>
      <div role="status" aria-live="polite">
        {announcement}
      </div>
      {unavailable && (
        <>
          <Typography role="alert">
            {t("timeline.extentUnavailable")}
          </Typography>
          <FactStreamList streams={[...streams]} onSelect={onSelectStream} />
        </>
      )}
      {!unavailable && level && extentStartMs !== undefined && extent && (
        <>
          <Box
            role="group"
            aria-label={t("timeline.zoom.label")}
            sx={{ display: "flex", gap: 1, flexWrap: "wrap", mt: 1 }}
          >
            {ZOOM_LEVELS.map((candidate) => (
              <Button
                key={candidate.id}
                size="small"
                variant={candidate.id === level.id ? "contained" : "outlined"}
                aria-pressed={candidate.id === level.id}
                onClick={() => chooseZoom(candidate.id)}
              >
                {t(`timeline.zoom.${candidate.id}`)}
              </Button>
            ))}
          </Box>
          <Typography component="p">
            {t("timeline.zoom.current", { level: levelText })}
          </Typography>
          <Box
            role="group"
            aria-label={t("timeline.extent.label")}
            sx={{ display: "flex", gap: 1, flexWrap: "wrap", mt: 1 }}
          >
            <Button
              size="small"
              variant="outlined"
              disabled={extentStartMs <= Math.floor(extent.startMs)}
              onClick={() => moveExtent(-1)}
            >
              {t("timeline.extent.earlier")}
            </Button>
            <Button
              size="small"
              variant="outlined"
              disabled={visibleEnd === undefined || visibleEnd >= extent.endMs}
              onClick={() => moveExtent(1)}
            >
              {t("timeline.extent.later")}
            </Button>
          </Box>
          <Typography component="p">
            {t("timeline.extent.value", {
              range: formatMatchRange(
                extentStartMs,
                visibleEnd ?? extentStartMs,
              ).text,
            })}
          </Typography>
          <Box
            aria-hidden="true"
            sx={{
              display: "flex",
              justifyContent: "space-between",
              fontSize: "0.75rem",
            }}
          >
            {ticks.map((tick, index) => (
              <span key={index}>{tick}</span>
            ))}
          </Box>
        </>
      )}
      <div hidden={unavailable}>{lanes}</div>
    </section>
  );
}

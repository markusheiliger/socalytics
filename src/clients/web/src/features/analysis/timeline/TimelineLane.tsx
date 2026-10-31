import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Typography from "@mui/material/Typography";
import {
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
} from "react";
import { useTranslation } from "react-i18next";
import type {
  CoverageWindow,
  FactStreamSummary,
} from "../../../platform/models/analysis";
import { formatMatchRange } from "../matchClock";
import { ProvenanceDetails } from "../ProvenanceDetails";
import type { BinState, DensityBin } from "./timelineBins";
import { unknownLaneStates, type LaneState } from "./timelineLane";

const GLYPHS: Record<BinState, string> = {
  facts: "●",
  analysedNoFacts: "○",
  notLoaded: "…",
  partial: "◐",
  notAnalysed: "×",
  noCoverage: "–",
};

const STRIPES =
  "repeating-linear-gradient(45deg, currentColor 0 2px, transparent 2px 6px)";
const DOTS = "radial-gradient(currentColor 1px, transparent 1px)";

// Each state has its own pattern besides its glyph and label, so colour is never the only signal.
const PATTERNS: Record<BinState, { image: string; size?: string }> = {
  facts: { image: "none" },
  analysedNoFacts: { image: "none" },
  notLoaded: { image: DOTS, size: "6px 6px" },
  partial: {
    image:
      "repeating-linear-gradient(90deg, currentColor 0 2px, transparent 2px 6px)",
  },
  notAnalysed: { image: STRIPES },
  noCoverage: { image: "none" },
};

interface WindowStyle {
  glyph: string;
  image: string;
  size?: string;
  outline: string;
}

// Coverage windows differ by glyph, pattern, and outline besides their state text.
const WINDOW_STYLES: Record<string, WindowStyle> = {
  accepted: { glyph: "✓", image: "none", outline: "1px solid currentColor" },
  missing: { glyph: "×", image: STRIPES, outline: "1px solid currentColor" },
  failed: {
    glyph: "!",
    image:
      "repeating-linear-gradient(135deg, currentColor 0 2px, transparent 2px 6px)",
    outline: "2px solid currentColor",
  },
  blocked: {
    glyph: "■",
    image:
      "repeating-linear-gradient(90deg, currentColor 0 2px, transparent 2px 6px)",
    outline: "1px solid currentColor",
  },
  skipped: {
    glyph: "»",
    image: DOTS,
    size: "6px 6px",
    outline: "1px dashed currentColor",
  },
};
const UNKNOWN_WINDOW_STYLE: WindowStyle = {
  glyph: "?",
  image: STRIPES,
  outline: "2px dotted currentColor",
};

// The review's existing wording is reused where one exists.
const LANE_STATE_TEXT: Partial<Record<LaneState, string>> = {
  inProgress: "states.inProgress",
  completeWithoutFacts: "states.completeWithoutFacts",
  noCoverage: "coverage.empty",
  capabilityNotInRun: "states.capabilityNotInRun",
  unavailable: "timeline.unavailable",
  error: "timeline.error",
  connectionRequired: "timeline.connectionRequired",
};

function binOutline(state: BinState): string {
  return state === "noCoverage"
    ? "1px dashed currentColor"
    : "1px solid currentColor";
}

interface TimelineLaneProps {
  stream: FactStreamSummary;
  state: LaneState;
  bins: DensityBin[];
  loadedFactCount: number;
  partial: boolean;
  updating?: boolean;
  windows?: readonly CoverageWindow[];
  extentStartMs?: number;
  visibleMs?: number;
  onReload?: () => void;
  runState?: string;
  nodeState?: string;
  selection?: { fromMs: number; toMs: number };
  onSelect?: (fromMs: number, toMs: number) => void;
  onClearSelection?: () => void;
  onShowFacts?: (fromMs: number, toMs: number) => void;
  onAnnounce?: (message: string) => void;
}

/** One lane: label, reported total, density bins with count, pattern, and glyph per state, and the lane detail (FR-005, FR-006, FR-009, FR-016). */
export function TimelineLane({
  stream,
  state,
  bins,
  loadedFactCount,
  partial,
  updating,
  windows = [],
  extentStartMs,
  visibleMs,
  onReload,
  runState,
  nodeState,
  selection,
  onSelect,
  onClearSelection,
  onShowFacts,
  onAnnounce,
}: TimelineLaneProps) {
  const { t } = useTranslation("analysis");
  const id = useId();
  const [detailOpen, setDetailOpen] = useState(false);
  const [focusIndex, setFocusIndex] = useState(0);
  const cells = useRef<(HTMLButtonElement | null)[]>([]);
  const anchor = useRef<number | undefined>(undefined);

  const capability = t("streams.capabilityValue", {
    id: stream.capabilityId,
    version: stream.capabilityVersion,
    interpolation: { escapeValue: false },
  });
  const raw = unknownLaneStates(runState, nodeState);
  const showBins = state === "ready";
  const stateLabel = t(`timeline.state.${state}`);

  const visibleWindows =
    extentStartMs === undefined || visibleMs === undefined
      ? []
      : windows.filter(
          (window) =>
            window.endMs > extentStartMs &&
            window.startMs < extentStartMs + visibleMs,
        );
  const windowState = (window: CoverageWindow) => {
    const key = window.state.toLowerCase();
    return key in WINDOW_STYLES
      ? t(`coverage.stateName.${key}`)
      : t("timeline.stateDescription.unknownState", {
          raw: window.state,
          interpolation: { escapeValue: false },
        });
  };
  const needsReload = state === "unavailable" || state === "error";

  const selectedBins = selection
    ? bins.filter(
        (bin) => bin.startMs >= selection.fromMs && bin.endMs <= selection.toMs,
      )
    : [];

  // A count is the sum of the selected bins' counts; other bins contribute their coverage state label.
  const describeSelection = (selected: DensityBin[]) => {
    const counted = selected.filter(
      (bin) => bin.state === "facts" || bin.state === "analysedNoFacts",
    );
    const parts: string[] = [];
    if (counted.length > 0) {
      const count = counted.reduce((sum, bin) => sum + bin.count, 0);
      parts.push(
        t(partial ? "timeline.atLeastCount" : "timeline.selection.factCount", {
          count,
        }),
      );
    }
    const labels = new Set(
      selected
        .filter((bin) => !counted.includes(bin))
        .map((bin) => t(`timeline.state.${bin.state}`)),
    );
    return [...parts, ...labels].join(". ");
  };

  const selectSpan = (fromIndex: number, toIndex: number) => {
    if (!onSelect) return;
    const low = Math.min(fromIndex, toIndex);
    const high = Math.max(fromIndex, toIndex);
    const fromMs = bins[low].startMs;
    const toMs = bins[high].endMs;
    onSelect(fromMs, toMs);
    onAnnounce?.(
      t("timeline.live.selected", {
        range: formatMatchRange(fromMs, toMs).text,
        capability,
        value: describeSelection(bins.slice(low, high + 1)),
        interpolation: { escapeValue: false },
      }),
    );
  };

  const chooseBin = (index: number, extend: boolean) => {
    const anchorIndex =
      extend && anchor.current !== undefined
        ? bins.findIndex((bin) => bin.startMs === anchor.current)
        : -1;
    if (anchorIndex >= 0) selectSpan(anchorIndex, index);
    else {
      anchor.current = bins[index].startMs;
      selectSpan(index, index);
    }
  };

  const moveFocus = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ) => {
    if (event.key === "Escape") {
      if (selection) {
        event.preventDefault();
        anchor.current = undefined;
        onClearSelection?.();
      }
      return;
    }
    let next: number | undefined;
    if (event.key === "ArrowRight") next = Math.min(bins.length - 1, index + 1);
    else if (event.key === "ArrowLeft") next = Math.max(0, index - 1);
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = bins.length - 1;
    if (next === undefined) return;
    event.preventDefault();
    setFocusIndex(next);
    cells.current[next]?.focus();
    if (event.shiftKey && next !== index) {
      if (anchor.current === undefined) anchor.current = bins[index].startMs;
      chooseBin(next, true);
    }
  };

  const binName = (bin: DensityBin) => {
    const range = formatMatchRange(bin.startMs, bin.endMs).label;
    const base =
      bin.state === "facts"
        ? t("timeline.binLabelCount", {
            range,
            state: t("timeline.state.facts"),
            count: bin.count,
            interpolation: { escapeValue: false },
          })
        : t("timeline.binLabel", {
            range,
            state: t(`timeline.state.${bin.state}`),
            interpolation: { escapeValue: false },
          });
    const unknown = bin.rawStates.map((value) =>
      t("timeline.stateDescription.unknownState", {
        raw: value,
        interpolation: { escapeValue: false },
      }),
    );
    return [base, ...unknown].join(". ");
  };

  return (
    <section
      aria-label={t("timeline.laneLabel", {
        capability,
        key: stream.nodeKey,
        interpolation: { escapeValue: false },
      })}
    >
      <Typography variant="h4" component="h4" sx={{ mt: 2 }}>
        {capability}
      </Typography>
      <Typography component="p">{stream.nodeKey}</Typography>
      <Typography component="p">
        {t("timeline.reportedTotal", { count: stream.factCount })}
      </Typography>
      {partial && showBins && (
        <Typography component="p">
          <strong>{t("timeline.partialMark")}</strong>
          {": "}
          {t("timeline.partialBasis", {
            loaded: loadedFactCount,
            reported: stream.factCount,
          })}
        </Typography>
      )}
      {updating && (
        <Typography component="p">{t("timeline.updating")}</Typography>
      )}
      {showBins &&
        visibleWindows.length > 0 &&
        extentStartMs !== undefined &&
        visibleMs !== undefined && (
          <Box
            component="ul"
            aria-label={t("timeline.coverageStrip.label", {
              capability,
              key: stream.nodeKey,
              interpolation: { escapeValue: false },
            })}
            sx={{
              position: "relative",
              listStyle: "none",
              m: 0,
              mb: "2px",
              p: 0,
              height: 24,
            }}
          >
            {visibleWindows.map((window, index) => {
              const style =
                WINDOW_STYLES[window.state.toLowerCase()] ??
                UNKNOWN_WINDOW_STYLE;
              const start = Math.max(window.startMs, extentStartMs);
              const end = Math.min(window.endMs, extentStartMs + visibleMs);
              const label = t("timeline.coverageStrip.window", {
                range: formatMatchRange(window.startMs, window.endMs).label,
                state: windowState(window),
                interpolation: { escapeValue: false },
              });
              return (
                <Box
                  component="li"
                  key={`${window.startMs}-${window.endMs}-${index}`}
                  aria-label={label}
                  title={label}
                  sx={{
                    position: "absolute",
                    top: 0,
                    bottom: 0,
                    left: `${((start - extentStartMs) / visibleMs) * 100}%`,
                    width: `${((end - start) / visibleMs) * 100}%`,
                    overflow: "hidden",
                    boxSizing: "border-box",
                    color: "text.primary",
                    backgroundColor: "background.paper",
                    backgroundImage: style.image,
                    backgroundSize: style.size,
                    border: style.outline,
                    fontSize: "0.75rem",
                    whiteSpace: "nowrap",
                  }}
                >
                  <span
                    aria-hidden="true"
                    style={{
                      background: "var(--mui-palette-background-paper, #fff)",
                      padding: "0 2px",
                    }}
                  >
                    {style.glyph} {windowState(window)}
                  </span>
                </Box>
              );
            })}
          </Box>
        )}
      {showBins ? (
        <Box
          role="group"
          aria-label={t("timeline.laneGroupLabel", {
            capability,
            key: stream.nodeKey,
            interpolation: { escapeValue: false },
          })}
          sx={{ display: "flex", gap: "2px", flexWrap: "nowrap" }}
        >
          {bins.map((bin, index) => {
            const pattern = PATTERNS[bin.state];
            return (
              <Box
                key={bin.startMs}
                component="button"
                type="button"
                ref={(element: HTMLButtonElement | null) => {
                  cells.current[index] = element;
                }}
                tabIndex={
                  index === Math.min(focusIndex, bins.length - 1) ? 0 : -1
                }
                aria-label={binName(bin)}
                aria-pressed={selectedBins.includes(bin)}
                onClick={(event: MouseEvent<HTMLButtonElement>) =>
                  chooseBin(index, event.shiftKey)
                }
                onFocus={() => setFocusIndex(index)}
                onKeyDown={(event: KeyboardEvent<HTMLButtonElement>) =>
                  moveFocus(event, index)
                }
                sx={{
                  flex: "1 1 0",
                  minWidth: 0,
                  minHeight: 32,
                  padding: 0,
                  color: "text.primary",
                  backgroundColor: "background.paper",
                  backgroundImage: pattern.image,
                  backgroundSize: pattern.size,
                  border: binOutline(bin.state),
                  ...(selectedBins.includes(bin) && {
                    outline: "3px double",
                    outlineOffset: "-3px",
                    fontWeight: "bold",
                  }),
                  fontSize: "0.75rem",
                  "&:focus-visible": {
                    outline: "3px solid",
                    outlineOffset: "2px",
                  },
                }}
              >
                <span
                  aria-hidden="true"
                  style={{
                    background: "var(--mui-palette-background-paper, #fff)",
                    padding: "0 2px",
                  }}
                >
                  {GLYPHS[bin.state]}
                  {bin.state === "facts" || bin.state === "analysedNoFacts"
                    ? bin.count
                    : ""}
                </span>
              </Box>
            );
          })}
        </Box>
      ) : (
        <>
          <Typography
            component="p"
            role={
              needsReload || state === "connectionRequired"
                ? "alert"
                : undefined
            }
          >
            {LANE_STATE_TEXT[state] ? t(LANE_STATE_TEXT[state]) : stateLabel}
          </Typography>
          {needsReload && onReload && (
            <Button size="small" onClick={onReload}>
              {t("timeline.reload")}
            </Button>
          )}
        </>
      )}
      {showBins && selection && selectedBins.length > 0 && (
        <Box role="group" aria-label={t("timeline.selection.label")}>
          <Typography component="p">
            {t("timeline.selection.range", {
              range: formatMatchRange(selection.fromMs, selection.toMs).text,
            })}
          </Typography>
          <Typography component="p">
            {describeSelection(selectedBins)}
          </Typography>
          <Button
            size="small"
            variant="contained"
            aria-label={t("timeline.selection.showFactsLabel", {
              key: stream.nodeKey,
              range: formatMatchRange(selection.fromMs, selection.toMs).label,
              interpolation: { escapeValue: false },
            })}
            onClick={() => onShowFacts?.(selection.fromMs, selection.toMs)}
          >
            {t("timeline.selection.showFacts")}
          </Button>
          <Button
            size="small"
            onClick={() => {
              anchor.current = undefined;
              onClearSelection?.();
            }}
          >
            {t("timeline.selection.clear")}
          </Button>
        </Box>
      )}
      <Button
        size="small"
        aria-expanded={detailOpen}
        aria-controls={`${id}-detail`}
        onClick={() => setDetailOpen((value) => !value)}
      >
        {t(
          detailOpen ? "timeline.laneDetail.hide" : "timeline.laneDetail.show",
          { key: stream.nodeKey, interpolation: { escapeValue: false } },
        )}
      </Button>
      {detailOpen && (
        <div id={`${id}-detail`}>
          <Typography component="p">{stateLabel}</Typography>
          {raw.runState !== undefined && (
            <Typography component="p">
              {t("timeline.stateDescription.unknownRunState", {
                raw: raw.runState,
                interpolation: { escapeValue: false },
              })}
            </Typography>
          )}
          {raw.nodeState !== undefined && (
            <Typography component="p">
              {t("timeline.laneDetail.unknownNodeState", {
                raw: raw.nodeState,
                interpolation: { escapeValue: false },
              })}
            </Typography>
          )}
          <ProvenanceDetails provenance={stream.provenance} />
        </div>
      )}
    </section>
  );
}

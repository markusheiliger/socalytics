import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Typography from "@mui/material/Typography";
import { useId, useRef, useState, type KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import type { FactStreamSummary } from "../../../platform/models/analysis";
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
  runState?: string;
  nodeState?: string;
}

/** One lane: label, reported total, density bins with count, pattern, and glyph per state, and the lane detail (FR-005, FR-006, FR-009, FR-016). */
export function TimelineLane({
  stream,
  state,
  bins,
  loadedFactCount,
  partial,
  updating,
  runState,
  nodeState,
}: TimelineLaneProps) {
  const { t } = useTranslation("analysis");
  const id = useId();
  const [detailOpen, setDetailOpen] = useState(false);
  const [focusIndex, setFocusIndex] = useState(0);
  const cells = useRef<(HTMLButtonElement | null)[]>([]);

  const capability = t("streams.capabilityValue", {
    id: stream.capabilityId,
    version: stream.capabilityVersion,
    interpolation: { escapeValue: false },
  });
  const raw = unknownLaneStates(runState, nodeState);
  const showBins = state === "ready";
  const stateLabel = t(`timeline.state.${state}`);

  const moveFocus = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ) => {
    let next: number | undefined;
    if (event.key === "ArrowRight") next = Math.min(bins.length - 1, index + 1);
    else if (event.key === "ArrowLeft") next = Math.max(0, index - 1);
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = bins.length - 1;
    if (next === undefined) return;
    event.preventDefault();
    setFocusIndex(next);
    cells.current[next]?.focus();
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
                tabIndex={index === focusIndex ? 0 : -1}
                aria-label={binName(bin)}
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
        <Typography component="p">{stateLabel}</Typography>
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

import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import { useTranslation } from "react-i18next";
import type { FactStreamSummary } from "../../../platform/models/analysis";
import { formatMatchRange } from "../matchClock";
import type { DensityBin } from "./timelineBins";

interface TimelineTextAlternativeProps {
  stream: FactStreamSummary;
  bins: readonly DensityBin[];
}

/** The text alternative of one lane: a table of the lane's bins from the same derived data as the graphic (FR-018). */
export function TimelineTextAlternative({
  stream,
  bins,
}: TimelineTextAlternativeProps) {
  const { t } = useTranslation("analysis");
  const capability = t("streams.capabilityValue", {
    id: stream.capabilityId,
    version: stream.capabilityVersion,
    interpolation: { escapeValue: false },
  });

  const value = (bin: DensityBin) => {
    const state = t(`timeline.state.${bin.state}`);
    const base = bin.state === "facts" ? `${state}, ${bin.count}` : state;
    const unknown = bin.rawStates.map((raw) =>
      t("timeline.stateDescription.unknownState", {
        raw,
        interpolation: { escapeValue: false },
      }),
    );
    return [base, ...unknown].join(". ");
  };

  return (
    <Table
      size="small"
      aria-label={t("timeline.textAlternative.tableLabel", {
        capability,
        key: stream.nodeKey,
        interpolation: { escapeValue: false },
      })}
    >
      <TableHead>
        <TableRow>
          <TableCell component="th" scope="col">
            {t("timeline.textAlternative.range")}
          </TableCell>
          <TableCell component="th" scope="col">
            {t("timeline.textAlternative.countOrState")}
          </TableCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {bins.map((bin) => (
          <TableRow key={bin.startMs}>
            <TableCell component="th" scope="row">
              {formatMatchRange(bin.startMs, bin.endMs).text}
            </TableCell>
            <TableCell>{value(bin)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

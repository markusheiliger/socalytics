import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Container from "@mui/material/Container";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";
import { Link, useSearchParams } from "react-router";
import { useConnectivity } from "../../connectivity/useConnectivity";
import { createOperationsAdapter } from "../../platform/adapters/operationsAdapter";
import { toPlatformFailure } from "../../platform/failure";
import type { NotificationReceipt } from "../../platform/models/operations";
import { useSession } from "../../session/sessionContext";
import { NotPermittedPage } from "../../shell/NotPermittedPage";
import { FailureAlert } from "../shared/FailureAlert";
import { CopyableValue } from "./CopyableValue";
import { enumerationText, formatTimestamp } from "./formatOperations";
import {
  OUTBOX_MESSAGE_TYPES,
  RECEIPT_OUTCOMES,
  parseReceiptFilters,
  writeReceiptFilters,
  type ReceiptFilters,
} from "./operationsFilters";
import { useContinuationPages } from "./useContinuationPages";

const KNOWN_TYPES: readonly string[] = OUTBOX_MESSAGE_TYPES;
const KNOWN_OUTCOMES: readonly string[] = RECEIPT_OUTCOMES;
const KNOWN_REJECTIONS: readonly string[] = [
  "schema-invalid",
  "lineage-not-found",
  "lineage-mismatch",
  "scope-mismatch",
];

/** Notification receipts, filtered by outcome from the address and paged with opaque tokens (FR-012, FR-013). */
export function NotificationReceiptsPage() {
  const { t } = useTranslation("operations");
  const { t: ts } = useTranslation("shell");
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createOperationsAdapter(platform), [platform]);
  const [params, setParams] = useSearchParams();
  const filters = useMemo(() => parseReceiptFilters(params), [params]);
  const filtered = filters.outcome !== undefined;

  const paging = useContinuationPages({
    queryKey: ["operations", "notification-receipts", filters.outcome ?? null],
    fetchPage: (token) => adapter.listNotificationReceipts(filters, token),
  });
  const failure = paging.error ? toPlatformFailure(paging.error) : undefined;

  if (failure?.kind === "notPermitted") return <NotPermittedPage />;

  const change = (next: ReceiptFilters) =>
    setParams(writeReceiptFilters(params, next));
  const items = paging.page?.items;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t("receipts.title")}</Typography>
      <Link to="/operations">{t("common.backToSummary")}</Link>

      {isUnreachable ? (
        <Alert severity="warning" role="alert" sx={{ mt: 2 }}>
          {ts("connectivity.connectionRequired")}
        </Alert>
      ) : (
        <>
          <Stack
            component="form"
            role="search"
            aria-label={t("filters.label")}
            direction="row"
            spacing={2}
            sx={{ mt: 2 }}
            onSubmit={(e) => e.preventDefault()}
          >
            <TextField
              select
              size="small"
              label={t("filters.outcome")}
              value={filters.outcome ?? ""}
              onChange={(e) =>
                change({
                  outcome:
                    e.target.value === ""
                      ? undefined
                      : (e.target.value as ReceiptFilters["outcome"]),
                })
              }
              slotProps={{
                select: { native: true },
                inputLabel: { shrink: true },
              }}
            >
              <option value="">{t("filters.all")}</option>
              {RECEIPT_OUTCOMES.map((value) => (
                <option key={value} value={value}>
                  {t(`outcome.${value}`)}
                </option>
              ))}
            </TextField>
            {filtered && (
              <Button onClick={() => change({})}>{t("filters.clear")}</Button>
            )}
          </Stack>

          {paging.expired && (
            <Alert severity="info" role="status" sx={{ mt: 2 }}>
              {t("common.positionExpired")}
            </Alert>
          )}

          {paging.isLoading && (
            <Typography sx={{ mt: 2 }}>{t("common.loading")}</Typography>
          )}

          {failure && (
            <Stack spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
              <FailureAlert failure={failure} />
              <Button
                variant="outlined"
                disabled={paging.isFetching}
                onClick={paging.refresh}
              >
                {t("common.tryAgain")}
              </Button>
            </Stack>
          )}

          {items && (
            <>
              <Stack
                direction="row"
                spacing={2}
                alignItems="center"
                sx={{ mt: 2 }}
              >
                <Button
                  variant="outlined"
                  disabled={paging.isFetching}
                  onClick={paging.refresh}
                >
                  {t("common.refresh")}
                </Button>
                <Typography role="status">
                  {paging.isFetching ? t("common.refreshing") : ""}
                </Typography>
              </Stack>

              {items.length === 0 ? (
                <Stack spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
                  <Typography>
                    {filtered
                      ? t("receipts.emptyFiltered")
                      : t("receipts.empty")}
                  </Typography>
                  {filtered && (
                    <Button onClick={() => change({})}>
                      {t("filters.clear")}
                    </Button>
                  )}
                </Stack>
              ) : (
                <ul
                  aria-label={t("receipts.listLabel")}
                  style={{ listStyle: "none", padding: 0 }}
                >
                  {items.map((receipt) => (
                    <ReceiptRow key={receipt.messageId} receipt={receipt} />
                  ))}
                </ul>
              )}

              <Stack direction="row" spacing={2} sx={{ mt: 2 }}>
                <Button
                  variant="outlined"
                  disabled={!paging.hasPrevious || paging.isFetching}
                  onClick={paging.previous}
                >
                  {t("common.previousPage")}
                </Button>
                <Button
                  variant="outlined"
                  disabled={!paging.hasNext || paging.isFetching}
                  onClick={paging.next}
                >
                  {t("common.nextPage")}
                </Button>
              </Stack>
            </>
          )}
        </>
      )}
    </Container>
  );
}

function ReceiptRow({ receipt }: { receipt: NotificationReceipt }) {
  const { t } = useTranslation("operations");
  const received = formatTimestamp(receipt.receivedAt);
  const absent = t("common.absent");
  const type =
    enumerationText(receipt.messageType, KNOWN_TYPES, (value) =>
      t(`messageType.${value}`),
    ) ?? absent;
  const outcome =
    enumerationText(receipt.outcome, KNOWN_OUTCOMES, (value) =>
      t(`outcome.${value}`),
    ) ?? absent;
  const rejection = enumerationText(
    receipt.rejectionCategory,
    KNOWN_REJECTIONS,
    (value) => t(`rejectionCategory.${value}`),
  );
  const identifier = (value: string | undefined) =>
    value ? <CopyableValue value={value} /> : absent;

  return (
    <li style={{ marginBottom: 16 }}>
      <dl style={{ margin: 0 }} aria-label={t("receipts.fields")}>
        <Field label={t("receipts.messageId")}>
          {identifier(receipt.messageId)}
        </Field>
        <Field label={t("receipts.messageType")}>{type}</Field>
        <Field label={t("receipts.recordingSetVersionId")}>
          {identifier(receipt.recordingSetVersionId)}
        </Field>
        <Field label={t("receipts.outcome")}>{outcome}</Field>
        {(rejection !== undefined || receipt.outcome === "rejected") && (
          <Field label={t("receipts.rejectionCategory")}>
            {rejection ?? absent}
          </Field>
        )}
        <Field label={t("receipts.runId")}>{identifier(receipt.runId)}</Field>
        <Field label={t("receipts.receivedAt")}>
          <span title={received?.iso}>{received?.text ?? absent}</span>
        </Field>
      </dl>
    </li>
  );
}

function Field({
  label,
  children,
}: {
  label: string;
  children: React.ReactNode;
}) {
  return (
    <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
      <dt style={{ fontWeight: 600 }}>{label}</dt>
      <dd style={{ margin: 0, overflowWrap: "anywhere" }}>{children}</dd>
    </div>
  );
}

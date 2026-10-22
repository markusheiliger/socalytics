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
import type { OutboxMessage } from "../../platform/models/operations";
import { useSession } from "../../session/sessionContext";
import { NotPermittedPage } from "../../shell/NotPermittedPage";
import { FailureAlert } from "../shared/FailureAlert";
import { enumerationText, formatTimestamp } from "./formatOperations";
import {
  OUTBOX_MESSAGE_TYPES,
  OUTBOX_STATES,
  parseOutboxFilters,
  writeOutboxFilters,
  type OutboxFilters,
} from "./operationsFilters";
import { useContinuationPages } from "./useContinuationPages";

/** Outgoing messages, filtered by state and message type from the address and paged with opaque tokens (FR-008, FR-009). */
export function OutboxMessagesPage() {
  const { t } = useTranslation("operations");
  const { t: ts } = useTranslation("shell");
  const { platform } = useSession();
  const { isUnreachable } = useConnectivity();
  const adapter = useMemo(() => createOperationsAdapter(platform), [platform]);
  const [params, setParams] = useSearchParams();
  const filters = useMemo(() => parseOutboxFilters(params), [params]);
  const filtered =
    filters.state !== undefined || filters.messageType !== undefined;

  const paging = useContinuationPages({
    queryKey: [
      "operations",
      "outbox-messages",
      filters.state ?? null,
      filters.messageType ?? null,
    ],
    fetchPage: (token) => adapter.listOutboxMessages(filters, token),
  });
  const failure = paging.error ? toPlatformFailure(paging.error) : undefined;

  if (failure?.kind === "notPermitted") return <NotPermittedPage />;

  const change = (next: OutboxFilters) =>
    setParams(writeOutboxFilters(params, next));
  const items = paging.page?.items;

  return (
    <Container component="main" maxWidth="md" sx={{ py: 4 }}>
      <Typography variant="h1">{t("messages.title")}</Typography>
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
              label={t("filters.state")}
              value={filters.state ?? ""}
              onChange={(e) =>
                change({
                  ...filters,
                  state:
                    e.target.value === ""
                      ? undefined
                      : (e.target.value as OutboxFilters["state"]),
                })
              }
              slotProps={{
                select: { native: true },
                inputLabel: { shrink: true },
              }}
            >
              <option value="">{t("filters.all")}</option>
              {OUTBOX_STATES.map((value) => (
                <option key={value} value={value}>
                  {t(`outboxState.${value}`)}
                </option>
              ))}
            </TextField>
            <TextField
              select
              size="small"
              label={t("filters.messageType")}
              value={filters.messageType ?? ""}
              onChange={(e) =>
                change({
                  ...filters,
                  messageType:
                    e.target.value === ""
                      ? undefined
                      : (e.target.value as OutboxFilters["messageType"]),
                })
              }
              slotProps={{
                select: { native: true },
                inputLabel: { shrink: true },
              }}
            >
              <option value="">{t("filters.all")}</option>
              {OUTBOX_MESSAGE_TYPES.map((value) => (
                <option key={value} value={value}>
                  {t(`messageType.${value}`)}
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
                      ? t("messages.emptyFiltered")
                      : t("messages.empty")}
                  </Typography>
                  {filtered && (
                    <Button onClick={() => change({})}>
                      {t("filters.clear")}
                    </Button>
                  )}
                </Stack>
              ) : (
                <ul
                  aria-label={t("messages.listLabel")}
                  style={{ listStyle: "none", padding: 0 }}
                >
                  {items.map((message) => (
                    <MessageRow key={message.messageId} message={message} />
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

const KNOWN_TYPES: readonly string[] = OUTBOX_MESSAGE_TYPES;
const KNOWN_STATES: readonly string[] = OUTBOX_STATES;

function MessageRow({ message }: { message: OutboxMessage }) {
  const { t } = useTranslation("operations");
  const created = formatTimestamp(message.createdAt);
  const type =
    enumerationText(message.messageType, KNOWN_TYPES, (value) =>
      t(`messageType.${value}`),
    ) ?? t("common.absent");
  const state =
    enumerationText(message.state, KNOWN_STATES, (value) =>
      t(`outboxState.${value}`),
    ) ?? t("common.absent");

  return (
    <li style={{ marginBottom: 12 }}>
      <Link
        to={`/operations/outbox-messages/${message.messageId}`}
        aria-label={t("messages.openMessage", { id: message.messageId })}
      >
        {type}
      </Link>{" "}
      <span>{state}</span>
      <div>
        {t("messages.attempts", { count: message.attemptCount })}
        {" · "}
        <span title={created?.iso}>
          {t("messages.created", {
            value: created?.text ?? t("common.absent"),
          })}
        </span>
        {message.failureCategory ? ` · ${message.failureCategory}` : ""}
      </div>
    </li>
  );
}

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { vi, type Mock } from "vitest";
import "../../../../src/i18n";
import { NotificationReceiptsPage } from "../../../../src/features/operations/NotificationReceiptsPage";
import { PlatformFailure } from "../../../../src/platform/failure";
import type {
  NotificationReceipt,
  Page,
  ReceiptOutcome,
} from "../../../../src/platform/models/operations";

const ID1 = "11111111-1111-4111-8111-111111111111";
const ID2 = "22222222-2222-4222-8222-222222222222";
const SET = "33333333-3333-4333-8333-333333333333";
const RUN = "44444444-4444-4444-8444-444444444444";

const receipt = (
  id: string,
  over: Partial<NotificationReceipt> = {},
): NotificationReceipt => ({
  messageId: id,
  messageType: "matches.recordings-finalized",
  recordingSetVersionId: SET,
  outcome: "run-created",
  runId: RUN,
  receivedAt: new Date("2030-01-01T09:00:00Z"),
  ...over,
});

type ListFn = (
  filter: { outcome?: ReceiptOutcome },
  token?: string,
) => Promise<Page<NotificationReceipt>>;
let listFn: Mock<ListFn>;

vi.mock("../../../../src/session/sessionContext", () => ({
  useSession: () => ({ platform: {}, status: "signedIn" }),
}));
vi.mock("../../../../src/platform/adapters/operationsAdapter", () => ({
  createOperationsAdapter: () => ({
    listNotificationReceipts: (f: object, t?: string) => listFn(f, t),
  }),
}));

function Location() {
  const location = useLocation();
  return (
    <output data-testid="location">
      {location.pathname + location.search}
    </output>
  );
}

function renderPage(entry = "/operations/notification-receipts") {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter initialEntries={[entry]}>
        <Location />
        <Routes>
          <Route
            path="/operations/notification-receipts"
            element={<NotificationReceiptsPage />}
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("NotificationReceiptsPage", () => {
  it("shows every receipt field with identifiers as plain copyable text", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [receipt(ID1)],
      continuationToken: undefined,
    }));
    renderPage();
    const list = await screen.findByRole("list", {
      name: "Notification receipts",
    });
    expect(list).toHaveTextContent(ID1);
    expect(list).toHaveTextContent("Recordings finalized");
    expect(list).toHaveTextContent(SET);
    expect(list).toHaveTextContent("Run created");
    expect(list).toHaveTextContent(RUN);
    expect(list).toHaveTextContent("Received");
    expect(within(list).queryAllByRole("link")).toHaveLength(0);
    expect(
      within(list).getByRole("button", { name: `Copy ${ID1}` }),
    ).toBeInTheDocument();
    expect(listFn).toHaveBeenCalledWith({}, undefined);
  });

  it("words a known rejection category and shows an unknown one raw", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [
        receipt(ID1, {
          outcome: "rejected",
          rejectionCategory: "lineage-not-found",
          runId: undefined,
        }),
        receipt(ID2, {
          outcome: "rejected",
          rejectionCategory: "quantum-flux",
          runId: undefined,
        }),
      ],
      continuationToken: undefined,
    }));
    renderPage();
    const list = await screen.findByRole("list", {
      name: "Notification receipts",
    });
    expect(list).toHaveTextContent(
      "The recording set version in the notification was not found",
    );
    expect(list).toHaveTextContent("quantum-flux");
    expect(list).toHaveTextContent("absent");
  });

  it("reads the outcome from the address, ignores unknown values, and writes changes back", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [receipt(ID1)],
      continuationToken: undefined,
    }));
    const user = userEvent.setup();
    renderPage("/operations/notification-receipts?outcome=bogus");
    await screen.findByText(ID1);
    expect(listFn).toHaveBeenCalledWith({}, undefined);
    await user.selectOptions(screen.getByLabelText("Outcome"), "rejected");
    expect(screen.getByTestId("location")).toHaveTextContent(
      "outcome=rejected",
    );
    expect(listFn).toHaveBeenLastCalledWith({ outcome: "rejected" }, undefined);
  });

  it("shows the filtered empty state and clears the filter", async () => {
    listFn = vi.fn<ListFn>(async (filter) => ({
      items: filter.outcome ? [] : [receipt(ID1)],
      continuationToken: undefined,
    }));
    const user = userEvent.setup();
    renderPage("/operations/notification-receipts?outcome=rejected");
    expect(
      await screen.findByText("No notification receipt matches"),
    ).toBeInTheDocument();
    await user.click(
      screen.getAllByRole("button", { name: "Clear filters" })[0],
    );
    expect(await screen.findByText(ID1)).toBeInTheDocument();
    expect(screen.getByTestId("location")).not.toHaveTextContent("outcome=");
  });

  it("shows the unfiltered empty state", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [],
      continuationToken: undefined,
    }));
    renderPage();
    expect(
      await screen.findByText("No notification receipts yet"),
    ).toBeInTheDocument();
  });

  it("pages forward and back and refreshes to the first page", async () => {
    listFn = vi.fn<ListFn>(async (_f, token) =>
      token === "t1"
        ? { items: [receipt(ID2)], continuationToken: undefined }
        : { items: [receipt(ID1)], continuationToken: "t1" },
    );
    const user = userEvent.setup();
    renderPage();
    await screen.findByText(ID1);
    expect(
      screen.getByRole("button", { name: "Previous page" }),
    ).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(await screen.findByText(ID2)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Next page" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Previous page" }));
    expect(await screen.findByText(ID1)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    await screen.findByText(ID2);
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    expect(await screen.findByText(ID1)).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Previous page" }),
    ).toBeDisabled();
  });

  it("shows the not-permitted view for a 403", async () => {
    listFn = vi
      .fn<ListFn>()
      .mockRejectedValue(new PlatformFailure("notPermitted", { status: 403 }));
    renderPage();
    expect(await screen.findByRole("alert")).toHaveTextContent(
      /permission|not permitted|not allowed/i,
    );
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("shows a recoverable error with Try again", async () => {
    listFn = vi
      .fn<ListFn>()
      .mockRejectedValueOnce(new PlatformFailure("unexpected", { status: 500 }))
      .mockResolvedValue({
        items: [receipt(ID1)],
        continuationToken: undefined,
      });
    const user = userEvent.setup();
    renderPage();
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText(ID1)).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

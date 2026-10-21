import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { vi, type Mock } from "vitest";
import "../../../../src/i18n";
import { OutboxMessagesPage } from "../../../../src/features/operations/OutboxMessagesPage";
import { PlatformFailure } from "../../../../src/platform/failure";
import type {
  OutboxMessage,
  OutboxMessageType,
  OutboxState,
  Page,
} from "../../../../src/platform/models/operations";

const message = (
  id: string,
  over: Partial<OutboxMessage> = {},
): OutboxMessage => ({
  messageId: id,
  messageType: "analysis.ready-work",
  contractVersion: "v1",
  subject: "subject",
  state: "failed",
  attemptCount: 3,
  publicationRound: 1,
  createdAt: new Date("2030-01-01T09:00:00Z"),
  ageSeconds: 10,
  failureCategory: "broker-unavailable",
  ...over,
});

const ID1 = "11111111-1111-4111-8111-111111111111";
const ID2 = "22222222-2222-4222-8222-222222222222";

type ListFn = (
  filter: { state?: OutboxState; messageType?: OutboxMessageType },
  token?: string,
) => Promise<Page<OutboxMessage>>;
let listFn: Mock<ListFn>;

vi.mock("../../../../src/session/sessionContext", () => ({
  useSession: () => ({ platform: {}, status: "signedIn" }),
}));
vi.mock("../../../../src/platform/adapters/operationsAdapter", () => ({
  createOperationsAdapter: () => ({
    listOutboxMessages: (f: object, t?: string) => listFn(f, t),
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

function renderPage(entry = "/operations/outbox-messages") {
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter initialEntries={[entry]}>
        <Location />
        <Routes>
          <Route
            path="/operations/outbox-messages"
            element={<OutboxMessagesPage />}
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("OutboxMessagesPage", () => {
  it("lists messages with type, state, attempts, creation time, and failure category linking to the detail", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [message(ID1)],
      continuationToken: undefined,
    }));
    renderPage();
    const link = await screen.findByRole("link", {
      name: `Open message ${ID1}`,
    });
    expect(link).toHaveAttribute("href", `/operations/outbox-messages/${ID1}`);
    expect(link).toHaveTextContent("Ready work");
    const list = screen.getByRole("list", { name: "Outgoing messages" });
    expect(list).toHaveTextContent("Failed");
    expect(list).toHaveTextContent("Attempts: 3");
    expect(list).toHaveTextContent("Created");
    expect(list).toHaveTextContent("broker-unavailable");
    expect(listFn).toHaveBeenCalledWith({}, undefined);
  });

  it("reads filters from the address, ignores unknown values, and writes changes back", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [message(ID1)],
      continuationToken: undefined,
    }));
    const user = userEvent.setup();
    renderPage("/operations/outbox-messages?state=failed&messageType=bogus");
    await screen.findByRole("link", { name: `Open message ${ID1}` });
    expect(listFn).toHaveBeenCalledWith({ state: "failed" }, undefined);
    await user.selectOptions(
      screen.getByLabelText("Message type"),
      "analysis.ready-work",
    );
    expect(screen.getByTestId("location")).toHaveTextContent("state=failed");
    expect(screen.getByTestId("location")).toHaveTextContent(
      "messageType=analysis.ready-work",
    );
    expect(listFn).toHaveBeenLastCalledWith(
      { state: "failed", messageType: "analysis.ready-work" },
      undefined,
    );
  });

  it("shows the filtered empty state and clears the filters", async () => {
    listFn = vi.fn<ListFn>(async (filter) => ({
      items: filter.state ? [] : [message(ID1)],
      continuationToken: undefined,
    }));
    const user = userEvent.setup();
    renderPage("/operations/outbox-messages?state=pending");
    expect(
      await screen.findByText("No outgoing message matches"),
    ).toBeInTheDocument();
    await user.click(
      screen.getAllByRole("button", { name: "Clear filters" })[0],
    );
    expect(
      await screen.findByRole("link", { name: `Open message ${ID1}` }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("location")).toHaveTextContent(
      "/operations/outbox-messages",
    );
    expect(screen.getByTestId("location")).not.toHaveTextContent("state=");
  });

  it("shows the unfiltered empty state", async () => {
    listFn = vi.fn<ListFn>(async () => ({
      items: [],
      continuationToken: undefined,
    }));
    renderPage();
    expect(
      await screen.findByText("No outgoing messages yet"),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: "Clear filters" }),
    ).not.toBeInTheDocument();
  });

  it("pages forward and back and refreshes to the first page", async () => {
    listFn = vi.fn<ListFn>(async (_f, token) =>
      token === "t1"
        ? { items: [message(ID2)], continuationToken: undefined }
        : { items: [message(ID1)], continuationToken: "t1" },
    );
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole("link", { name: `Open message ${ID1}` });
    expect(
      screen.getByRole("button", { name: "Previous page" }),
    ).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(
      await screen.findByRole("link", { name: `Open message ${ID2}` }),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Next page" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Previous page" }));
    expect(
      await screen.findByRole("link", { name: `Open message ${ID1}` }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    await screen.findByRole("link", { name: `Open message ${ID2}` });
    await user.click(screen.getByRole("button", { name: "Refresh" }));
    expect(
      await screen.findByRole("link", { name: `Open message ${ID1}` }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Previous page" }),
    ).toBeDisabled();
  });

  it("explains an expired position and shows the first page", async () => {
    listFn = vi.fn<ListFn>(async (_f, token) => {
      if (token === "t1") {
        throw new PlatformFailure("programmingError", {
          status: 400,
          fieldErrors: [{ field: "continuationToken", code: "invalid" }],
        });
      }
      return { items: [message(ID1)], continuationToken: "t1" };
    });
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole("link", { name: `Open message ${ID1}` });
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(
      await screen.findByText(
        "The list position expired; showing the first page again",
      ),
    ).toBeInTheDocument();
    expect(
      await screen.findByRole("link", { name: `Open message ${ID1}` }),
    ).toBeInTheDocument();
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
        items: [message(ID1)],
        continuationToken: undefined,
      });
    const user = userEvent.setup();
    renderPage();
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Try again" }));
    expect(
      await screen.findByRole("link", { name: `Open message ${ID1}` }),
    ).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

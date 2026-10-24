import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import {
  createInvitationAdapter,
  toInvitation,
} from "../../../../src/platform/adapters/invitationAdapter";
import { PlatformFailure } from "../../../../src/platform/failure";
import { bodyOf, ok, problem, setup } from "../../platform/adapters/support";
import { fakeAdapter, makeSession, renderApp } from "../../session/helpers";

const TEAM = "00000000-0000-4000-8000-000000000003";
const INVITATION = "00000000-0000-4000-8000-0000000000e1";
const team = {
  id: TEAM,
  seasonId: "00000000-0000-4000-8000-000000000002",
  seasonState: "Active",
  name: "First Team",
  createdAt: "2026-10-12T12:00:00Z",
  version: 1,
};
const dto = {
  id: INVITATION,
  state: "pending",
  address: "new@example.org",
  accountName: "jdoe",
  teamRoles: [{ teamId: TEAM, teamName: "First Team", role: "viewer" }],
  version: 1,
};

const json = (
  status: number,
  body?: unknown,
  headers: Record<string, string> = {},
) =>
  new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: {
      "Content-Type":
        status >= 400 ? "application/problem+json" : "application/json",
      ...headers,
    },
  });

type Call = [string | Request, RequestInit];
const fetchCalls = () =>
  (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls as Call[];
const urlOf = (c: Call) => (typeof c[0] === "string" ? c[0] : c[0].url);
const sentBody = (c: Call | undefined) =>
  JSON.parse(new TextDecoder().decode(c?.[1].body as ArrayBuffer));
const callTo = (operation: string) =>
  fetchCalls().find((c) =>
    new RegExp(`/api/v1/invitations/${operation}$`).test(urlOf(c)),
  );
const invitationCreated = () =>
  fetchCalls().find(
    (c) => /\/api\/v1\/invitations$/.test(urlOf(c)) && c[1].method === "POST",
  );

const stubFetch = (handler: (url: string, init: RequestInit) => Response) =>
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string | Request, init: RequestInit = {}) =>
      handler(typeof input === "string" ? input : input.url, init),
    ),
  );

const signedOut = () =>
  fakeAdapter({
    restore: vi
      .fn()
      .mockRejectedValue(new PlatformFailure("sessionEnded", { status: 401 })),
  });
const admin = () =>
  fakeAdapter({
    restore: vi
      .fn()
      .mockResolvedValue(makeSession({ clubRoles: ["ClubAdmin"] })),
  });

afterEach(() => vi.unstubAllGlobals());

describe("invitation adapter mapping", () => {
  it("maps an invitation with kebab-case roles to the application model", async () => {
    const { api, calls } = setup(() => ok(201, dto));
    const result = await createInvitationAdapter(api).createInvitation(
      "new@example.org",
      "jdoe",
      [{ teamId: TEAM, role: "Viewer" }],
    );
    expect(result).toEqual({
      ok: true,
      value: {
        id: INVITATION,
        state: "pending",
        address: "new@example.org",
        accountName: "jdoe",
        teamRoles: [{ teamId: TEAM, role: "Viewer" }],
        version: 1,
      },
    });
    expect(bodyOf(calls[0])).toEqual({
      address: "new@example.org",
      accountName: "jdoe",
      teamRoles: [{ teamId: TEAM, role: "viewer" }],
    });
  });

  it("maps the conflicts, field problems, the neutral invalid answer, and the wait time", async () => {
    const adapter = (r: () => Response) =>
      createInvitationAdapter(setup(r).api);
    expect(
      await adapter(() =>
        problem(409, "email-address-in-use"),
      ).createInvitation("a@b.c", "x", []),
    ).toEqual({ ok: false, kind: "emailAddressInUse" });
    expect(
      await adapter(() => problem(409, "account-name-taken")).createInvitation(
        "a@b.c",
        "x",
        [],
      ),
    ).toEqual({ ok: false, kind: "accountNameTaken" });
    expect(
      await adapter(() =>
        problem(400, "validation-failed", [
          { field: "address", code: "required" },
        ]),
      ).createInvitation("", "x", []),
    ).toEqual({
      ok: false,
      kind: "fieldErrors",
      fieldErrors: [{ field: "address", code: "required" }],
    });
    expect(
      await adapter(() => problem(400, "invitation-invalid")).checkInvitation(
        "t",
      ),
    ).toEqual({ ok: false, kind: "invalid" });
    expect(
      await adapter(() =>
        ok(200, { clubName: "Club", accountName: "jdoe" }),
      ).checkInvitation("t"),
    ).toEqual({
      ok: true,
      value: { clubName: "Club", accountName: "jdoe" },
    });
    expect(
      await adapter(() => ok(204)).acceptInvitation("t", "Secret-123"),
    ).toEqual({ ok: true, value: undefined });
    expect(
      await adapter(() =>
        json(429, { status: 429 }, { "Retry-After": "30" }),
      ).checkInvitation("t"),
    ).toEqual({
      ok: false,
      kind: "rateLimited",
      retryAfterSeconds: 30,
    });
  });

  it("rejects an incomplete invitation as incompatible", () => {
    expect(() => toInvitation({ id: INVITATION } as never)).toThrow(
      PlatformFailure,
    );
  });
});

describe("invite member dialog", () => {
  const platform = (invite: () => Response = () => json(201, dto)) =>
    stubFetch((url, init) => {
      if (/\/api\/v1\/invitations$/.test(url) && init.method === "POST")
        return invite();
      if (/\/api\/v1\/teams/.test(url))
        return json(200, { items: [team], continuationToken: null });
      if (/\/api\/v1\/members/.test(url))
        return json(200, { items: [], continuationToken: null });
      return json(404, { status: 404 });
    });

  const open = async () => {
    renderApp(admin(), "/members");
    await userEvent.click(
      await screen.findByRole("button", { name: "Invite member" }),
    );
    return within(
      await screen.findByRole("dialog", { name: "Invite a member" }),
    );
  };

  const fill = async (dialog: ReturnType<typeof within>) => {
    await userEvent.type(
      dialog.getByLabelText(/email address/i),
      "  new@example.org ",
    );
    await userEvent.type(dialog.getByLabelText(/sign-in name/i), "jdoe");
  };

  it("sends the trimmed values with the team-role rows and confirms the invitation", async () => {
    platform();
    const dialog = await open();
    await fill(dialog);
    await waitFor(() =>
      expect(
        dialog.getByRole("button", { name: "Add team role" }),
      ).toBeEnabled(),
    );
    await userEvent.click(
      dialog.getByRole("button", { name: "Add team role" }),
    );
    await userEvent.selectOptions(dialog.getByLabelText("Role"), "Viewer");
    await userEvent.click(
      dialog.getByRole("button", { name: "Send invitation" }),
    );
    expect(
      await screen.findByText("The invitation was sent to new@example.org."),
    ).toBeInTheDocument();
    expect(sentBody(invitationCreated())).toEqual({
      address: "new@example.org",
      accountName: "jdoe",
      teamRoles: [{ teamId: TEAM, role: "viewer" }],
    });
    await waitFor(() =>
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument(),
    );
  });

  it("adds and removes team-role rows", async () => {
    platform();
    const dialog = await open();
    await waitFor(() =>
      expect(
        dialog.getByRole("button", { name: "Add team role" }),
      ).toBeEnabled(),
    );
    await userEvent.click(
      dialog.getByRole("button", { name: "Add team role" }),
    );
    expect(dialog.getByLabelText("Team")).toHaveValue(TEAM);
    await userEvent.click(
      dialog.getByRole("button", { name: "Remove team role 1" }),
    );
    expect(dialog.queryByLabelText("Team")).not.toBeInTheDocument();
  });

  it("shows field problems at the fields", async () => {
    platform(() =>
      json(400, {
        status: 400,
        code: "validation-failed",
        errors: [{ field: "address", code: "too-long" }],
      }),
    );
    const dialog = await open();
    await fill(dialog);
    await userEvent.click(
      dialog.getByRole("button", { name: "Send invitation" }),
    );
    expect(
      await dialog.findByText("This value is too long."),
    ).toBeInTheDocument();
  });

  it("shows the email address conflict and keeps the dialog open", async () => {
    platform(() => json(409, { status: 409, code: "email-address-in-use" }));
    const dialog = await open();
    await fill(dialog);
    await userEvent.click(
      dialog.getByRole("button", { name: "Send invitation" }),
    );
    expect(
      await dialog.findByText(/email address is already in use/),
    ).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("shows the account name conflict", async () => {
    platform(() => json(409, { status: 409, code: "account-name-taken" }));
    const dialog = await open();
    await fill(dialog);
    await userEvent.click(
      dialog.getByRole("button", { name: "Send invitation" }),
    );
    expect(
      await dialog.findByText(/sign-in name is already taken/),
    ).toBeInTheDocument();
  });
});

describe("invitation page", () => {
  const usable = (acceptance: () => Response = () => json(204)) =>
    stubFetch((url) => {
      if (/invitations\/checks$/.test(url))
        return json(200, { clubName: "Riverside RC", accountName: "jdoe" });
      if (/invitations\/acceptances$/.test(url)) return acceptance();
      return json(404, { status: 404 });
    });

  const fill = async (password: string, confirmation: string) => {
    await userEvent.type(await screen.findByLabelText(/^password/i), password);
    await userEvent.type(
      screen.getByLabelText(/^confirm password/i),
      confirmation,
    );
    await userEvent.click(
      screen.getByRole("button", { name: "Create account" }),
    );
  };

  it("shows the club and account name and removes the token from the address bar", async () => {
    usable();
    const { router } = renderApp(
      signedOut(),
      "/invitations/accept#token=secret-token",
    );
    expect(await screen.findByText(/Riverside RC/)).toBeInTheDocument();
    expect(screen.getByLabelText(/sign-in name/i)).toHaveValue("jdoe");
    expect(router.state.location.hash).toBe("");
    expect(window.location.hash).toBe("");
    expect(sentBody(callTo("checks"))).toEqual({ token: "secret-token" });
  });

  it("reports a mismatch without calling the platform", async () => {
    usable();
    renderApp(signedOut(), "/invitations/accept#token=t");
    await fill("Secret-123", "Secret-124");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "The two passwords do not match.",
    );
    expect(callTo("acceptances")).toBeUndefined();
  });

  it("reports a policy problem and keeps the form usable", async () => {
    usable(() =>
      json(400, {
        status: 400,
        code: "validation-failed",
        errors: [{ field: "password", code: "password-policy" }],
      }),
    );
    renderApp(signedOut(), "/invitations/accept#token=t");
    await fill("short", "short");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "does not meet the password requirements",
    );
    expect(
      screen.getByRole("button", { name: "Create account" }),
    ).toBeInTheDocument();
  });

  it("confirms success and leads to sign-in", async () => {
    usable();
    const { router } = renderApp(signedOut(), "/invitations/accept#token=t");
    await fill("Secret-123", "Secret-123");
    expect(
      await screen.findByText(/Your account is ready/),
    ).toBeInTheDocument();
    expect(sentBody(callTo("acceptances"))).toEqual({
      token: "t",
      password: "Secret-123",
    });
    await userEvent.click(screen.getByRole("link", { name: "Go to sign in" }));
    await waitFor(() =>
      expect(router.state.location.pathname).toBe("/sign-in"),
    );
  });

  it("shows one neutral message for an invalid invitation", async () => {
    stubFetch(() => json(400, { status: 400, code: "invitation-invalid" }));
    renderApp(signedOut(), "/invitations/accept#token=old");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This invitation cannot be used. Contact your club for help.",
    );
    expect(screen.queryByLabelText(/^password/i)).not.toBeInTheDocument();
  });

  it("treats a missing token as invalid without calling the platform", async () => {
    stubFetch(() => json(500, {}));
    renderApp(signedOut(), "/invitations/accept");
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "This invitation cannot be used.",
    );
    expect(fetchCalls().some((c) => /invitations/.test(urlOf(c)))).toBe(false);
  });
});

import { afterAll, beforeAll, describe, it } from "vitest";
import { type IonCallContext, type IonInterceptor, IonWsClient } from "@argon-chat/ion.webcore";
import { createClient } from "../../../../src/tests/Contracts/gen-ts/contracts";

/**
 * A resumable session against the real .NET server, through a proxy the server side resets in the
 * middle of the stream.
 *
 * Driven by `ForeignClientInteropTests` like `stream.interop.test.ts`, which also sets
 * `ION_INTEROP_PROXY_URL` — a TCP relay in front of the server that it resets (RST) while the
 * scenario runs. The server pushes r1 and r2, resets the connection, pushes r3 (into a dead
 * transport, or queued for the absent client), waits for the session to be resumed, and pushes r4.
 * The client must see exactly r1..r4, once each, in order, having resumed — not started afresh —
 * and the server must see one connection that was resumed at least once.
 *
 * A file of its own: this scenario reconnects on purpose, and the other file fails any that does.
 */

const PROXY = process.env.ION_INTEROP_PROXY_URL;
const RUN = process.env.ION_INTEROP_RUN ?? "js-local";

/** Sets the scenario's user on every ticket exchange — a resume exchanges a ticket like any connection. */
class LabUser implements IonInterceptor {
  exchanges = 0;

  constructor(readonly user: string) {}

  async invokeAsync(
    ctx: IonCallContext,
    next: (ctx: IonCallContext, signal?: AbortSignal) => Promise<void>,
    signal?: AbortSignal
  ): Promise<void> {
    this.exchanges++;
    (ctx.requestHeaders as Record<string, string>)["x-lab-user"] = this.user;
    await next(ctx, signal);
  }
}

describe.skipIf(!PROXY)("a resumable session against the real server", () => {
  const reconnected: string[] = [];
  const onReconnected = (attempt: number, resumed: boolean) =>
    reconnected.push(resumed ? `resumed after ${attempt}` : `started afresh after ${attempt}`);

  beforeAll(() => IonWsClient.on("reconnected", onReconnected));
  afterAll(() => IonWsClient.off("reconnected", onReconnected));

  it(`${RUN}-resume: reset mid-stream, resumed with nothing lost and nothing twice`, { timeout: 30_000 }, async ({ expect }) => {
    const who = new LabUser(`${RUN}-resume`);
    const lab = createClient(PROXY!, [who], {
      streamOptions: {
        transports: ["websocket"],
        reconnect: { initialDelayMs: 50, maxDelayMs: 400 },
      },
    }).StreamLab;

    const got: string[] = [];
    for await (const e of lab.Listen(`resume-${RUN}`)) {
      got.push(e.body);
      if (got.length === 4) break;
    }

    expect(got).toEqual(["r1", "r2", "r3", "r4"]);
    expect(reconnected.length).toBeGreaterThanOrEqual(1);
    expect(reconnected.every((r) => r.startsWith("resumed"))).toBe(true);
    expect(who.exchanges).toBeGreaterThanOrEqual(2);
  });
});

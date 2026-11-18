/**
 * @winche/functions/testing — run a function's code in tests (Deno test, or any runner) without a server.
 *
 *   import { withSecrets, withResources, withServer } from "@winche/functions/testing";
 *
 *   Deno.test("sends", () => withSecrets({ MAIL_KEY: "test" }, () => send.run({ to: "a@b" }, testContext())));
 *
 * Secrets, resources and @winche/admin read what the innermost `with…` gives while its callback runs (one at a time:
 * don't run tests in parallel that give different ones).
 */
import { type AdminOptions, type Call } from "@winche/admin";
import type { Caller, Context } from "./index.js";
/** Runs `fn` with these secrets' values (a secret not given reads as not set). */
export declare function withSecrets<R>(values: {
    [name: string]: string | null;
}, fn: () => R | Promise<R>): Promise<R>;
/**
 * Runs `fn` with resources: a folder on disk (e.g. "functions/resources"; needs --allow-read) or a map of paths to
 * contents.
 */
export declare function withResources<R>(source: string | {
    [path: string]: string | Uint8Array;
}, fn: () => R | Promise<R>): Promise<R>;
/**
 * Runs `fn` with @winche/admin reaching a server (the emulator, e.g. { server: "http://localhost:9090", project: "demo",
 * token }) instead of a function's run; or with a stand-in: a function that answers its calls ({ op, args } → result).
 */
export declare function withServer<R>(server: AdminOptions | Call, fn: () => R | Promise<R>): Promise<R>;
/** A context for calling a function's `run` in a test: its name, an optional caller, logs to the console. */
export declare function testContext(options?: {
    name?: string;
    caller?: Caller | null;
}): Context;

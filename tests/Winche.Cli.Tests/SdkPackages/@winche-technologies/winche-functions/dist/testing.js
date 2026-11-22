// @ts-self-types="./testing.d.ts"
// SPDX-License-Identifier: MIT
/**
 * @winche-technologies/winche-functions/testing — run a function's code in tests (Deno test, or any runner) without a server.
 *
 *   import { withSecrets, withResources, withServer } from "@winche-technologies/winche-functions/testing";
 *
 *   Deno.test("sends", () => withSecrets({ MAIL_KEY: "test" }, () => send.run({ to: "a@b" }, testContext())));
 *
 * Secrets, resources and @winche-technologies/winche-admin read what the innermost `with…` gives while its callback runs (one at a time:
 * don't run tests in parallel that give different ones).
 */
import { initializeAdmin } from "@winche-technologies/winche-admin";
const stack = [];
globalThis[Symbol.for("winche.test")] = () => (stack.length ? Object.assign({}, ...stack) : undefined);
async function within(run, fn) {
    stack.push(run);
    try {
        return await fn();
    }
    finally {
        stack.splice(stack.lastIndexOf(run), 1);
    }
}
/** Runs `fn` with these secrets' values (a secret not given reads as not set). */
export function withSecrets(values, fn) {
    return within({ secrets: {
            get: (name) => {
                const v = values[name];
                if (v == null)
                    throw Object.assign(new Error(`The secret ${name} isn't set in this test: give it to withSecrets.`), { code: "failed-precondition" });
                return v;
            },
            has: (name) => values[name] != null,
        } }, fn);
}
const deno = () => {
    const d = globalThis.Deno;
    if (!d)
        throw new Error("withResources(folder) reads the folder with Deno: run the tests with Deno, or give a map of paths to contents.");
    return d;
};
const clean = (p) => String(p ?? "").replace(/^(\.\/)+/, "").replace(/^\/+/, "");
/**
 * Runs `fn` with resources: a folder on disk (e.g. "functions/resources"; needs --allow-read) or a map of paths to
 * contents.
 */
export function withResources(source, fn) {
    if (typeof source === "string") {
        const root = source.replace(/\/+$/, "");
        const exists = (p) => { try {
            return deno().statSync(`${root}/${clean(p)}`).isFile;
        }
        catch {
            return false;
        } };
        const walk = async (dir, prefix, out) => {
            for await (const e of deno().readDir(dir)) {
                if (e.isDirectory)
                    await walk(`${dir}/${e.name}`, `${prefix}${e.name}/`, out);
                else if (e.isFile)
                    out.push(prefix + e.name);
            }
        };
        return within({ resources: {
                read: async (p) => (exists(p) ? await deno().readFile(`${root}/${clean(p)}`) : null),
                readText: async (p) => (exists(p) ? await deno().readTextFile(`${root}/${clean(p)}`) : null),
                list: async (folder) => {
                    const all = [];
                    await walk(root, "", all);
                    const prefix = clean(folder ?? "").replace(/\/+$/, "");
                    return all.filter((p) => prefix === "" || p.startsWith(prefix + "/")).sort();
                },
                has: exists,
            } }, fn);
    }
    const files = new Map(Object.entries(source).map(([p, v]) => [clean(p), typeof v === "string" ? new TextEncoder().encode(v) : v]));
    return within({ resources: {
            read: async (p) => files.get(clean(p)) ?? null,
            readText: async (p) => (files.has(clean(p)) ? new TextDecoder().decode(files.get(clean(p))) : null),
            list: async (folder) => {
                const prefix = clean(folder ?? "").replace(/\/+$/, "");
                return [...files.keys()].filter((p) => prefix === "" || p.startsWith(prefix + "/")).sort();
            },
            has: (p) => files.has(clean(p)),
        } }, fn);
}
/**
 * Runs `fn` with @winche-technologies/winche-admin reaching a server (the emulator, e.g. { server: "http://localhost:9090", project: "demo",
 * token }) instead of a function's run; or with a stand-in: a function that answers its calls ({ op, args } → result).
 */
export function withServer(server, fn) {
    if (typeof server === "function")
        return within({ call: server }, fn);
    const admin = initializeAdmin(server);
    return within({ call: admin.transport }, fn);
}
/** A context for calling a function's `run` in a test: its name, an optional caller, logs to the console. */
export function testContext(options) {
    return { name: options?.name ?? "test", caller: options?.caller ?? null, byServer: options?.byServer ?? false, app: options?.app ?? null,
        log: (...a) => console.log(...a) };
}

// @ts-self-types="./index.d.ts"
// SPDX-License-Identifier: MIT
/**
 * @winche-technologies/winche-functions — write Winche functions.
 *
 *   import { onDocument, callable, schedule, secrets } from "@winche-technologies/winche-functions";
 *   import { documents, FieldValue } from "@winche-technologies/winche-admin";
 *
 *   export const onVisit = onDocument.created("patients/{patientId}/visits/{visitId}", async (e, ctx) => {
 *     await documents.doc(`patients/${e.params.patientId}`).update({ lastVisit: e.data.get("at"), visits: FieldValue.increment(1) });
 *   });
 *
 *   export const hello = callable(async (data: { name: string }, ctx) => ({ greeting: `Hello ${data.name}`, uid: ctx.caller?.uid }));
 *
 *   const mailKey = secrets.define("MAIL_KEY");
 *   export const nightly = schedule("0 2 * * *", async (_e, ctx) => ctx.log("Nightly clean-up", mailKey.isSet()), { secrets: [mailKey] });
 *
 * Functions run on the Winche server in a sandbox: they can use the network (fetch) but not its files or settings.
 * Their data comes from @winche-technologies/winche-admin, which acts with the server's own rights inside them (access rules don't apply,
 * unless `as(ctx.caller)`).
 */
import { WincheError, DocumentReference, DocumentSnapshot, ChildReference, FileSnapshot, UserReference, UserSnapshot, decodeFields, encodeValue, fileData, userData, } from "@winche-technologies/winche-admin";
/** @internal The run in progress: the runtime's (one per run, across its awaits), else a test's. */
function current(what) {
    const g = globalThis;
    const run = g[Symbol.for("winche.run")]?.() ?? g[Symbol.for("winche.test")]?.();
    if (!run)
        throw new WincheError("failed-precondition", `${what} is read outside a function's run: read it inside the function (a module's top level runs before any run).`);
    return run;
}
const SECRET_NAME = /^[A-Z][A-Z0-9_]{0,127}$/;
export const secrets = {
    /** Declares a secret: names in capitals, digits and underscores (e.g. "MAIL_KEY"). */
    define(name) {
        if (typeof name !== "string" || !SECRET_NAME.test(name))
            throw new Error(`secrets.define: '${name}' isn't a secret name (capitals, digits and underscores, starting with a letter, e.g. MAIL_KEY).`);
        return Object.freeze({
            name,
            value: () => { const run = current(`The secret ${name}`); if (!run.secrets)
                throw new WincheError("failed-precondition", `No secrets here: the secret ${name} is read inside a function, or a test's withSecrets.`); return run.secrets.get(name); },
            isSet: () => !!current(`The secret ${name}`).secrets?.has(name),
            toJSON: () => name,
        });
    },
};
const resourcePath = (path) => String(path ?? "").replace(/^(\.\/)+/, "").replace(/^\/+/, "");
const resourcesNow = (what) => {
    const r = current(what).resources;
    if (!r)
        throw new WincheError("failed-precondition", `No resources here: ${what} is read inside a function, or a test's withResources.`);
    return r;
};
export const resources = {
    /** Declares a resource, read while a function runs. */
    define(path) {
        const p = resourcePath(path);
        if (!p || p.split("/").some((s) => s === ".." || s === ""))
            throw new Error(`resources.define: '${path}' isn't a path inside resources/ (e.g. "fonts/Roboto.ttf").`);
        return Object.freeze({
            path: p,
            bytes: () => resourcesNow(`The resource ${p}`).read(p),
            text: () => resourcesNow(`The resource ${p}`).readText(p),
            exists: () => resourcesNow(`The resource ${p}`).has(p),
        });
    },
    /** The resource paths in a folder and its subfolders (all of them without one), sorted. */
    list: (folder) => resourcesNow("The resources").list(folder),
};
/** @internal The context a handler gets. */
export function contextOf(raw) {
    return { name: raw.functionName, caller: raw.auth ?? null, byServer: raw.server === true, app: raw.app ?? null, log: raw.log };
}
/** A document as a change event carries it (plain fields beside typed ones). @internal */
function changedDocument(call, path, d) {
    if (!d)
        return new DocumentSnapshot(new DocumentReference(call, path), false, null, null);
    if (d.typedFields)
        return DocumentSnapshot.of(call, { ...d, fields: d.typedFields });
    return new DocumentSnapshot(new DocumentReference(call, path), true, d.fields ?? {}, { createTime: d.createTime, updateTime: d.updateTime, version: d.version });
}
/** @internal The event a handler gets, from what the server sent. */
function eventOf(trigger, a, call) {
    switch (trigger.kind) {
        case "documents":
            return { type: a.type, path: a.path, params: a.params ?? {}, data: changedDocument(call, a.path, a.data), before: changedDocument(call, a.path, a.before), id: a.id, at: new Date(a.at) };
        case "files": {
            const ref = new ChildReference(call, a.path);
            return { type: a.type, path: a.path, params: a.params ?? {}, file: new FileSnapshot(ref, a.file ? fileData(a.file) : null), id: a.id, at: new Date(a.at) };
        }
        case "users": {
            const ref = new UserReference(call, a.uid ?? a.user?.uid);
            return { type: a.type, uid: ref.uid, user: new UserSnapshot(ref, a.user ? userData(a.user) : null), provider: a.provider ?? null, id: a.id, at: new Date(a.at) };
        }
        case "schedule":
            return { scheduledAt: new Date(a.scheduledAt), id: a.id };
        case "assets":
            return { type: a.type, release: a.release, channel: a.channel ?? null, note: a.note ?? null, by: a.by ?? null, id: a.id, at: new Date(a.at) };
        case "before":
            if (trigger.on === "documents")
                return { operation: a.operation, path: a.path, params: a.params ?? {}, caller: a.auth ?? null,
                    before: a.before ? DocumentSnapshot.of(call, a.before) : new DocumentSnapshot(new DocumentReference(call, a.path), false, null, null),
                    data: a.data ? decodeFields(a.data.fields) : null };
            if (trigger.on === "files") {
                const ref = new ChildReference(call, a.path);
                return { operation: a.operation, path: a.path, params: a.params ?? {}, caller: a.auth ?? null, before: new FileSnapshot(ref, a.before ? fileData(a.before) : null), file: a.file };
            }
            if (a.user) {
                const ref = new UserReference(call, a.user.uid);
                return { user: new UserSnapshot(ref, userData(a.user)), provider: a.provider, ipAddress: a.ipAddress ?? null };
            }
            return a;
        default:
            return a;
    }
}
/** @internal A before-function's answer, for the server. */
function changesOut(trigger, r) {
    if (trigger.kind === "before" && trigger.on === "documents" && r && typeof r === "object" && r.set)
        return { ...r, set: Object.fromEntries(Object.entries(r.set).map(([k, v]) => [k, encodeValue(v)])) };
    return r;
}
/**
 * Throw it from a callable to send the app a code, a message and, optionally, details (anything JSON can carry).
 * Other errors reach the app as "internal" with no message; theirs stays in the logs.
 */
export class HttpsError extends Error {
    code;
    details;
    constructor(code, message, details) {
        super(message);
        this.code = code;
        this.details = details;
        this.name = "HttpsError";
    }
}
/** @internal Options as the server reads them: secrets by name. */
function optionsOut(options) {
    if (!options?.secrets)
        return options ?? {};
    if (!Array.isArray(options.secrets))
        throw new Error("secrets: give a list of handles from secrets.define(\"NAME\").");
    const names = options.secrets.map((s) => {
        if (typeof s === "string")
            throw new Error(`secrets: '${s}' is a name; give its handle: const key = secrets.define("${s}"), then { secrets: [key] }.`);
        if (!s || typeof s.name !== "string")
            throw new Error("secrets: give handles from secrets.define(\"NAME\").");
        return s.name;
    });
    return { ...options, secrets: names };
}
const define = (trigger, run, options = {}) => ({
    __winche: options?.integrity === true ? 2 : 1, trigger, options: optionsOut(options), run,
    handler: async (arg, raw) => {
        const r = raw;
        return changesOut(trigger, await run(eventOf(trigger, arg, r.call), contextOf(r)));
    },
});
/**
 * Before-functions run before a write is saved, inside it: the write waits for them, so keep them quick. They can
 * refuse it (throw HttpsError: its message reaches the caller) or change it (return the changes). In them
 * @winche-technologies/winche-admin only reads. Side effects (emails, other writes) belong in the onCreated… triggers.
 */
const before = (on, events, path, handler, options) => define({ kind: "before", on, events, path }, handler, options);
/** Document triggers. Paths are patterns: {name} matches one part (in event.params), {rest=**} the rest. */
export const onDocument = {
    created: (path, handler, options) => define({ kind: "documents", events: ["created"], path }, handler, options),
    updated: (path, handler, options) => define({ kind: "documents", events: ["updated"], path }, handler, options),
    deleted: (path, handler, options) => define({ kind: "documents", events: ["deleted"], path }, handler, options),
    /** Created, updated or deleted. */
    written: (path, handler, options) => define({ kind: "documents", events: ["created", "updated", "deleted"], path }, handler, options),
    /** Before a document is created, updated or deleted (each document of a batch, and of a recursive delete, once). */
    willBeWritten: (path, handler, options) => before("documents", ["write"], path, handler, options),
};
/** File triggers. */
export const onFile = {
    uploaded: (path, handler, options) => define({ kind: "files", events: ["uploaded"], path }, handler, options),
    deleted: (path, handler, options) => define({ kind: "files", events: ["deleted"], path }, handler, options),
    metadataUpdated: (path, handler, options) => define({ kind: "files", events: ["metadataUpdated"], path }, handler, options),
    /** Before a file is registered or replaced, its metadata changes, or it's deleted (each file under a deleted folder too). */
    willBeWritten: (path, handler, options) => before("files", ["write"], path, handler, options),
};
/** User triggers (Winche Auth). */
export const onUser = {
    created: (handler, options) => define({ kind: "users", events: ["created"] }, handler, options),
    deleted: (handler, options) => define({ kind: "users", events: ["deleted"] }, handler, options),
    signedIn: (handler, options) => define({ kind: "users", events: ["signedIn"] }, handler, options),
    disabled: (handler, options) => define({ kind: "users", events: ["disabled"] }, handler, options),
    /** Before someone becomes a user: throw HttpsError to refuse the sign-up. */
    willBeCreated: (handler, options) => before("users", ["create"], undefined, handler, options),
    /** Before a sign-in succeeds: throw HttpsError to refuse it. */
    willSignIn: (handler, options) => before("users", ["signIn"], undefined, handler, options),
};
/** Asset triggers: a release published, a channel moved. With options.channel, only that channel's. */
export const onAssets = {
    released: (handler, options) => assetTrigger(["released"], handler, options),
    channelMoved: (handler, options) => assetTrigger(["channelMoved"], handler, options),
};
const assetTrigger = (events, handler, options) => define({ kind: "assets", events, channel: options?.channel }, (e, ctx) => (options?.channel && e.channel !== options.channel ? null : handler(e, ctx)), options);
/** Runs on a schedule: a cron expression ("minute hour day month weekday"), in UTC unless timeZone is given. */
export const schedule = (cron, handler, options) => define({ kind: "schedule", cron, timeZone: options?.timeZone }, handler, options);
/** Called by apps (the SDKs' functions.call("name", data)); returns JSON to the caller. */
export const callable = (handler, options) => define({ kind: "callable" }, handler, options);
/**
 * A page in the console made of your own files: `pages({ title: "Reports", files: "console/reports" })` shows
 * console/reports/index.html in a sandboxed frame. In it, @winche-technologies/winche-admin acts as the person who opened it.
 */
export const pages = (options) => define({ kind: "consolePage" }, () => null, options);
/**
 * Answers web requests at /functions/v1/http/{name} (and below it): links in emails, other services' webhooks. It gets
 * a standard Request and returns a Response (or text, or JSON). Anyone can call it: check what you need yourself (a
 * signature with a secret, a token in the link). Bodies up to 1 MB.
 */
export const http = {
    onRequest: (handler, options) => define({ kind: "http", events: options?.methods }, handler, options),
};
const PLUGIN_FUNCTION = /^[A-Za-z_$][A-Za-z0-9_$]{0,63}$/;
/**
 * Defines a plugin: functions that projects install, configure and upgrade from the console or winche.json instead
 * of writing them. The package's default export; its winche-plugin.json lists the settings and secrets an installer
 * gives. `make` runs once for each installed copy and returns its functions by name:
 *
 *   export default plugin((p) => {
 *     const token = p.secret("AUTH_TOKEN");
 *     return {
 *       send: callable(async (data: { to: string; body: string }, ctx) => { … token.value() … p.setting("FROM") … }, { secrets: [token] }),
 *       queued: onDocument.created(`${p.setting("QUEUE")}/{id}`, async (e) => { … }, { secrets: [token] }),
 *     };
 *   });
 *
 * Apps call a copy's callables by its id: functions.call("sms.send", { … }).
 */
export function plugin(make) {
    if (typeof make !== "function")
        throw new Error("plugin: give a function that returns the plugin's functions, e.g. plugin((p) => ({ send: callable(…) })).");
    return Object.freeze({
        __wincheplugin: 1,
        install(installation) {
            const { id, settings = {}, secrets: names = {} } = installation ?? {};
            const handles = new Map();
            const instance = Object.freeze({
                id,
                settings: Object.freeze({ ...settings }),
                setting(name) {
                    if (!Object.hasOwn(settings, name))
                        throw new Error(`The plugin has no setting ${name}: list it in its winche-plugin.json.`);
                    return settings[name];
                },
                secret(name) {
                    if (!Object.hasOwn(names, name))
                        throw new Error(`The plugin has no secret ${name}: list it in its winche-plugin.json.`);
                    let handle = handles.get(name);
                    if (!handle)
                        handles.set(name, handle = secrets.define(names[name]));
                    return handle;
                },
            });
            const made = make(instance);
            if (!made || typeof made !== "object")
                throw new Error("plugin: make returns the plugin's functions by name, e.g. { send: callable(…) }.");
            const functions = {};
            for (const [name, fn] of Object.entries(made)) {
                if (!PLUGIN_FUNCTION.test(name))
                    throw new Error(`plugin: '${name}' isn't a function name (letters and digits).`);
                if (!fn || typeof fn.__winche !== "number")
                    throw new Error(`plugin: ${name} isn't a function (callable, onDocument.created, http.onRequest, …).`);
                if (fn.trigger.kind === "consolePage")
                    throw new Error(`plugin: ${name}: plugins can't add console pages.`);
                functions[name] = fn;
            }
            return Object.freeze({ __wincheGroup: 1, id, functions: Object.freeze(functions) });
        },
    });
}

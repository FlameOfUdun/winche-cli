namespace Winche.Cli;

/// <summary>
/// The client for console pages (<c>@winche/console-page</c>): typed calls over the bridge the console gives every page
/// (<c>window.winche</c>), and React hooks. Written into each page project (<c>.winche/console-page/</c>) by
/// <c>winche pages init</c> and refreshed at every build, like the functions SDK; the template's Vite and TypeScript
/// settings name it <c>@winche/console-page</c>.
/// </summary>
internal static class ConsolePageClient
{
    public const string Folder = ".winche/console-page";

    public const string Index = """
        // @winche/console-page: a console page's side of the bridge the Winche console gives it (window.winche).
        // Written by the Winche CLI (refreshed with it): don't edit.
        //
        // A page runs in a sandboxed frame, without the console's sign-in: it reaches its project through its actions
        // (consoleAction in the functions), which the console runs for it with the signed-in person's role.

        /** Who opened the page, as the console knows them. */
        export interface ConsoleUser {
          email: string | null;
          role: "viewer" | "member" | "admin";
        }

        /** The console's look: light or dark, its colors (also --winche-* CSS variables) and font. */
        export interface ConsoleTheme {
          scheme: "light" | "dark";
          colors?: Record<string, string>;
          font?: string;
        }

        /** An action's refusal: its message, code (e.g. "permission-denied") and details. */
        export interface ActionError extends Error {
          code?: string;
          details?: unknown;
        }

        interface Bridge {
          user: ConsoleUser | null;
          theme: ConsoleTheme | null;
          ready: Promise<Bridge>;
          call(name: string, data?: unknown): Promise<unknown>;
          confirm(text: string, options?: { danger?: boolean; confirmLabel?: string }): Promise<boolean>;
          toast(text: string, kind?: "info" | "success" | "error"): void;
          navigate(path: string): void;
          setTitle(text: string): void;
        }

        const bridge = (): Bridge => {
          const found = (globalThis as { winche?: Bridge }).winche;
          if (!found) throw new Error("This page isn't open in the Winche console (open it from the console: it adds the bridge).");
          return found;
        };

        /** Resolves once the console has connected (who's here and the theme are known). */
        export const ready = (): Promise<void> => bridge().ready.then(() => undefined);

        /** Runs the action `name` with `data`; its result, or an {@link ActionError}. */
        export const call = async <T = unknown>(name: string, data?: unknown): Promise<T> => (await bridge().call(name, data)) as T;

        /** Asks the person in the console's own dialog; true when they confirm. */
        export const confirm = (text: string, options?: { danger?: boolean; confirmLabel?: string }): Promise<boolean> =>
          bridge().confirm(text, options);

        /** A notice in the console. */
        export const toast = (text: string, kind: "info" | "success" | "error" = "info"): void => bridge().toast(text, kind);

        /** Opens one of the console's pages (a path within the project, e.g. "/database"). */
        export const navigate = (path: string): void => bridge().navigate(path);

        /** The line under the page's title in the console. */
        export const setTitle = (text: string): void => bridge().setTitle(text);

        /** Who opened the page (null until {@link ready}). */
        export const user = (): ConsoleUser | null => bridge().user;

        /** Whether the person is an admin of the project. */
        export const isAdmin = (): boolean => user()?.role === "admin";

        /** The console's theme (null until {@link ready}). */
        export const theme = (): ConsoleTheme | null => bridge().theme;

        /** Calls `listener` when the console's theme changes; returns a function that stops it. */
        export const onTheme = (listener: (theme: ConsoleTheme) => void): (() => void) => {
          const handle = (e: Event) => listener((e as CustomEvent<ConsoleTheme>).detail);
          window.addEventListener("winche:theme", handle);
          return () => window.removeEventListener("winche:theme", handle);
        };

        /** The message to show for an error from {@link call}. */
        export const errorText = (error: unknown): string => error instanceof Error ? error.message : String(error);

        /**
         * Runs the action now and again while there's reason to: `every` (ms) after each result, or a function of the
         * result that says how long until the next (a number) or to stop (false), e.g.
         * `(r) => r.status === "running" ? 3000 : false` to follow a background job until it ends.
         * Returns a function that stops it.
         */
        export function follow<T = unknown>(
          name: string,
          data: unknown,
          options: { every: number | ((result: T) => number | false); onResult: (result: T) => void; onError?: (error: ActionError) => void },
        ): () => void {
          let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
          const run = async () => {
            if (stopped) return;
            try {
              const result = await call<T>(name, data);
              if (stopped) return;
              options.onResult(result);
              const next = typeof options.every === "function" ? options.every(result) : options.every;
              if (next !== false) timer = setTimeout(run, next);
            } catch (e) {
              if (!stopped) options.onError?.(e as ActionError);
            }
          };
          void run();
          return () => { stopped = true; clearTimeout(timer); };
        }
        """;

    public const string React = """
        // @winche/console-page/react: React hooks over a console page's bridge. Written by the Winche CLI (refreshed with
        // it): don't edit.
        import { useCallback, useEffect, useRef, useState } from "react";
        import { call, confirm, errorText, onTheme, ready, theme, toast, user, type ActionError, type ConsoleTheme, type ConsoleUser } from "./index";

        /** True once the console has connected (who's here and the theme are known). */
        export function useReady(): boolean {
          const [isReady, setReady] = useState(false);
          useEffect(() => { let live = true; void ready().then(() => live && setReady(true)); return () => { live = false; }; }, []);
          return isReady;
        }

        /** Who opened the page (null until the console has connected). */
        export function useUser(): ConsoleUser | null {
          const isReady = useReady();
          return isReady ? user() : null;
        }

        /** The console's theme, kept up to date (null until the console has connected). */
        export function useTheme(): ConsoleTheme | null {
          const isReady = useReady();
          const [current, setCurrent] = useState<ConsoleTheme | null>(null);
          useEffect(() => { if (isReady) setCurrent(theme()); return onTheme(setCurrent); }, [isReady]);
          return current;
        }

        export interface ActionState<T> {
          data: T | undefined;
          error: ActionError | undefined;
          /** The first load (later ones keep showing the last data). */
          loading: boolean;
          /** Loads again now. */
          refresh: () => Promise<void>;
        }

        /**
         * The result of the action `name` with `data`, loaded when the page opens and again when `data` changes.
         * `refreshEvery` keeps it current: a number (ms), or a function of the result saying how long until the next load
         * or false to stop, e.g. `(r) => r.backfill.status === "running" ? 3000 : false`.
         */
        export function useAction<T = unknown>(
          name: string,
          data?: unknown,
          options: { refreshEvery?: number | ((result: T) => number | false); enabled?: boolean } = {},
        ): ActionState<T> {
          const [state, setState] = useState<{ data?: T; error?: ActionError; loading: boolean }>({ loading: true });
          const key = JSON.stringify(data ?? null);
          const every = useRef(options.refreshEvery);
          every.current = options.refreshEvery;
          const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
          const generation = useRef(0);
          const enabled = options.enabled ?? true;

          const load = useCallback(async () => {
            clearTimeout(timer.current);
            const mine = ++generation.current;
            try {
              await ready();
              const result = await call<T>(name, JSON.parse(key));
              if (mine !== generation.current) return;
              setState({ data: result, loading: false });
              const next = typeof every.current === "function" ? every.current(result) : every.current;
              if (next !== undefined && next !== false) timer.current = setTimeout(() => void load(), next);
            } catch (e) {
              if (mine === generation.current) setState((s) => ({ data: s.data, error: e as ActionError, loading: false }));
            }
          }, [name, key]);

          useEffect(() => {
            if (!enabled) return;
            void load();
            return () => { generation.current++; clearTimeout(timer.current); };
          }, [load, enabled]);

          return { data: state.data, error: state.error, loading: state.loading, refresh: load };
        }

        export interface RunState<T> {
          /** Runs the action (asking first, if `confirm` is set); resolves to its result, or undefined when declined or refused. */
          run: (data?: unknown) => Promise<T | undefined>;
          running: boolean;
          error: ActionError | undefined;
        }

        /**
         * An action run on demand (a button): `confirm` asks first in the console's dialog (`danger` makes it red), its
         * result is announced (`success`: the notice, else the result if it's text, else "Done"), and `after` runs then
         * (e.g. a {@link useAction}'s refresh).
         */
        export function useRun<T = unknown>(
          name: string,
          options: { confirm?: string; danger?: boolean; confirmLabel?: string; success?: string | false; after?: (result: T) => unknown } = {},
        ): RunState<T> {
          const [running, setRunning] = useState(false);
          const [error, setError] = useState<ActionError>();
          const run = useCallback(async (data?: unknown) => {
            if (options.confirm && !(await confirm(options.confirm, { danger: options.danger, confirmLabel: options.confirmLabel }))) return undefined;
            setRunning(true);
            setError(undefined);
            try {
              const result = await call<T>(name, data);
              if (options.success !== false) toast(options.success ?? (typeof result === "string" ? result : "Done"), "success");
              await options.after?.(result);
              return result;
            } catch (e) {
              setError(e as ActionError);
              toast(errorText(e), "error");
              return undefined;
            } finally {
              setRunning(false);
            }
          }, [name, options.confirm, options.danger, options.confirmLabel, options.success, options.after]);
          return { run, running, error };
        }
        """;
}

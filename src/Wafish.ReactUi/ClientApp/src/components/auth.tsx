import { createContext, useContext, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useQueryClient } from "@tanstack/react-query";
import { ArrowRight } from "lucide-react";
import { WafishMark } from "@/components/wafish-mark";
import {
  api,
  config,
  field,
  onSessionExpired,
  setCredential,
  write,
} from "@/api/client";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ErrorNotice, Field } from "@/components/workspace";
import {
  beginAuthenticatorLogin,
  completeAuthenticatorLogin,
  supportsWebAuthn,
} from "@/api/webauthn";
const Context = createContext({ logout: async () => {} });
export const useAuth = () => useContext(Context);
const schema = z.object({
  username: z.string(),
  password: z.string().min(config.authMode === "None" ? 0 : 1, "Required"),
  twoFactorCode: z.string(),
});
type Login = z.infer<typeof schema>;
export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<"loading" | "in" | "out">(() =>
    ["ApiKey", "BasicAuth"].includes(config.authMode) ? "out" : "loading",
  );
  const [error, setError] = useState<unknown>();
  const [twoFactor, setTwoFactor] = useState(false);
  const [passkeyPending, setPasskeyPending] = useState(false);
  const passkeyAbort = useRef<AbortController | null>(null);
  useEffect(() => () => passkeyAbort.current?.abort(), []);
  const query = useQueryClient();
  const { t } = useLocale();
  const form = useForm<Login>({
    resolver: zodResolver(schema),
    defaultValues: { username: "", password: "", twoFactorCode: "" },
  });
  useEffect(() => {
    let live = true;
    onSessionExpired(() => {
      setCredential({});
      query.clear();
      setState("out");
      setError(new Error("Session expired. Sign in again."));
    });
    if (!["ApiKey", "BasicAuth"].includes(config.authMode))
      void api("api/auth/status")
        .then(() => {
          if (live) setState("in");
        })
        .catch(() => {
          if (live) setState("out");
        });
    return () => {
      live = false;
      onSessionExpired(() => {});
    };
  }, [query]);
  async function login(values: Login) {
    setError(undefined);
    try {
      if (config.authMode === "ApiKey")
        setCredential({ [config.apiKeyHeader]: values.password });
      else if (config.authMode === "BasicAuth")
        setCredential({
          Authorization:
            "Basic " +
            btoa(
              String.fromCharCode(
                ...new TextEncoder().encode(
                  values.username + ":" + values.password,
                ),
              ),
            ),
        });
      else if (config.authMode !== "None") {
        const result = await write("login", "POST", values);
        if (field(result, "requiresTwoFactor")) {
          setTwoFactor(true);
          form.setFocus("twoFactorCode");
          return;
        }
      }
      await api("api/auth/status");
      query.clear();
      setState("in");
      form.reset();
    } catch (e) {
      setCredential({});
      setError(e);
    }
  }
  async function passkeyLogin() {
    setError(undefined);
    setPasskeyPending(true);
    const controller = new AbortController();
    passkeyAbort.current = controller;
    try {
      const values = form.getValues();
      if (!values.username.trim()) {
        form.setFocus("username");
        throw new Error("Enter your username.");
      }
      const ceremony = await beginAuthenticatorLogin(
        values.username,
        controller.signal,
      );
      if (ceremony.requiresTwoFactor && !values.twoFactorCode) {
        setTwoFactor(true);
        return;
      }
      await completeAuthenticatorLogin(
        ceremony,
        values.twoFactorCode,
        controller.signal,
      );
      await api("api/auth/status", { signal: controller.signal });
      query.clear();
      setState("in");
      form.reset();
    } catch (e) {
      if (!controller.signal.aborted) setError(e);
    } finally {
      if (!controller.signal.aborted) setPasskeyPending(false);
    }
  }
  async function logout() {
    try {
      await write("logout", "POST");
    } finally {
      setCredential({});
      query.clear();
      setState("out");
      setTwoFactor(false);
      setError(undefined);
    }
  }
  if (state === "loading")
    return (
      <div className="login-page" role="status">
        {t("Loading")}
      </div>
    );
  if (state === "out")
    return (
      <div className="login-page">
        <div className="login-card">
          <div className="brand-mark">
            <WafishMark />
          </div>
          <p className="eyebrow mt-6">Wafish / YARP</p>
          <h1 className="mt-2 text-3xl font-semibold">{t("Sign in")}</h1>
          <p className="mt-3 text-muted-foreground">
            {t("Use your gateway credentials to continue.")}
          </p>
          <form onSubmit={form.handleSubmit(login)} className="mt-8 space-y-5">
            <ErrorNotice error={error} draft={false} title="Sign in" />
            {config.authMode !== "ApiKey" && config.authMode !== "None" && (
              <Field label="Username">
                <Input
                  autoComplete="username"
                  {...form.register("username")}
                  required
                  autoFocus
                />
              </Field>
            )}
            {config.authMode !== "None" && (
              <Field
                label={config.authMode === "ApiKey" ? "API key" : "Password"}
              >
                <Input
                  type="password"
                  autoComplete="current-password"
                  {...form.register("password")}
                  required
                />
              </Field>
            )}
            {twoFactor && (
              <Field label="Verification code">
                <Input
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  {...form.register("twoFactorCode")}
                  required
                />
              </Field>
            )}
            <Button
              className="w-full"
              disabled={form.formState.isSubmitting || passkeyPending}
              type="submit"
            >
              {t("Sign in")}
              <ArrowRight />
            </Button>
            {supportsWebAuthn() && (
              <Button
                type="button"
                variant="outline"
                className="w-full"
                disabled={form.formState.isSubmitting || passkeyPending}
                onClick={passkeyLogin}
              >
                {t("Sign in with an authenticator")}
              </Button>
            )}
          </form>
          <div className="mt-6 flex items-center justify-between gap-3 text-xs text-muted-foreground">
            <span>{t("Credentials stay in this tab.")}</span>
          </div>
        </div>
      </div>
    );
  return <Context.Provider value={{ logout }}>{children}</Context.Provider>;
}

import { useEffect, useRef, useState } from "react";
import { useApi } from "@/hooks/api";
import { field } from "@/api/client";
import {
  authenticatorWrite,
  enrollAuthenticator,
  supportsWebAuthn,
} from "@/api/webauthn";
import type { AuthenticatorDescription } from "@/api/webauthn";
import { useAuth } from "@/components/auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ErrorNotice, Field, Panel, QueryState } from "@/components/workspace";
import { useLocale } from "@/locales/i18n";

export function Authenticators({
  requiresTwoFactor,
}: {
  requiresTwoFactor: boolean;
}) {
  const query = useApi("api/webauthn/credentials");
  const { logout } = useAuth();
  const { t } = useLocale();
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<unknown>();
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  const credentials = (field(query.data, "credentials") ??
    []) as AuthenticatorDescription[];
  const proofMissing = !password || (requiresTwoFactor && !code);
  async function change(id?: string) {
    const controller = new AbortController();
    abort.current = controller;
    setPending(true);
    setError(undefined);
    try {
      if (id)
        await authenticatorWrite(
          "credentials/remove",
          { credentialId: id, password, twoFactorCode: code || null },
          controller.signal,
        );
      else await enrollAuthenticator(name, password, code, controller.signal);
      setPassword("");
      setCode("");
      setName("");
      await logout();
    } catch (e) {
      if (!controller.signal.aborted) setError(e);
    } finally {
      if (!controller.signal.aborted) {
        setPending(false);
        setPassword("");
        setCode("");
      }
    }
  }
  return (
    <Panel title="Authenticators">
      <p className="mb-4 text-sm text-muted-foreground">
        {t(
          "Use a passkey or security key. Changes require a new sign-in. Keep your password for recovery.",
        )}
      </p>
      <QueryState query={query}>
        <div className="space-y-4">
          <Field label="Current password">
            <Input
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              disabled={pending}
            />
          </Field>
          {requiresTwoFactor && (
            <Field label="Authenticator verification code">
              <Input
                inputMode="numeric"
                autoComplete="one-time-code"
                value={code}
                onChange={(e) => setCode(e.target.value)}
                disabled={pending}
              />
            </Field>
          )}
          {credentials.length === 0 && (
            <p>{t("No authenticators registered.")}</p>
          )}
          <ul className="space-y-3">
            {credentials.map((item) => (
              <li
                key={item.id}
                className="flex items-center justify-between gap-4"
              >
                <div>
                  <strong>{item.name}</strong>
                  <p className="text-sm text-muted-foreground">
                    {t("Registered")}:{" "}
                    {new Date(item.createdUtc).toLocaleString()}
                  </p>
                  {item.lastUsedUtc && (
                    <p className="text-sm text-muted-foreground">
                      {t("Last used")}:{" "}
                      {new Date(item.lastUsedUtc).toLocaleString()}
                    </p>
                  )}
                </div>
                <Button
                  variant="outline"
                  disabled={pending || proofMissing}
                  onClick={() => change(item.id)}
                  aria-label={t("Remove") + " " + item.name}
                >
                  {t("Remove")}
                </Button>
              </li>
            ))}
          </ul>
          {supportsWebAuthn() ? (
            <>
              <Field label="Authenticator name">
                <Input
                  maxLength={64}
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  disabled={pending}
                />
              </Field>
              <Button
                disabled={
                  pending ||
                  proofMissing ||
                  !name.trim() ||
                  credentials.length >= 20
                }
                onClick={() => change()}
              >
                {t("Add authenticator")}
              </Button>
            </>
          ) : (
            <p>
              {t(
                "Authenticator enrollment requires a supported browser and a secure connection.",
              )}
            </p>
          )}
          <ErrorNotice error={error} draft={false} />
        </div>
      </QueryState>
    </Panel>
  );
}

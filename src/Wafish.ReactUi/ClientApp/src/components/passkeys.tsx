import { useEffect, useRef, useState } from "react";
import { rows, text, field, write } from "@/api/client";
import { registerPasskey, passkeysSupported } from "@/api/passkeys";
import { useApi } from "@/hooks/api";
import { useAuth } from "@/components/auth";
import {
  Panel,
  QueryState,
  Field,
  Confirm,
  ErrorNotice,
} from "@/components/workspace";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useLocale } from "@/locales/i18n";

export function PasskeyPanel() {
  const query = useApi("api/passkeys");
  const { logout } = useAuth();
  const { t } = useLocale();
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => controller.current?.abort(), []);
  return (
    <Panel title="Passkeys">
      <p>
        {t(
          "Password sign-in remains available for recovery. Revoking a passkey signs out all sessions.",
        )}
      </p>
      <QueryState query={query}>
        <ul>
          {rows(query.data).map((item) => (
            <li key={text(field(item, "id"))}>
              {text(field(item, "name"))}
              <Confirm
                title="Revoke passkey"
                description={t(
                  "Revoke this passkey and sign out all sessions?",
                )}
                onConfirm={async () => {
                  await write("api/passkeys/revoke", "POST", {
                    id: field(item, "id"),
                  });
                  await logout();
                }}
              >
                {t("Revoke")}
              </Confirm>
            </li>
          ))}
        </ul>
      </QueryState>
      <Field label="Passkey name">
        <Input
          maxLength={64}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
      </Field>
      <Button
        disabled={busy || !name.trim() || !passkeysSupported()}
        onClick={async () => {
          setBusy(true);
          setError(undefined);
          controller.current = new AbortController();
          try {
            await registerPasskey(name.trim(), controller.current.signal);
            setName("");
            await query.refetch();
          } catch (error) {
            setError(error);
          } finally {
            setBusy(false);
            controller.current = null;
          }
        }}
      >
        {t("Register passkey")}
      </Button>
      {!passkeysSupported() && (
        <p>
          {t("This browser does not support passkeys. Use password sign-in.")}
        </p>
      )}
      <ErrorNotice error={error} />
    </Panel>
  );
}

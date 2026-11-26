import { api } from "@/api/client";

const post = <T = unknown>(path: string, body: unknown, signal?: AbortSignal) =>
  api<T>(path, { method: "POST", body: JSON.stringify(body), signal });

type Ceremony<T> = { id: string; publicKey: T };
export function passkeysSupported() {
  return (
    typeof PublicKeyCredential !== "undefined" &&
    typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
    typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function" &&
    typeof PublicKeyCredential.prototype.toJSON === "function"
  );
}
export async function registerPasskey(name: string, signal?: AbortSignal) {
  if (!passkeysSupported())
    throw new Error(
      "This browser does not support passkeys. Use password sign-in.",
    );
  const ceremony = (await post(
    "api/passkeys/registration/options",
    { name },
    signal,
  )) as Ceremony<PublicKeyCredentialCreationOptionsJSON>;
  const credential = (await navigator.credentials.create({
    publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(
      ceremony.publicKey,
    ),
    signal,
  })) as PublicKeyCredential | null;
  if (!credential) throw new Error("Passkey registration was cancelled.");
  return post(
    "api/passkeys/registration",
    { id: ceremony.id, credential: credential.toJSON() },
    signal,
  );
}
export async function signInWithPasskey(signal?: AbortSignal) {
  if (!passkeysSupported())
    throw new Error(
      "This browser does not support passkeys. Use password sign-in.",
    );
  const ceremony = (await post(
    "api/passkeys/login/options",
    {},
    signal,
  )) as Ceremony<PublicKeyCredentialRequestOptionsJSON>;
  const credential = (await navigator.credentials.get({
    publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(
      ceremony.publicKey,
    ),
    signal,
  })) as PublicKeyCredential | null;
  if (!credential) throw new Error("Passkey sign-in was cancelled.");
  return post(
    "api/passkeys/login",
    { id: ceremony.id, credential: credential.toJSON() },
    signal,
  );
}

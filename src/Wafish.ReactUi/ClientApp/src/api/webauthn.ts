import { api, config } from "@/api/client";

type Descriptor = Omit<PublicKeyCredentialDescriptor, "id"> & { id: string };
type Creation = Omit<
  PublicKeyCredentialCreationOptions,
  "challenge" | "user" | "excludeCredentials"
> & {
  challenge: string;
  user: Omit<PublicKeyCredentialUserEntity, "id"> & { id: string };
  excludeCredentials?: Descriptor[];
};
type Assertion = Omit<
  PublicKeyCredentialRequestOptions,
  "challenge" | "allowCredentials"
> & {
  challenge: string;
  allowCredentials?: Descriptor[];
};
export interface Ceremony<T> {
  challengeId: string;
  options: T;
  requiresTwoFactor: boolean;
}
export interface AuthenticatorDescription {
  id: string;
  name: string;
  createdUtc: string;
  lastUsedUtc: string | null;
}
export const supportsWebAuthn = () =>
  Boolean(
    config.webAuthn &&
    window.isSecureContext &&
    window.PublicKeyCredential &&
    navigator.credentials,
  );

function decode(value: string): ArrayBuffer {
  return Uint8Array.from(
    atob(value.replace(/-/g, "+").replace(/_/g, "/")),
    (char) => char.charCodeAt(0),
  ).buffer;
}
function encode(value: ArrayBuffer): string {
  return btoa(String.fromCharCode(...new Uint8Array(value)))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}
function descriptor(value: Descriptor): PublicKeyCredentialDescriptor {
  return { ...value, id: decode(value.id) };
}
function serialize(credential: Credential | null, registration: boolean) {
  if (!credential || credential.type !== "public-key")
    throw new Error("Authenticator operation was cancelled.");
  const key = credential as PublicKeyCredential;
  const response = key.response;
  const common = { clientDataJSON: encode(response.clientDataJSON) };
  return {
    id: key.id,
    rawId: encode(key.rawId),
    type: key.type,
    extensions: key.getClientExtensionResults(),
    response: registration
      ? {
          ...common,
          attestationObject: encode(
            (response as AuthenticatorAttestationResponse).attestationObject,
          ),
          transports:
            (response as AuthenticatorAttestationResponse).getTransports?.() ??
            [],
        }
      : {
          ...common,
          authenticatorData: encode(
            (response as AuthenticatorAssertionResponse).authenticatorData,
          ),
          signature: encode(
            (response as AuthenticatorAssertionResponse).signature,
          ),
          userHandle: (response as AuthenticatorAssertionResponse).userHandle
            ? encode((response as AuthenticatorAssertionResponse).userHandle!)
            : null,
        },
  };
}
// Fresh proof can fail while the existing session remains valid.
export function authenticatorWrite<T>(
  path: string,
  body: unknown,
  signal?: AbortSignal,
) {
  return api<T>(
    "api/webauthn/" + path,
    {
      method: "POST",
      body: JSON.stringify(body),
      signal,
    },
    false,
  );
}
export const beginAuthenticatorLogin = (
  username: string,
  signal?: AbortSignal,
) =>
  authenticatorWrite<Ceremony<Assertion>>(
    "login/options",
    { username },
    signal,
  );
export async function completeAuthenticatorLogin(
  ceremony: Ceremony<Assertion>,
  twoFactorCode: string,
  signal?: AbortSignal,
) {
  const options = ceremony.options;
  const credential = await navigator.credentials.get({
    signal,
    publicKey: {
      ...options,
      challenge: decode(options.challenge),
      allowCredentials: options.allowCredentials?.map(descriptor),
    },
  });
  await authenticatorWrite(
    "login/complete",
    {
      challengeId: ceremony.challengeId,
      credential: serialize(credential, false),
      twoFactorCode: twoFactorCode || null,
    },
    signal,
  );
}
export async function enrollAuthenticator(
  name: string,
  password: string,
  twoFactorCode: string,
  signal?: AbortSignal,
) {
  const ceremony = await authenticatorWrite<Ceremony<Creation>>(
    "enrollment/options",
    { name, password, twoFactorCode: twoFactorCode || null },
    signal,
  );
  const options = ceremony.options;
  const credential = await navigator.credentials.create({
    signal,
    publicKey: {
      ...options,
      challenge: decode(options.challenge),
      user: { ...options.user, id: decode(options.user.id) },
      excludeCredentials: options.excludeCredentials?.map(descriptor),
    },
  });
  await authenticatorWrite(
    "enrollment/complete",
    {
      challengeId: ceremony.challengeId,
      credential: serialize(credential, true),
    },
    signal,
  );
}

using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

internal static partial class WebAuthnChecks
{
    private sealed class Authenticator : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal byte[] Id { get; } = RandomNumberGenerator.GetBytes(32);
        internal string UserHandle { get; private set; } = "";
        internal uint Counter { get; set; }
        private static string Encode(byte[] value) => WebEncoders.Base64UrlEncode(value);
        private static byte[] ClientData(string type, JsonElement options, string origin, bool crossOrigin) => JsonSerializer.SerializeToUtf8Bytes(new
        { type, challenge = options.GetProperty("challenge").GetString(), origin, crossOrigin });
        internal object Create(JsonElement options, string origin = Origin)
        {
            UserHandle = options.GetProperty("user").GetProperty("id").GetString()!;
            var key = _key.ExportParameters(false);
            var cose = new CborWriter(); cose.WriteStartMap(5);
            cose.WriteInt32(1); cose.WriteInt32(2); cose.WriteInt32(3); cose.WriteInt32(-7);
            cose.WriteInt32(-1); cose.WriteInt32(1);
            cose.WriteInt32(-2); cose.WriteByteString(key.Q.X!); cose.WriteInt32(-3); cose.WriteByteString(key.Q.Y!); cose.WriteEndMap();
            var publicKey = cose.Encode();
            var data = new byte[37 + 16 + 2 + Id.Length + publicKey.Length];
            SHA256.HashData(Encoding.UTF8.GetBytes(options.GetProperty("rp").GetProperty("id").GetString()!)).CopyTo(data, 0);
            data[32] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(53), (ushort)Id.Length);
            Id.CopyTo(data, 55); publicKey.CopyTo(data, 55 + Id.Length);
            var attestation = new CborWriter(); attestation.WriteStartMap(3);
            attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
            attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
            attestation.WriteTextString("authData"); attestation.WriteByteString(data); attestation.WriteEndMap();
            return new { id = Encode(Id), rawId = Encode(Id), type = "public-key", clientExtensionResults = new { },
                response = new { attestationObject = Encode(attestation.Encode()), clientDataJSON = Encode(ClientData("webauthn.create", options, origin, false)), transports = new[] { "internal" } } };
        }
        internal object Get(JsonElement options, string origin = Origin, string rp = "localhost", bool verified = true,
            bool crossOrigin = false, string? userHandle = null, bool corruptSignature = false)
        {
            var data = new byte[37]; SHA256.HashData(Encoding.UTF8.GetBytes(rp)).CopyTo(data, 0);
            data[32] = verified ? (byte)5 : (byte)1;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), ++Counter);
            var client = ClientData("webauthn.get", options, origin, crossOrigin);
            var signature = _key.SignData([.. data, .. SHA256.HashData(client)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (corruptSignature) signature[^1] ^= 1;
            return new { id = Encode(Id), rawId = Encode(Id), type = "public-key", clientExtensionResults = new { },
                response = new { authenticatorData = Encode(data), signature = Encode(signature), clientDataJSON = Encode(client), userHandle = userHandle ?? UserHandle } };
        }
        public void Dispose() => _key.Dispose();
    }
}

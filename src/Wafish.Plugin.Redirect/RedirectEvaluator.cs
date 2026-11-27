using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.WebUtilities;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Plugin.Redirect;

/// <summary>Immutable template compiled before configuration publication.</summary>
public sealed class CompiledRedirect
{
    internal CompiledRedirect(RedirectOptions options, ImmutableArray<RedirectPart> parts, FrozenSet<string> hosts, bool variableHost)
        => (Options, Parts, Hosts, VariableHost) = (options with { AllowedHosts = [] }, parts, hosts, variableHost);
    internal RedirectOptions Options { get; }
    internal ImmutableArray<RedirectPart> Parts { get; }
    internal FrozenSet<string> Hosts { get; }
    internal bool VariableHost { get; }
}

internal sealed record RedirectPart(string Text, bool Token = false, bool Host = false, bool CatchAll = false);

public static class RedirectEvaluator
{
    public const int MaximumUrlBytes = 8192;

    public static CompiledRedirect Compile(RedirectOptions options, RouteConfig route)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(route);
        if (!Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.QueryMode) ||
            options.StatusCode is not (301 or 302 or 303 or 307 or 308) || options.HttpsPort is < 1 or > 65535)
            throw new ArgumentException("Invalid redirect mode, query mode, status, or HTTPS port.");
        if (options.AllowedHosts is null || options.AllowedHosts.Length > 128)
            throw new ArgumentException("Redirects allow at most 128 exact destination hosts.");
        var hosts = options.AllowedHosts.Select(NormalizeHost).ToFrozenSet(StringComparer.Ordinal);
        if (options.Mode == RedirectMode.HttpsOnly)
        {
            if (!string.IsNullOrEmpty(options.Target)) throw new ArgumentException("HTTPS mode does not accept a target.");
            if (hosts.Count == 0) throw new ArgumentException("HTTPS mode requires allowedHosts.");
            return new(options, [], hosts, true);
        }

        var target = options.Target;
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Target mode requires a target.");
        ValidateCharacters(target);
        var local = target.StartsWith('/') && !target.StartsWith("//", StringComparison.Ordinal);
        var authorityStart = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 :
            target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : -1;
        if (!local && authorityStart < 0) throw new ArgumentException("Use a local absolute path or HTTP(S) URL.");
        var authorityEnd = authorityStart < 0 ? -1 : target.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityStart >= 0 && authorityEnd < 0) authorityEnd = target.Length;
        var fullPathToken = target.IndexOf("{request.path}", StringComparison.Ordinal);
        if (authorityStart >= 0 && fullPathToken >= authorityStart && fullPathToken < authorityEnd) authorityEnd = fullPathToken;
        var queryStart = target.IndexOfAny(['?', '#']);
        if (queryStart < 0) queryStart = target.Length;
        var parameters = RoutePatternFactory.Parse(route.Match.Path ?? "/{**catchall}").Parameters
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var parts = ImmutableArray.CreateBuilder<RedirectPart>();
        var sample = new StringBuilder();
        var variableHost = false;
        for (var position = 0; position < target.Length;)
        {
            var start = target.IndexOf('{', position);
            if (start < 0)
            {
                var literal = target[position..];
                if (literal.Contains('}')) throw new ArgumentException("Unmatched redirect template brace.");
                parts.Add(new(literal)); sample.Append(literal); break;
            }
            var prefix = target[position..start];
            if (prefix.Contains('}')) throw new ArgumentException("Unmatched redirect template brace.");
            parts.Add(new(prefix)); sample.Append(prefix);
            var end = target.IndexOf('}', start + 1);
            if (end < 0) throw new ArgumentException("Unmatched redirect template brace.");
            var token = target[(start + 1)..end];
            var host = authorityStart >= 0 && start >= authorityStart && end < authorityEnd;
            if (start >= queryStart || (authorityStart >= 0 && start < authorityStart))
                throw new ArgumentException("Templates are only allowed in hostnames and paths.");
            var catchAll = token.StartsWith("**", StringComparison.Ordinal);
            if (catchAll) token = token[2..];
            if (token.StartsWith("route.", StringComparison.Ordinal))
            {
                if (!parameters.TryGetValue(token[6..], out var parameter) || (catchAll && !parameter.IsCatchAll))
                    throw new ArgumentException("Redirect token must name a route parameter; ** requires a catch-all parameter.");
            }
            else if (token is not ("request.host" or "request.path") || catchAll)
                throw new ArgumentException("Unknown redirect template token.");
            if ((token == "request.host" && !host) || (token == "request.path" && host) || (host && catchAll))
                throw new ArgumentException("Redirect token is in the wrong URL component.");
            if (host)
            {
                var authorityPrefix = target[authorityStart..start];
                // Colons outside bracketed literals denote the port. Host tokens cannot enter a port.
                if (authorityPrefix.Contains(':') || authorityPrefix.Contains('@') || authorityPrefix.Contains('['))
                    throw new ArgumentException("Redirect schemes and ports must be literal.");
                variableHost = true;
            }
            parts.Add(new(token, true, host, catchAll));
            sample.Append(token == "request.path" ? "/sample" : "sample");
            position = end + 1;
        }
        ValidateTarget(sample.ToString());
        if (variableHost && hosts.Count == 0) throw new ArgumentException("Host templates require allowedHosts.");
        var result = new CompiledRedirect(options, parts.ToImmutable(), hosts, variableHost);
        // Exact source paths allow a precommit self-loop check. Dynamic paths are checked per request.
        if (route.Match.Path is { } sourcePath && !sourcePath.Contains('{'))
        {
            foreach (var host in route.Match.Hosts ?? ["redirect.invalid"])
            {
                if (host.Contains('*')) continue;
                foreach (var scheme in new[] { "http", "https" })
                {
                    var context = new DefaultHttpContext();
                    context.Request.Scheme = scheme;
                    context.Request.Host = new HostString(host);
                    context.Request.Path = route.Match.Path;
                    if (Evaluate(result, context.Request).Error == "Redirect target equals the request URL.")
                        throw new ArgumentException("Redirect target equals its source route.");
                }
            }
        }
        return result;
    }

    public static RedirectResult Evaluate(CompiledRedirect redirect, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(redirect);
        ArgumentNullException.ThrowIfNull(request);
        request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
        var options = redirect.Options;
        if (options.Mode == RedirectMode.HttpsOnly && request.IsHttps) return new("PassThrough");
        try
        {
            string target;
            if (options.Mode == RedirectMode.HttpsOnly)
            {
                var host = NormalizeHost(request.Host.Host);
                if (!redirect.Hosts.Contains(host)) throw new ArgumentException("Destination host is not allowed.");
                target = UriHelper.BuildAbsolute("https", new HostString(host, options.HttpsPort), request.PathBase, request.Path);
                if (options.HttpsPort == 443)
                    target = UriHelper.BuildAbsolute("https", new HostString(host.Contains(':') ? "[" + host + "]" : host), request.PathBase, request.Path);
            }
            else
            {
                var builder = new StringBuilder();
                foreach (var part in redirect.Parts)
                {
                    var value = part.Text;
                    if (part.Token)
                    {
                        value = part.Text switch
                        {
                            "request.host" => NormalizeHost(request.Host.Host) is var hostValue && hostValue.Contains(':') ? "[" + hostValue + "]" : hostValue,
                            "request.path" => request.PathBase.Add(request.Path).ToUriComponent(),
                            _ => request.RouteValues.TryGetValue(part.Text[6..], out var captured) && captured != null
                                ? Convert.ToString(captured, CultureInfo.InvariantCulture) ?? "" : part.CatchAll ? "" :
                                throw new ArgumentException("A required route value is missing.")
                        };
                        if (part.Host && part.Text != "request.host")
                        {
                            value = NormalizeHost(value);
                            if (value.Contains('.') || value.Contains(':')) throw new ArgumentException("A hostname capture must be one DNS label.");
                        }
                        else if (!part.Host && part.Text != "request.path")
                        {
                            if (value.Any(c => char.IsControl(c) || c == '\\'))
                                throw new ArgumentException("Route value contains unsafe characters.");
                            value = part.CatchAll
                                ? string.Join('/', value.Split('/').Select(EncodeSegment)) : EncodeSegment(value);
                        }
                    }
                    builder.Append(value);
                    if (builder.Length > MaximumUrlBytes) throw new ArgumentException("Redirect URL exceeds 8 KiB.");
                }
                target = builder.ToString();
            }
            ValidateTarget(target);
            if (redirect.VariableHost && !redirect.Hosts.Contains(NormalizeHost(new Uri(target).Host)))
                throw new ArgumentException("Destination host is not allowed.");
            if (options.QueryMode == RedirectQueryMode.Preserve)
                target = PreserveQuery(target, request.QueryString.Value);
            ValidateTarget(target);
            target = EncodeTarget(target);
            ValidateTarget(target);
            var source = new Uri(request.GetEncodedUrl());
            var destination = new Uri(source, target);
            if (SameRequest(source, destination)) throw new ArgumentException("Redirect target equals the request URL.");
            return new("Redirect", options.StatusCode, target);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return new("InvalidTarget", 400, Error: ex.Message);
        }
    }

    private static string EncodeTarget(string target)
    {
        var local = target.StartsWith('/');
        var uri = new Uri(local ? "http://redirect.invalid" + target : target);
        var path = uri.AbsolutePath + uri.Query + uri.Fragment;
        if (local) return path;
        var host = NormalizeHost(uri.Host);
        if (host.Contains(':')) host = "[" + host + "]";
        return uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + path;
    }

    private static string EncodeSegment(string value) => Uri.EscapeDataString(Uri.UnescapeDataString(value));

    private static bool SameRequest(Uri source, Uri target) =>
        source.Scheme == target.Scheme && NormalizeHost(source.Host) == NormalizeHost(target.Host) && source.Port == target.Port &&
        NormalizeEscapes(source.AbsolutePath) == NormalizeEscapes(target.AbsolutePath) &&
        NormalizeEscapes(source.Query) == NormalizeEscapes(target.Query);

    private static string NormalizeEscapes(string value)
    {
        if (!value.Contains('%')) return value;
        var chars = value.ToCharArray();
        for (var i = 0; i + 2 < chars.Length; i++)
            if (chars[i] == '%')
            {
                chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
                chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
                i += 2;
            }
        return new string(chars);
    }

    private static string PreserveQuery(string target, string? query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return target;
        var fragmentIndex = target.IndexOf('#');
        var fragment = fragmentIndex < 0 ? "" : target[fragmentIndex..];
        var prefix = fragmentIndex < 0 ? target : target[..fragmentIndex];
        var queryIndex = prefix.IndexOf('?');
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (queryIndex >= 0)
            foreach (var pair in new QueryStringEnumerable(prefix[queryIndex..])) existing.Add(pair.DecodeName().ToString());
        var additions = new List<string>();
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((separator < 0 ? pair : pair[..separator]).Replace('+', ' '));
            if (!existing.Contains(name)) additions.Add(pair);
        }
        if (additions.Count > 0) prefix += (queryIndex < 0 ? "?" : prefix.EndsWith('?') ? "" : "&") + string.Join('&', additions);
        return prefix + fragment;
    }

    private static void ValidateTarget(string target)
    {
        ValidateCharacters(target);
        if (target.StartsWith('/') && !target.StartsWith("//", StringComparison.Ordinal)) return;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Target must be a local path or an HTTP(S) URL without user information.");
        _ = NormalizeHost(uri.Host);
    }

    private static void ValidateCharacters(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaximumUrlBytes) throw new ArgumentException("Redirect URL exceeds 8 KiB.");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || c == '\\' || c == ' ') throw new ArgumentException("Redirect URL contains unsafe characters.");
            if (c != '%') continue;
            if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
                throw new ArgumentException("Redirect URL contains a malformed escape.");
            if (decoded < 32 || decoded is 92 or 127) throw new ArgumentException("Redirect URL contains an unsafe escape.");
            i += 2;
        }
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            host.IndexOfAny(['/', '\\', '@', '%', '?', '#', '*']) >= 0)
            throw new ArgumentException("Allowed hosts must be exact hostnames without ports or wildcards.");
        var plain = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(plain, out var address)) return address.ToString().ToLowerInvariant();
        if (plain.Contains(':')) throw new ArgumentException("Allowed hosts cannot contain ports.");
        var ascii = new IdnMapping().GetAscii(plain).ToLowerInvariant();
        if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) throw new ArgumentException("Invalid destination hostname.");
        return ascii;
    }
}

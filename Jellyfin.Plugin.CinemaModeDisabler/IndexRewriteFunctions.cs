using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.CinemaModeDisabler;

/// <summary>
/// Stateless rules of the middleware that injects the Cinema Mode script and style into Jellyfin Web's <c>index.html</c>.
/// </summary>
public static class IndexRewriteFunctions
{
    /// <summary>
    /// The cache policy of the rewritten page.
    /// Browsers revalidate on every load, so installing, updating or removing the plugin shows at once.
    /// </summary>
    public const string RevalidateCacheControl = "no-cache";

    /// <summary>
    /// The attribute that marks the injected elements, which also keeps a page from being injected twice.
    /// </summary>
    public const string MarkerAttribute = "data-cinema-mode-disabler";

    private const string WebDirectoryPath = "/web/";
    private const string IndexHTMLFileName = "index.html";
    private const string HeadEndTag = "</head>";
    private const string ScriptResource = "Jellyfin.Plugin.CinemaModeDisabler.Web.disable-cinema-mode.js";
    private const string StyleResource = "Jellyfin.Plugin.CinemaModeDisabler.Web.hide-cinema-mode.css";
    private const int ETagHashLength = 8;
    private const string ETagQuote = "\"";

    private static readonly UTF8Encoding StrictUTF8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] UTF8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    // NOTE: data-cfasync="false" keeps Cloudflare's Rocket Loader from deferring the script past Jellyfin Web's own startup
    private static readonly Lazy<string> InjectionText = new Lazy<string>(
        () => "<style " + MarkerAttribute + ">" + ReadResource(StyleResource) + "</style>"
            + "<script " + MarkerAttribute + " data-cfasync=\"false\">" + ReadResource(ScriptResource) + "</script>");

    /// <summary>
    /// Gets the elements inserted before <c>&lt;/head&gt;</c>: the style that hides the setting, then the script that turns it off.
    /// </summary>
    public static string Injection => InjectionText.Value;

    /// <summary>
    /// Identifies a request for Jellyfin Web's <c>index.html</c>.
    /// </summary>
    /// <param name="baseUrl">The server base URL: empty, or a path that starts with a slash and has no trailing slash.</param>
    /// <param name="requestPath">The request path, which includes the base URL.</param>
    /// <returns><c>true</c> for <c>{baseUrl}/web/</c> and <c>{baseUrl}/web/index.html</c>.</returns>
    public static bool IsIndexHTML(string baseUrl, string? requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || !requestPath.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> webPath = requestPath.AsSpan(baseUrl.Length);
        if (!webPath.StartsWith(WebDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Jellyfin serves the page for the bare web directory through its default files
        ReadOnlySpan<char> fileName = webPath[WebDirectoryPath.Length..];
        return fileName.IsEmpty || fileName.Equals(IndexHTMLFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Inserts <see cref="Injection"/> before the page's <c>&lt;/head&gt;</c>.
    /// </summary>
    /// <param name="contents">The served page.</param>
    /// <returns>The rewritten page, or <c>null</c> when the page has no head end tag or already carries the injection.</returns>
    public static string? Rewrite(string contents)
    {
        int headEnd = contents.IndexOf(HeadEndTag, StringComparison.OrdinalIgnoreCase);
        if (headEnd < 0 || contents.Contains(MarkerAttribute, StringComparison.Ordinal))
        {
            return null;
        }

        return string.Concat(contents.AsSpan(0, headEnd), Injection, contents.AsSpan(headEnd));
    }

    /// <summary>
    /// Decodes a served file as UTF-8, skipping a byte order mark.
    /// </summary>
    /// <param name="content">The served bytes.</param>
    /// <returns>The text, or <c>null</c> when the bytes are not valid UTF-8.</returns>
    public static string? DecodeUTF8(byte[] content)
    {
        int start = content.AsSpan().StartsWith(UTF8ByteOrderMark) ? UTF8ByteOrderMark.Length : 0;
        try
        {
            return StrictUTF8.GetString(content, start, content.Length - start);
        }
        catch (DecoderFallbackException)
        {
            // The caller serves the original bytes untouched
            return null;
        }
    }

    /// <summary>
    /// Encodes rewritten text as UTF-8 without a byte order mark.
    /// </summary>
    /// <param name="text">The rewritten text.</param>
    /// <returns>The bytes to serve.</returns>
    public static byte[] EncodeUTF8(string text)
    {
        return StrictUTF8.GetBytes(text);
    }

    /// <summary>
    /// Computes a strong entity tag from the served bytes.
    /// </summary>
    /// <param name="content">The bytes to serve.</param>
    /// <returns>A quoted tag of the first eight bytes of the SHA-256 hash.</returns>
    public static string ComputeETag(ReadOnlySpan<byte> content)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, hash);
        return ETagQuote + Convert.ToHexStringLower(hash[..ETagHashLength]) + ETagQuote;
    }

    /// <summary>
    /// Evaluates <c>If-None-Match</c> against the rewritten representation, with the weak comparison it requires.
    /// </summary>
    /// <param name="ifNoneMatch">The request's <c>If-None-Match</c> values.</param>
    /// <param name="etag">The quoted tag of the rewritten page.</param>
    /// <returns><c>true</c> when the client already holds this representation.</returns>
    public static bool MatchesIfNoneMatch(StringValues ifNoneMatch, string etag)
    {
        if (StringValues.IsNullOrEmpty(ifNoneMatch) || !EntityTagHeaderValue.TryParseList(ifNoneMatch, out IList<EntityTagHeaderValue>? clientTags))
        {
            return false;
        }

        EntityTagHeaderValue currentTag = new EntityTagHeaderValue(etag);
        foreach (EntityTagHeaderValue clientTag in clientTags)
        {
            if (clientTag.Equals(EntityTagHeaderValue.Any) || clientTag.Compare(currentTag, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadResource(string resourceName)
    {
        using Stream stream = typeof(IndexRewriteFunctions).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The plugin assembly lacks the embedded resource " + resourceName);
        using StreamReader reader = new StreamReader(stream, StrictUTF8);
        return reader.ReadToEnd().Trim();
    }
}

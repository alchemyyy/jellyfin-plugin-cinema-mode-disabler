using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.CinemaModeDisabler;

/// <summary>
/// Injects the Cinema Mode script and style into Jellyfin Web's <c>index.html</c>.
/// It composes with other rewriting middleware: it always calls the next layer, asks it for the complete uncompressed page, and replaces the validators of the response it changes.
/// </summary>
/// <param name="next">The next middleware.</param>
/// <param name="getBaseUrl">Returns the server base URL.</param>
/// <param name="logger">The logger.</param>
public sealed class IndexRewriteMiddleware(
    RequestDelegate next,
    Func<string> getBaseUrl,
    ILogger<IndexRewriteMiddleware> logger)
{
    /// <summary>
    /// Runs the next layer and rewrites its response to a GET of <c>index.html</c>.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the response is written.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IndexRewriteFunctions.IsIndexHTML(getBaseUrl(), context.Request.Path.Value))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Inner 304s, ranges and encodings would describe the unmodified page, so inner layers must send all of it as plain text
        IHeaderDictionary requestHeaders = context.Request.Headers;
        StringValues ifNoneMatch = requestHeaders.IfNoneMatch;
        requestHeaders.Remove(HeaderNames.AcceptEncoding);
        requestHeaders.Remove(HeaderNames.IfNoneMatch);
        requestHeaders.Remove(HeaderNames.IfModifiedSince);
        requestHeaders.Remove(HeaderNames.IfMatch);
        requestHeaders.Remove(HeaderNames.IfUnmodifiedSince);
        requestHeaders.Remove(HeaderNames.IfRange);
        requestHeaders.Remove(HeaderNames.Range);

        HttpResponse response = context.Response;
        Stream originalBody = response.Body;
        using MemoryStream bufferedBody = new MemoryStream();
        response.Body = bufferedBody;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
        }

        byte[] innerContent = bufferedBody.ToArray();
        string? rewrittenText = CanRewrite(response) ? RewriteContent(innerContent) : null;
        if (rewrittenText is null)
        {
            await originalBody.WriteAsync(innerContent, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        byte[] rewrittenContent = IndexRewriteFunctions.EncodeUTF8(rewrittenText);
        string etag = IndexRewriteFunctions.ComputeETag(rewrittenContent);
        IHeaderDictionary responseHeaders = response.Headers;
        responseHeaders.Remove(HeaderNames.LastModified);
        responseHeaders.Remove(HeaderNames.AcceptRanges);
        responseHeaders.ETag = etag;
        responseHeaders.CacheControl = IndexRewriteFunctions.RevalidateCacheControl;
        if (IndexRewriteFunctions.MatchesIfNoneMatch(ifNoneMatch, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.ContentLength = null;
            return;
        }

        response.ContentLength = rewrittenContent.Length;
        await originalBody.WriteAsync(rewrittenContent, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool CanRewrite(HttpResponse response)
    {
        // A text rewrite would corrupt an encoded body, so anything but a plain 200 passes through
        return response.StatusCode == StatusCodes.Status200OK && StringValues.IsNullOrEmpty(response.Headers.ContentEncoding);
    }

    private string? RewriteContent(byte[] content)
    {
        string? text = IndexRewriteFunctions.DecodeUTF8(content);
        if (text is null)
        {
            logger.LogWarning("Cinema Mode Disabler left index.html unchanged because it is not valid UTF-8");
            return null;
        }

        return IndexRewriteFunctions.Rewrite(text);
    }
}

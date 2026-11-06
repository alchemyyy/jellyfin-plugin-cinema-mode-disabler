using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.CinemaModeDisabler.Tests;

// Where the other rewriter sits relative to the Cinema Mode middleware
public enum StackOrder
{
    None,
    Inside,
    Outside,
}

// A stand-in for the WebGPU Player's index.html middleware, with the same contract: it always calls the next layer,
// strips the encoding, range and conditional headers, rewrites only a plain 200, and replaces the validators
public sealed class OtherRewriterMiddleware(RequestDelegate next)
{
    public const string Marker = "<script data-cfasync=\"false\">/* other rewriter */</script>";

    public async Task InvokeAsync(HttpContext context)
    {
        string? path = context.Request.Path.Value;
        if (!HttpMethods.IsGet(context.Request.Method) || (path != "/web/" && path != "/web/index.html"))
        {
            await next(context);
            return;
        }

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
            await next(context);
        }
        finally
        {
            response.Body = originalBody;
        }

        byte[] innerContent = bufferedBody.ToArray();
        if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.ContentEncoding))
        {
            await originalBody.WriteAsync(innerContent, context.RequestAborted);
            return;
        }

        string text = Encoding.UTF8.GetString(innerContent).Replace("</head>", Marker + "</head>", StringComparison.Ordinal);
        byte[] rewrittenContent = Encoding.UTF8.GetBytes(text);
        string etag = IndexRewriteFunctions.ComputeETag(rewrittenContent);
        response.Headers.Remove(HeaderNames.LastModified);
        response.Headers.Remove(HeaderNames.AcceptRanges);
        response.Headers.ETag = etag;
        response.Headers.CacheControl = "no-cache";
        if (IndexRewriteFunctions.MatchesIfNoneMatch(ifNoneMatch, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            response.ContentLength = null;
            return;
        }

        response.ContentLength = rewrittenContent.Length;
        await originalBody.WriteAsync(rewrittenContent, context.RequestAborted);
    }
}

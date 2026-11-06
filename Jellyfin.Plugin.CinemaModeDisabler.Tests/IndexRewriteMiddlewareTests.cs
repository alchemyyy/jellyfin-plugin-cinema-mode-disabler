using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Jellyfin.Plugin.CinemaModeDisabler.Tests;

// Runs the middleware in front of a pipeline shaped like Jellyfin's /web serving: base URL map, response compression, default and static files
public sealed class IndexRewriteMiddlewareTests : IDisposable
{
    private const string Page = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body></body></html>";
    private const string Script = "console.log('stock bundle');";
    private const string AcceptAnyEncoding = "gzip, deflate, br";

    private readonly string webDirectory;
    private readonly PhysicalFileProvider fileProvider;

    public IndexRewriteMiddlewareTests()
    {
        webDirectory = Directory.CreateTempSubdirectory("cinema-mode-disabler-web-").FullName;
        File.WriteAllText(Path.Combine(webDirectory, "index.html"), Page);
        File.WriteAllText(Path.Combine(webDirectory, "main.js"), Script);
        fileProvider = new PhysicalFileProvider(webDirectory);
    }

    public void Dispose()
    {
        fileProvider.Dispose();
        Directory.Delete(webDirectory, recursive: true);
    }

    [Theory]
    [InlineData("", "/web/")]
    [InlineData("", "/web/index.html")]
    [InlineData("/jellyfin", "/jellyfin/web/")]
    [InlineData("/jellyfin", "/jellyfin/web/index.html")]
    public async Task IndexHTML_CompressedRequest_GetsTheRewrittenPageWithFreshValidators(string baseUrl, string requestPath)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(baseUrl, cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await GetAsync(client, requestPath, cancellationToken);

        byte[] expected = Encoding.UTF8.GetBytes(Page.Replace("</head>", IndexRewriteFunctions.Injection + "</head>", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal(IndexRewriteFunctions.ComputeETag(expected), response.Headers.ETag?.ToString());
        Assert.Equal(IndexRewriteFunctions.RevalidateCacheControl, response.Headers.CacheControl?.ToString());
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Empty(response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task IndexHTML_CarriesTheScriptAndStyleOnce()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();

        string page = await client.GetStringAsync(new Uri("/web/index.html", UriKind.Relative), cancellationToken);

        Assert.Equal(1, CountOccurrences(page, "<script data-cinema-mode-disabler data-cfasync=\"false\">"));
        Assert.Equal(1, CountOccurrences(page, "<style data-cinema-mode-disabler>"));
        Assert.Equal(1, CountOccurrences(page, "'-enableCinemaMode'"));
        Assert.Equal(1, CountOccurrences(page, ".chkEnableCinemaMode"));
        Assert.True(page.IndexOf("data-cinema-mode-disabler", StringComparison.Ordinal) < page.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchingIfNoneMatch_ReturnsNotModifiedWithoutBody()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();
        using HttpResponseMessage first = await GetAsync(client, "/web/index.html", cancellationToken);
        string etag = first.Headers.ETag!.ToString();

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri("/web/index.html", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, etag);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task ValidatorsAndRangeOfTheStockPage_StillGetTheRewrittenPage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string stockETag;
        DateTimeOffset stockLastModified;
        await using (WebApplication stockApplication = await StartServerAsync(string.Empty, cancellationToken, installMiddleware: false))
        {
            using HttpClient stockClient = stockApplication.GetTestClient();
            using HttpResponseMessage stock = await GetAsync(stockClient, "/web/index.html", cancellationToken);
            stockETag = stock.Headers.ETag!.ToString();
            stockLastModified = stock.Content.Headers.LastModified!.Value;
        }

        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri("/web/index.html", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, stockETag);
        request.Headers.IfModifiedSince = stockLastModified;
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 9);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // A browser that cached the stock page before the install gets the rewritten one, not a stale 304 or a slice
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(IndexRewriteFunctions.Injection, await response.Content.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/web/main.js", HttpStatusCode.OK, Script)]
    [InlineData("/web/index.html.bak", HttpStatusCode.NotFound, "")]
    [InlineData("/web/other/index.html", HttpStatusCode.NotFound, "")]
    public async Task OtherFiles_PassThrough(string requestPath, HttpStatusCode expectedStatus, string expectedBody)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(requestPath, UriKind.Relative), cancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task PathOutsideTheBaseUrl_IsNotRewritten()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync("/jellyfin", cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/web/index.html", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HeadRequest_PassesThrough()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, new Uri("/web/index.html", UriKind.Relative));
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(Page), response.Content.Headers.ContentLength);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status206PartialContent)]
    public async Task NonOKInnerResponse_PassesThroughUnchanged(int statusCode)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartInnerServerAsync(
            async context =>
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsync(Page, context.RequestAborted);
            },
            cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/web/index.html", UriKind.Relative), cancellationToken);

        Assert.Equal(statusCode, (int)response.StatusCode);
        Assert.Equal(Page, await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Null(response.Headers.ETag);
    }

    [Fact]
    public async Task EncodedInnerResponse_PassesThroughUnchanged()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] encodedBody = [0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02];

        // An inner layer that compresses even without Accept-Encoding, which the middleware cannot edit
        await using WebApplication application = await StartInnerServerAsync(
            async context =>
            {
                context.Response.Headers.ContentEncoding = "gzip";
                await context.Response.Body.WriteAsync(encodedBody, context.RequestAborted);
            },
            cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/web/index.html", UriKind.Relative), cancellationToken);

        Assert.Equal(encodedBody, await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task PageWithoutHeadEndTag_PassesThroughUnchanged()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string Fragment = "<html><body>no head</body></html>";
        await File.WriteAllTextAsync(Path.Combine(webDirectory, "index.html"), Fragment, cancellationToken);
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/web/index.html", UriKind.Relative), cancellationToken);

        Assert.Equal(Fragment, await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StackedWithAnotherRewriter_BothInjectionsLand(bool outermost)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using WebApplication application = await StartServerAsync(string.Empty, cancellationToken, stackOtherRewriter: outermost ? StackOrder.Inside : StackOrder.Outside);
        using HttpClient client = application.GetTestClient();

        using HttpResponseMessage response = await GetAsync(client, "/web/", cancellationToken);
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        string page = Encoding.UTF8.GetString(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(1, CountOccurrences(page, OtherRewriterMiddleware.Marker));
        Assert.Equal(1, CountOccurrences(page, "<script data-cinema-mode-disabler"));
        Assert.Equal(IndexRewriteFunctions.ComputeETag(body), response.Headers.ETag?.ToString());

        // The outer layer's tag describes the final page, so a revalidation is a 304 whichever layer is outermost
        using HttpRequestMessage revalidation = new HttpRequestMessage(HttpMethod.Get, new Uri("/web/", UriKind.Relative));
        revalidation.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, response.Headers.ETag!.ToString());
        revalidation.Headers.AcceptEncoding.ParseAdd(AcceptAnyEncoding);
        using HttpResponseMessage notModified = await client.SendAsync(revalidation, cancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string requestPath, CancellationToken cancellationToken)
    {
        // Inner response compression would encode these responses if the middleware let the header through
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(requestPath, UriKind.Relative));
        request.Headers.AcceptEncoding.ParseAdd(AcceptAnyEncoding);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<WebApplication> StartInnerServerAsync(RequestDelegate inner, CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        WebApplication application = builder.Build();
        application.UseMiddleware<IndexRewriteMiddleware>((Func<string>)(() => string.Empty));
        application.Run(inner);
        await application.StartAsync(cancellationToken);
        return application;
    }

    private async Task<WebApplication> StartServerAsync(
        string baseUrl,
        CancellationToken cancellationToken,
        bool installMiddleware = true,
        StackOrder stackOtherRewriter = StackOrder.None)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddResponseCompression();
        WebApplication application = builder.Build();
        if (stackOtherRewriter == StackOrder.Outside)
        {
            application.UseMiddleware<OtherRewriterMiddleware>();
        }

        if (installMiddleware)
        {
            application.UseMiddleware<IndexRewriteMiddleware>((Func<string>)(() => baseUrl));
        }

        if (stackOtherRewriter == StackOrder.Inside)
        {
            application.UseMiddleware<OtherRewriterMiddleware>();
        }

        application.Map(baseUrl, mainApplication =>
        {
            mainApplication.UseResponseCompression();
            mainApplication.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider, RequestPath = "/web" });
            mainApplication.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider, RequestPath = "/web" });
        });
        await application.StartAsync(cancellationToken);
        return application;
    }
}

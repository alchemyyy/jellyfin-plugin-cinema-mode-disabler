using System;
using Xunit;

namespace Jellyfin.Plugin.CinemaModeDisabler.Tests;

public sealed class IndexRewriteFunctionsTests
{
    [Theory]
    [InlineData("", "/web/", true)]
    [InlineData("", "/web/index.html", true)]
    [InlineData("", "/WEB/Index.HTML", true)]
    [InlineData("/jellyfin", "/jellyfin/web/", true)]
    [InlineData("/jellyfin", "/jellyfin/web/index.html", true)]
    [InlineData("/jellyfin", "/web/index.html", false)]
    [InlineData("", "/web", false)]
    [InlineData("", "/web/main.js", false)]
    [InlineData("", "/web/config.json", false)]
    [InlineData("", "/web/sub/index.html", false)]
    [InlineData("", null, false)]
    public void IsIndexHTML_MatchesOnlyThePage(string baseUrl, string? requestPath, bool expected)
    {
        Assert.Equal(expected, IndexRewriteFunctions.IsIndexHTML(baseUrl, requestPath));
    }

    [Fact]
    public void Injection_IsAStyleAndANonDeferredScript()
    {
        string injection = IndexRewriteFunctions.Injection;

        Assert.StartsWith("<style data-cinema-mode-disabler>", injection, StringComparison.Ordinal);
        Assert.Contains(".chkEnableCinemaMode", injection, StringComparison.Ordinal);
        Assert.Contains("<script data-cinema-mode-disabler data-cfasync=\"false\">", injection, StringComparison.Ordinal);
        Assert.Contains("'-enableCinemaMode'", injection, StringComparison.Ordinal);
        Assert.EndsWith("</script>", injection, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_InsertsBeforeTheFirstHeadEndTag()
    {
        string rewritten = IndexRewriteFunctions.Rewrite("<html><HEAD></HEAD><body></head></body></html>")!;

        Assert.Equal("<html><HEAD>" + IndexRewriteFunctions.Injection + "</HEAD><body></head></body></html>", rewritten);
    }

    [Fact]
    public void Rewrite_AlreadyInjectedPage_IsLeftAlone()
    {
        string once = IndexRewriteFunctions.Rewrite("<head></head>")!;

        Assert.Null(IndexRewriteFunctions.Rewrite(once));
    }

    [Fact]
    public void Rewrite_PageWithoutHeadEndTag_IsLeftAlone()
    {
        Assert.Null(IndexRewriteFunctions.Rewrite("<html><body></body></html>"));
    }
}

using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fabr.Core;
using Fabr.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using OpenCaddis.Agentic;
using ReverseMarkdown;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("WebBrowser")]
public sealed class WebBrowserPlugin : IFabrPlugin
{
    private IFabrAgentHost? _host;
    private ILogger<WebBrowserPlugin> _logger = null!;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _timeoutMs = 30_000;
    private int _maxContentLength = 50_000;
    private string _screenshotPath = Path.Combine(Path.GetTempPath(), "screenshots");
    private bool _headless = true;

    public async Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<WebBrowserPlugin>>();

        var timeoutSetting = config.GetPluginSetting("WebBrowser", "TimeoutMs");
        if (int.TryParse(timeoutSetting, out var timeout))
            _timeoutMs = timeout;

        var maxLenSetting = config.GetPluginSetting("WebBrowser", "MaxContentLength");
        if (int.TryParse(maxLenSetting, out var maxLen))
            _maxContentLength = maxLen;

        var ssPath = config.GetPluginSetting("WebBrowser", "ScreenshotPath");
        if (!string.IsNullOrWhiteSpace(ssPath))
            _screenshotPath = ssPath;

        var headlessSetting = config.GetPluginSetting("WebBrowser", "Headless");
        if (bool.TryParse(headlessSetting, out var headless))
            _headless = headless;

        if (!Directory.Exists(_screenshotPath))
            Directory.CreateDirectory(_screenshotPath);

        // Auto-install Chromium if not already present
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser installation failed with exit code {exitCode}.");

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headless
        });

        _logger.LogInformation("WebBrowserPlugin initialized (headless: {Headless}, timeout: {Timeout}ms, screenshots: {ScreenshotPath})", _headless, _timeoutMs, _screenshotPath);

        var lifetime = serviceProvider.GetService<IHostApplicationLifetime>();
        lifetime?.ApplicationStopping.Register(() =>
        {
            _browser?.CloseAsync().GetAwaiter().GetResult();
            _playwright?.Dispose();
        });
    }

    // --- Tool Methods ---

    [Description("Navigate to a URL and return the page content as markdown. Use this to read web pages.")]
    public async Task<string> NavigateAndRead(
        [Description("The URL to navigate to (must be http or https)")] string url,
        [Description("Whether to wait for dynamic content to load (default false)")] bool waitForDynamic = false)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Navigating to {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url,
                waitForDynamic ? WaitUntilState.NetworkIdle : WaitUntilState.Load);

            try
            {
                var title = await page.TitleAsync();
                var html = await page.ContentAsync();
                var markdown = ConvertHtmlToMarkdown(html);

                return $"# {title}\n**URL:** {url}\n\n{markdown}";
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Page load timed out after {Timeout}ms for {Url}", _timeoutMs, url);
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "Browser operation failed for {Url}", url);
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Extract specific content from a web page using a CSS selector. Useful for targeting particular sections of a page.")]
    public async Task<string> ExtractContent(
        [Description("The URL to navigate to")] string url,
        [Description("CSS selector to extract content from (e.g. 'article', '.main-content', '#results')")] string selector,
        [Description("If true, return raw HTML instead of markdown (default false)")] bool rawHtml = false)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Extracting content from {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                var elements = await page.QuerySelectorAllAsync(selector);
                if (elements.Count == 0)
                {
                    _logger.LogDebug("No elements found for selector '{Selector}' on {Url}", selector, url);
                    return $"No elements found matching selector: {selector}";
                }

                var sb = new StringBuilder();
                foreach (var element in elements)
                {
                    var html = await element.InnerHTMLAsync();
                    if (rawHtml)
                        sb.AppendLine(html);
                    else
                        sb.AppendLine(ConvertHtmlToMarkdown(html));
                    sb.AppendLine();
                }

                return TruncateContent(sb.ToString().Trim());
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Take a screenshot of a web page and save it to disk. Returns the file path of the saved screenshot.")]
    public async Task<string> TakeScreenshot(
        [Description("The URL to navigate to")] string url,
        [Description("Whether to capture the full scrollable page (default false)")] bool fullPage = false,
        [Description("Optional CSS selector to screenshot a specific element")] string? selector = null)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Taking screenshot of {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                _logger.LogDebug("Taking screenshot of {Url} (fullPage: {FullPage}, selector: {Selector})", url, fullPage, selector ?? "(none)");
                var fileName = $"screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.png";
                var filePath = Path.Combine(_screenshotPath, fileName);

                if (!string.IsNullOrWhiteSpace(selector))
                {
                    var element = await page.QuerySelectorAsync(selector);
                    if (element is null)
                        return $"Error: No element found matching selector: {selector}";

                    await element.ScreenshotAsync(new ElementHandleScreenshotOptions { Path = filePath });
                }
                else
                {
                    await page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Path = filePath,
                        FullPage = fullPage
                    });
                }

                _logger.LogInformation("Screenshot saved: {FilePath}", filePath);
                return $"Screenshot saved to: {filePath}";
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Click an element on a web page and return the resulting page content. Useful for interacting with buttons, links, or other clickable elements.")]
    public async Task<string> ClickElement(
        [Description("The URL to navigate to")] string url,
        [Description("CSS selector of the element to click")] string selector,
        [Description("Milliseconds to wait after clicking for content to update (default 2000)")] int waitAfterClickMs = 2000)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Clicking element on {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                var element = await page.QuerySelectorAsync(selector);
                if (element is null)
                    return $"Error: No element found matching selector: {selector}";

                await element.ClickAsync();
                await page.WaitForTimeoutAsync(Math.Min(waitAfterClickMs, 10_000));

                var title = await page.TitleAsync();
                var currentUrl = page.Url;
                var html = await page.ContentAsync();
                var markdown = ConvertHtmlToMarkdown(html);

                return $"# {title}\n**URL:** {currentUrl}\n\n{markdown}";
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Fill form fields on a web page and optionally submit the form. Pass fields as a JSON object mapping CSS selectors to values.")]
    public async Task<string> FillForm(
        [Description("The URL to navigate to")] string url,
        [Description("JSON object mapping CSS selectors to values, e.g. {\"#username\": \"john\", \"#password\": \"secret\"}")] string fieldsJson,
        [Description("Optional CSS selector of a submit button to click after filling fields")] string? submitSelector = null)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Filling form on {url}...");

        Dictionary<string, string>? fields;
        try
        {
            fields = JsonSerializer.Deserialize<Dictionary<string, string>>(fieldsJson);
            if (fields is null || fields.Count == 0)
                return "Error: fieldsJson must be a non-empty JSON object mapping selectors to values.";
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON for fieldsJson: {ex.Message}";
        }

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                foreach (var (fieldSelector, value) in fields)
                {
                    var field = await page.QuerySelectorAsync(fieldSelector);
                    if (field is null)
                        return $"Error: No element found matching selector: {fieldSelector}";

                    await field.FillAsync(value);
                }

                var filledCount = fields.Count;

                if (!string.IsNullOrWhiteSpace(submitSelector))
                {
                    var submitButton = await page.QuerySelectorAsync(submitSelector);
                    if (submitButton is null)
                        return $"Error: No submit element found matching selector: {submitSelector}";

                    await submitButton.ClickAsync();
                    await page.WaitForTimeoutAsync(2000);

                    var title = await page.TitleAsync();
                    var currentUrl = page.Url;
                    var html = await page.ContentAsync();
                    var markdown = ConvertHtmlToMarkdown(html);

                    return $"Filled {filledCount} field(s) and submitted.\n\n# {title}\n**URL:** {currentUrl}\n\n{markdown}";
                }

                return $"Filled {filledCount} field(s) successfully.";
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Extract all links from a web page as a numbered list. Useful for discovering navigation options and content links.")]
    public async Task<string> GetPageLinks(
        [Description("The URL to navigate to")] string url,
        [Description("Optional CSS selector to limit link extraction to a specific section of the page")] string? scopeSelector = null)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Getting links from {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                IReadOnlyList<IElementHandle> links;
                if (!string.IsNullOrWhiteSpace(scopeSelector))
                {
                    var scope = await page.QuerySelectorAsync(scopeSelector);
                    if (scope is null)
                        return $"Error: No element found matching scope selector: {scopeSelector}";

                    links = await scope.QuerySelectorAllAsync("a[href]");
                }
                else
                {
                    links = await page.QuerySelectorAllAsync("a[href]");
                }

                if (links.Count == 0)
                    return "No links found on the page.";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {links.Count} link(s):\n");

                for (var i = 0; i < links.Count; i++)
                {
                    var href = await links[i].GetAttributeAsync("href") ?? "";
                    var text = (await links[i].InnerTextAsync()).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                        text = "(no text)";

                    // Collapse whitespace in link text
                    text = Regex.Replace(text, @"\s+", " ");
                    if (text.Length > 100)
                        text = text[..100] + "...";

                    sb.AppendLine($"{i + 1}. [{text}]({href})");
                }

                return TruncateContent(sb.ToString().Trim());
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    [Description("Execute JavaScript on a web page and return the result. Useful for extracting dynamic data or interacting with page scripts.")]
    public async Task<string> EvaluateJavaScript(
        [Description("The URL to navigate to")] string url,
        [Description("The JavaScript expression to evaluate (must return a value)")] string expression)
    {
        var error = ValidateUrl(url);
        if (error is not null)
            return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Running JavaScript on {url}...");

        try
        {
            var (context, page) = await CreatePageAndNavigateAsync(url, WaitUntilState.Load);

            try
            {
                var result = await page.EvaluateAsync(expression);
                var resultStr = result?.ToString() ?? "(null)";
                return TruncateContent(resultStr);
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        catch (TimeoutException)
        {
            return $"Error: Page load timed out after {_timeoutMs}ms for URL: {url}";
        }
        catch (PlaywrightException ex)
        {
            return $"Error: Browser operation failed: {ex.Message}";
        }
    }

    // --- Private Helpers ---

    private string? ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: URL cannot be empty.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Invalid URL format: {Url}", url);
            return $"Error: Invalid URL format: {url}";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning("Unsupported URL scheme '{Scheme}' for {Url}", uri.Scheme, url);
            return $"Error: Only http and https URLs are supported. Got: {uri.Scheme}";
        }

        var host = uri.Host.ToLowerInvariant();

        // Block localhost
        if (host is "localhost" or "127.0.0.1" or "[::1]")
        {
            _logger.LogWarning("Blocked access to localhost address: {Host}", host);
            return $"Error: Access to local addresses is not allowed: {host}";
        }

        // Block internal network ranges
        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            var isPrivate = bytes[0] switch
            {
                10 => true,
                172 => bytes[1] >= 16 && bytes[1] <= 31,
                192 => bytes[1] == 168,
                _ => false
            };
            if (isPrivate)
            {
                _logger.LogWarning("Blocked access to private network address: {Host}", host);
                return $"Error: Access to private network addresses is not allowed: {host}";
            }
        }

        // Block internal domains
        if (host.EndsWith(".local") || host.EndsWith(".internal"))
        {
            _logger.LogWarning("Blocked access to internal domain: {Host}", host);
            return $"Error: Access to internal domains is not allowed: {host}";
        }

        return null;
    }

    private async Task<(IBrowserContext context, IPage page)> CreatePageAndNavigateAsync(
        string url, WaitUntilState waitUntil)
    {
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();

        page.SetDefaultTimeout(_timeoutMs);

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = waitUntil,
            Timeout = _timeoutMs
        });

        return (context, page);
    }

    private string ConvertHtmlToMarkdown(string html)
    {
        // Strip noisy tags before conversion
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<noscript[\s\S]*?</noscript>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<svg[\s\S]*?</svg>", "", RegexOptions.IgnoreCase);

        var converter = new Converter(new Config
        {
            UnknownTags = Config.UnknownTagsOption.Drop,
            SmartHrefHandling = true,
            RemoveComments = true
        });

        var markdown = converter.Convert(html);

        // Collapse excessive blank lines
        markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n");

        return TruncateContent(markdown.Trim());
    }

    private string TruncateContent(string content)
    {
        if (content.Length <= _maxContentLength)
            return content;

        return content[.._maxContentLength] + "\n\n[Content truncated...]";
    }
}

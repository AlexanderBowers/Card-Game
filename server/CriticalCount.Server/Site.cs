using System.Reflection;
using System.Text.RegularExpressions;
using Markdig;

namespace CriticalCount.Server;

/// <summary>
/// The developer site at cloudydaygames.com (2026-10-13): a studio home page, the privacy policy,
/// the account deletion page and app-ads.txt, served by this same process through the same
/// Cloudflare Tunnel as the game. The API lives under /v1 and /ws, so nothing here collides.
///
/// Google needs all of it on a domain we own: the OAuth sign-in screen's Branding page (for cloud
/// save) only accepts a home page and privacy policy on an authorized domain, and AdMob reads
/// app-ads.txt from the developer website in the Play listing.
///
/// The privacy policy and the deletion page are rendered from the repo's own PRIVACY.md and
/// ACCOUNT_DELETION.md, compiled in as resources, so the site can never say something different
/// from the policy in the repo. Pages are rendered once at startup.
/// </summary>
public static class Site
{
    public const string AppAdsTxt = "google.com, pub-9876378155655051, DIRECT, f08c47fec0942fa0\n";

    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static void Map(WebApplication app)
    {
        string layout = Text("site.layout.html");
        string home = Text("site.home.html");
        string legal = Text("site.legal.html");
        byte[] logo = Bytes("site.logo.png");

        string homePage = Page(layout, "Cloudy Day Games", "home", home);
        string privacyPage = Page(layout, "Privacy Policy - Critical Count", "privacy",
            Legal(legal, "Privacy", "Privacy Policy", Text("site.PRIVACY.md")));
        string deletePage = Page(layout, "Delete your account - Critical Count", "delete",
            Legal(legal, "Data safety", "Delete your account", Text("site.ACCOUNT_DELETION.md")));

        app.MapGet("/", () => Html(homePage));
        app.MapGet("/privacy", () => Html(privacyPage));
        app.MapGet("/delete-account", () => Html(deletePage));
        app.MapGet("/app-ads.txt", () => Results.Text(AppAdsTxt, "text/plain; charset=utf-8"));
        app.MapGet("/logo.png", () => Results.Bytes(logo, "image/png"));
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nAllow: /\nDisallow: /v1/\n", "text/plain; charset=utf-8"));
    }

    private static IResult Html(string page) => Results.Content(page, "text/html; charset=utf-8");

    private static string Page(string layout, string title, string current, string content) =>
        layout.Replace("{{TITLE}}", title)
              .Replace("{{NAV_HOME}}", current == "home" ? "active" : "")
              .Replace("{{NAV_PRIVACY}}", current == "privacy" ? "active" : "")
              .Replace("{{NAV_DELETE}}", current == "delete" ? "active" : "")
              .Replace("{{CONTENT}}", content);

    private static string Legal(string legal, string badge, string heading, string markdown) =>
        legal.Replace("{{BADGE}}", badge)
             .Replace("{{HEADING}}", heading)
             .Replace("{{BODY}}", RenderLegal(markdown));

    /// <summary>The markdown as page body: its own top heading dropped (the page header says it)
    /// and links to the repo copies of these two pages pointed at the site's own.</summary>
    public static string RenderLegal(string markdown)
    {
        string md = markdown.Replace("\r\n", "\n");
        md = Regex.Replace(md, @"\A\s*#[^#\n][^\n]*\n", "");
        // "**Effective date:** ...", "**Developer:** ..." on consecutive lines stay on their own lines.
        md = Regex.Replace(md, @"^(\*\*[^*\n]+:\*\*[^\n]*)\n(?=\*\*[^*\n]+:\*\*)", "$1  \n", RegexOptions.Multiline);
        md = Regex.Replace(md, @"https://github\.com/AlexanderBowers/Card-Game/blob/master/PRIVACY\.md", "https://cloudydaygames.com/privacy");
        md = Regex.Replace(md, @"https://github\.com/AlexanderBowers/Card-Game/blob/master/ACCOUNT_DELETION\.md", "https://cloudydaygames.com/delete-account");
        return Markdig.Markdown.ToHtml(md, Markdown);
    }

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Missing site resource {name}");

    private static string Text(string name)
    {
        using var reader = new StreamReader(Resource(name));
        return reader.ReadToEnd();
    }

    private static byte[] Bytes(string name)
    {
        using var stream = Resource(name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Xml.Dom;
using AngleSharp.Xml.Parser;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ganss.XSS
{
    /// <summary>
    /// Cleans Xml documents and fragments from constructs that can lead to <a href="https://en.wikipedia.org/wiki/Cross-site_scripting">XSS attacks</a>.
    /// </summary>
    /// <remarks>
    /// XSS attacks can occur at several levels within an Xml document or fragment:
    /// <list type="bullet">
    /// <item>Xml Tags (e.g. the &lt;script&gt; tag)</item>
    /// <item>Xml attributes (e.g. the "onload" attribute)</item>
    /// <item>CSS styles (url property values)</item>
    /// <item>malformed Xml or Xml that exploits parser bugs in specific browsers</item>
    /// </list>
    /// <para>
    /// The XmlSanitizer class addresses all of these possible attack vectors by using a sophisticated Xml parser (<a href="https://github.com/AngleSharp/AngleSharp">AngleSharp</a>).
    /// </para>
    /// <para>
    /// In order to facilitate different use cases, XmlSanitizer can be customized at the levels mentioned above:
    /// <list type="bullet">
    /// <item>You can specify the allowed Xml tags through the property <see cref="AllowedTags"/>. All other tags will be stripped.</item>
    /// <item>You can specify the allowed Xml attributes through the property <see cref="AllowedAttributes"/>. All other attributes will be stripped.</item>
    /// <item>You can specify the allowed CSS property names through the property <see cref="AllowedCssProperties"/>. All other styles will be stripped.</item>
    /// <item>You can specify the allowed URI schemes through the property <see cref="AllowedSchemes"/>. All other URIs will be stripped.</item>
    /// <item>You can specify the Xml attributes that contain URIs (such as "src", "href" etc.) through the property <see cref="UriAttributes"/>.</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// <![CDATA[
    /// var sanitizer = new XmlSanitizer();
    /// var Xml = @"<script>alert('xss')</script><div onload=""alert('xss')"" style=""background-color: test"">Test<img src=""test.gif"" style=""background-image: url(javascript:alert('xss')); margin: 10px""></div>";
    /// var sanitized = sanitizer.Sanitize(Xml, "http://www.example.com");
    /// // -> "<div style="background-color: test">Test<img style="margin: 10px" src="http://www.example.com/test.gif"></div>"
    /// ]]>
    /// </code>
    /// </example>
    public class XmlSanitizer : IXmlSanitizer
    {
        // from http://genshi.edgewall.org/
        private static readonly Regex CssUnicodeEscapes = new(@"\\([0-9a-fA-F]{1,6})\s?|\\([^\r\n\f0-9a-fA-F'""{};:()#*])", RegexOptions.Compiled);
        private static readonly Regex CssComments = new(@"/\*.*?\*/", RegexOptions.Compiled);
        // IE6 <http://heideri.ch/jso/#80>
        private static readonly Regex CssExpression = new(@"[eE\uFF25\uFF45][xX\uFF38\uFF58][pP\uFF30\uFF50][rR\u0280\uFF32\uFF52][eE\uFF25\uFF45][sS\uFF33\uFF53]{2}[iI\u026A\uFF29\uFF49][oO\uFF2F\uFF4F][nN\u0274\uFF2E\uFF4E]", RegexOptions.Compiled);
        private static readonly Regex CssUrl = new(@"[Uu][Rr\u0280][Ll\u029F]\s*\(\s*(['""]?)\s*([^'"")\s]+)\s*(['""]?)\s*", RegexOptions.Compiled);
        private static readonly IConfiguration defaultConfiguration = Configuration.Default.WithCss(new CssParserOptions
        {
            IsIncludingUnknownDeclarations = true,
            IsIncludingUnknownRules = true,
            IsToleratingInvalidSelectors = true,
        });

        private static readonly XmlParser defaultXmlParser = new(new XmlParserOptions(), BrowsingContext.New(defaultConfiguration));

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlSanitizer"/> class.
        /// </summary>
        /// <param name="allowedTags">The allowed tag names such as "a" and "div". When <c>null</c>, uses <see cref="DefaultAllowedTags"/></param>
        /// <param name="allowedSchemes">The allowed HTTP schemes such as "http" and "https". When <c>null</c>, uses <see cref="DefaultAllowedSchemes"/></param>
        /// <param name="allowedAttributes">The allowed Xml attributes such as "href" and "alt". When <c>null</c>, uses <see cref="DefaultAllowedAttributes"/></param>
        /// <param name="uriAttributes">The Xml attributes that can contain a URI such as "href". When <c>null</c>, uses <see cref="DefaultUriAttributes"/></param>
        /// <param name="allowedCssProperties">The allowed CSS properties such as "font" and "margin". When <c>null</c>, uses <see cref="DefaultAllowedCssProperties"/></param>
        public XmlSanitizer(IEnumerable<string>? allowedTags = null, IEnumerable<string>? allowedSchemes = null,
            IEnumerable<string>? allowedAttributes = null, IEnumerable<string>? uriAttributes = null, IEnumerable<string>? allowedCssProperties = null)
        {
            AllowedTags = new HashSet<string>(allowedTags ?? DefaultAllowedTags, StringComparer.OrdinalIgnoreCase);
            AllowedSchemes = new HashSet<string>(allowedSchemes ?? DefaultAllowedSchemes, StringComparer.OrdinalIgnoreCase);
            AllowedAttributes = new HashSet<string>(allowedAttributes ?? DefaultAllowedAttributes, StringComparer.OrdinalIgnoreCase);
            UriAttributes = new HashSet<string>(uriAttributes ?? DefaultUriAttributes, StringComparer.OrdinalIgnoreCase);
            AllowedCssProperties = new HashSet<string>(allowedCssProperties ?? DefaultAllowedCssProperties, StringComparer.OrdinalIgnoreCase);
            AllowedAtRules = new HashSet<CssRuleType>(DefaultAllowedAtRules);
            AllowedClasses = new HashSet<string>(DefaultAllowedClasses, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets or sets the default value indicating whether to keep child nodes of elements that are removed. Default is false.
        /// </summary>
        public static bool DefaultKeepChildNodes { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether to keep child nodes of elements that are removed. Default is <see cref="DefaultKeepChildNodes"/>.
        /// </summary>
        public bool KeepChildNodes { get; set; } = DefaultKeepChildNodes;

        /// <summary>
        /// Gets or sets the default <see cref="Func{XmlParser}"/> object that creates the parser used for parsing the input.
        /// </summary>
        public static Func<XmlParser> DefaultXmlParserFactory { get; set; } = () => defaultXmlParser;

        /// <summary>
        /// Gets or sets the <see cref="Func{XmlParser}"/> object the creates the parser used for parsing the input.
        /// </summary>
        public Func<XmlParser> XmlParserFactory { get; set; } = DefaultXmlParserFactory;

        /// <summary>
        /// Gets or sets the default <see cref="IMarkupFormatter"/> object used for generating output. Default is <see cref="XmlFormatter.Instance"/>.
        /// </summary>
        public static IMarkupFormatter DefaultOutputFormatter { get; set; } = XmlFormatter.Instance;

        /// <summary>
        /// Gets or sets the <see cref="IMarkupFormatter"/> object used for generating output. Default is <see cref="DefaultOutputFormatter"/>.
        /// </summary>
        public IMarkupFormatter OutputFormatter { get; set; } = DefaultOutputFormatter;

        /// <summary>
        /// Gets or sets the allowed CSS at-rules such as "@media" and "@font-face".
        /// </summary>
        /// <value>
        /// The allowed CSS at-rules.
        /// </value>
        public ISet<CssRuleType> AllowedAtRules { get; private set; }

        /// <summary>
        /// The default allowed CSS at-rules.
        /// </summary>
        public static ISet<CssRuleType> DefaultAllowedAtRules { get; } = new HashSet<CssRuleType>() { CssRuleType.Style, CssRuleType.Namespace };

        /// <summary>
        /// Gets or sets the allowed HTTP schemes such as "http" and "https".
        /// </summary>
        /// <value>
        /// The allowed HTTP schemes.
        /// </value>
        public ISet<string> AllowedSchemes { get; private set; }

        /// <summary>
        /// The default allowed URI schemes.
        /// </summary>
        public static ISet<string> DefaultAllowedSchemes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" };

        /// <summary>
        /// Gets or sets the allowed Xml tag names such as "a" and "div".
        /// </summary>
        /// <value>
        /// The allowed tag names.
        /// </value>
        public ISet<string> AllowedTags { get; private set; }

        /// <summary>
        /// The default allowed Xml tag names.
        /// </summary>
        public static ISet<string> DefaultAllowedTags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "svg",
            "altglyph",
            "altglyphdef",
            "altglyphitem",
            "animatecolor",
            "animatemotion",
            "animatetransform",
            "circle",
            "clippath",
            "defs",
            "desc",
            "ellipse",
            "filter",
            "font",
            "g",
            "glyph",
            "glyphref",
            "hkern",
            "image",
            "line",
            "lineargradient",
            "marker",
            "mask",
            "metadata",
            "mpath",
            "path",
            "pattern",
            "polygon",
            "polyline",
            "radialgradient",
            "rect",
            "stop",
            "switch",
            "symbol",
            "text",
            "textpath",
            "title",
            "tref",
            "tspan",
            "use",
            "view",
            "vkern",
        };

        /// <summary>
        /// Gets or sets the allowed Xml attributes such as "href" and "alt".
        /// </summary>
        /// <value>
        /// The allowed Xml attributes.
        /// </value>
        public ISet<string> AllowedAttributes { get; private set; }

        /// <summary>
        /// Allow all Xml5 data attributes; the attributes prefixed with data-
        /// </summary>
        public bool AllowDataAttributes { get; set; }

        /// <summary>
        /// The default allowed Xml attributes.
        /// </summary>
        public static ISet<string> DefaultAllowedAttributes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "accent-height",
            "accumulate",
            "additive",
            "alignment-baseline",
            "ascent",
            "attributename",
            "attributetype",
            "azimuth",
            "basefrequency",
            "baseline-shift",
            "begin",
            "bias",
            "by",
            "class",
            "clip",
            "clippathunits",
            "clip-path",
            "clip-rule",
            "color",
            "color-interpolation",
            "color-interpolation-filters",
            "color-profile",
            "color-rendering",
            "cx",
            "cy",
            "d",
            "dx",
            "dy",
            "diffuseconstant",
            "direction",
            "display",
            "divisor",
            "dur",
            "edgemode",
            "elevation",
            "end",
            "fill",
            "fill-opacity",
            "fill-rule",
            "filter",
            "filterunits",
            "flood-color",
            "flood-opacity",
            "font-family",
            "font-size",
            "font-size-adjust",
            "font-stretch",
            "font-style",
            "font-variant",
            "font-weight",
            "fx",
            "fy",
            "g1",
            "g2",
            "glyph-name",
            "glyphref",
            "gradientunits",
            "gradienttransform",
            "height",
            "href",
            "id",
            "image-rendering",
            "in",
            "in2",
            "k",
            "k1",
            "k2",
            "k3",
            "k4",
            "kerning",
            "keypoints",
            "keysplines",
            "keytimes",
            "lang",
            "lengthadjust",
            "letter-spacing",
            "kernelmatrix",
            "kernelunitlength",
            "lighting-color",
            "local",
            "marker-end",
            "marker-mid",
            "marker-start",
            "markerheight",
            "markerunits",
            "markerwidth",
            "maskcontentunits",
            "maskunits",
            "max",
            "mask",
            "media",
            "method",
            "mode",
            "min",
            "name",
            "numoctaves",
            "offset",
            "operator",
            "opacity",
            "order",
            "orient",
            "orientation",
            "origin",
            "overflow",
            "paint-order",
            "path",
            "pathlength",
            "patterncontentunits",
            "patterntransform",
            "patternunits",
            "points",
            "preservealpha",
            "preserveaspectratio",
            "primitiveunits",
            "r",
            "rx",
            "ry",
            "radius",
            "refx",
            "refy",
            "repeatcount",
            "repeatdur",
            "restart",
            "result",
            "rotate",
            "scale",
            "seed",
            "shape-rendering",
            "specularconstant",
            "specularexponent",
            "spreadmethod",
            "startoffset",
            "stddeviation",
            "stitchtiles",
            "stop-color",
            "stop-opacity",
            "stroke-dasharray",
            "stroke-dashoffset",
            "stroke-linecap",
            "stroke-linejoin",
            "stroke-miterlimit",
            "stroke-opacity",
            "stroke",
            "stroke-width",
            "style",
            "surfacescale",
            "systemlanguage",
            "tabindex",
            "targetx",
            "targety",
            "transform",
            "text-anchor",
            "text-decoration",
            "text-rendering",
            "textlength",
            "type",
            "u1",
            "u2",
            "unicode",
            "values",
            "viewbox",
            "visibility",
            "version",
            "vert-adv-y",
            "vert-origin-x",
            "vert-origin-y",
            "width",
            "word-spacing",
            "wrap",
            "writing-mode",
            "xchannelselector",
            "ychannelselector",
            "x",
            "x1",
            "x2",
            "xmlns",
            "y",
            "y1",
            "y2",
            "z",
            "zoomandpan",
        };

        /// <summary>
        /// Gets or sets the Xml attributes that can contain a URI such as "href".
        /// </summary>
        /// <value>
        /// The URI attributes.
        /// </value>
        public ISet<string> UriAttributes { get; private set; }

        /// <summary>
        /// The default URI attributes.
        /// </summary>
        public static ISet<string> DefaultUriAttributes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "action", "background", "dynsrc", "href", "lowsrc", "src" };

        /// <summary>
        /// Gets or sets the allowed CSS properties such as "font" and "margin".
        /// </summary>
        /// <value>
        /// The allowed CSS properties.
        /// </value>
        public ISet<string> AllowedCssProperties { get; private set; }

        /// <summary>
        /// The default allowed CSS properties.
        /// </summary>
        public static ISet<string> DefaultAllowedCssProperties { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // CSS 3 properties <http://www.w3.org/TR/CSS/#properties>
            "background",
            "background-attachment",
            "background-clip",
            "background-color",
            "background-image",
            "background-origin",
            "background-position",
            "background-position-x",
            "background-position-y",
            "background-repeat",
            "background-repeat-x", // see https://github.com/mganss/XmlSanitizer/issues/243
            "background-repeat-y",
            "background-size",
            "border",
            "border-bottom",
            "border-bottom-color",
            "border-bottom-left-radius",
            "border-bottom-right-radius",
            "border-bottom-style",
            "border-bottom-width",
            "border-collapse",
            "border-color",
            "border-image",
            "border-image-outset",
            "border-image-repeat",
            "border-image-slice",
            "border-image-source",
            "border-image-width",
            "border-left",
            "border-left-color",
            "border-left-style",
            "border-left-width",
            "border-radius",
            "border-right",
            "border-right-color",
            "border-right-style",
            "border-right-width",
            "border-spacing",
            "border-style",
            "border-top",
            "border-top-color",
            "border-top-left-radius",
            "border-top-right-radius",
            "border-top-style",
            "border-top-width",
            "border-width",
            "bottom",
            "caption-side",
            "clear",
            "clip",
            "color",
            "content",
            "counter-increment",
            "counter-reset",
            "cursor",
            "direction",
            "display",
            "empty-cells",
            "float",
            "font",
            "font-family",
            "font-feature-settings",
            "font-kerning",
            "font-language-override",
            "font-size",
            "font-size-adjust",
            "font-stretch",
            "font-style",
            "font-synthesis",
            "font-variant",
            "font-variant-alternates",
            "font-variant-caps",
            "font-variant-east-asian",
            "font-variant-ligatures",
            "font-variant-numeric",
            "font-variant-position",
            "font-weight",
            "height",
            "left",
            "letter-spacing",
            "line-height",
            "list-style",
            "list-style-image",
            "list-style-position",
            "list-style-type",
            "margin",
            "margin-bottom",
            "margin-left",
            "margin-right",
            "margin-top",
            "max-height",
            "max-width",
            "min-height",
            "min-width",
            "opacity",
            "orphans",
            "outline",
            "outline-color",
            "outline-offset",
            "outline-style",
            "outline-width",
            "overflow",
            "overflow-wrap",
            "overflow-x",
            "overflow-y",
            "padding",
            "padding-bottom",
            "padding-left",
            "padding-right",
            "padding-top",
            "page-break-after",
            "page-break-before",
            "page-break-inside",
            "quotes",
            "right",
            "table-layout",
            "text-align",
            "text-decoration",
            "text-decoration-color",
            "text-decoration-line",
            "text-decoration-skip",
            "text-decoration-style",
            "text-indent",
            "text-transform",
            "top",
            "unicode-bidi",
            "vertical-align",
            "visibility",
            "white-space",
            "widows",
            "width",
            "word-spacing",
            "z-index"
        };

        /// <summary>
        /// Gets or sets a regex that must not match for legal CSS property values.
        /// </summary>
        /// <value>
        /// The regex.
        /// </value>
        public Regex DisallowCssPropertyValue { get; set; } = DefaultDisallowedCssPropertyValue;

        /// <summary>
        /// The default allowed CSS classes.
        /// </summary>
        public static ISet<string> DefaultAllowedClasses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the allowed CSS classes. If the set is empty, all classes will be allowed.
        /// </summary>
        /// <value>
        /// The allowed CSS classes. An empty set means all classes are allowed.
        /// </value>
        public ISet<string> AllowedClasses { get; private set; }

        /// <summary>
        /// Occurs after sanitizing the document and post processing nodes.
        /// </summary>
        public event EventHandler<PostProcessDomEventArgs>? PostProcessDom;
        /// <summary>
        /// Occurs for every node after sanitizing.
        /// </summary>
        public event EventHandler<PostProcessNodeEventArgs>? PostProcessNode;
        /// <summary>
        /// Occurs before a tag is removed.
        /// </summary>
        public event EventHandler<RemovingTagEventArgs>? RemovingTag;
        /// <summary>
        /// Occurs before an attribute is removed.
        /// </summary>
        public event EventHandler<RemovingAttributeEventArgs>? RemovingAttribute;
        /// <summary>
        /// Occurs before a style is removed.
        /// </summary>
        public event EventHandler<RemovingStyleEventArgs>? RemovingStyle;
        /// <summary>
        /// Occurs before an at-rule is removed.
        /// </summary>
        public event EventHandler<RemovingAtRuleEventArgs>? RemovingAtRule;
        /// <summary>
        /// Occurs before a comment is removed.
        /// </summary>
        public event EventHandler<RemovingCommentEventArgs>? RemovingComment;
        /// <summary>
        /// Occurs before a CSS class is removed.
        /// </summary>
        public event EventHandler<RemovingCssClassEventArgs>? RemovingCssClass;
        /// <summary>
        /// Occurs when a URL is being sanitized.
        /// </summary>
        public event EventHandler<FilterUrlEventArgs>? FilterUrl;

        /// <summary>
        /// Raises the <see cref="E:PostProcessDom" /> event.
        /// </summary>
        /// <param name="e">The <see cref="PostProcessDomEventArgs"/> instance containing the event data.</param>
        protected virtual void OnPostProcessDom(PostProcessDomEventArgs e)
        {
            PostProcessDom?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:PostProcessNode" /> event.
        /// </summary>
        /// <param name="e">The <see cref="PostProcessNodeEventArgs"/> instance containing the event data.</param>
        protected virtual void OnPostProcessNode(PostProcessNodeEventArgs e)
        {
            PostProcessNode?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingTag" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingTagEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingTag(RemovingTagEventArgs e)
        {
            RemovingTag?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingAttribute" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingAttributeEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingAttribute(RemovingAttributeEventArgs e)
        {
            RemovingAttribute?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingStyle" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingStyleEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingStyle(RemovingStyleEventArgs e)
        {
            RemovingStyle?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingAtRule" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingAtRuleEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingAtRule(RemovingAtRuleEventArgs e)
        {
            RemovingAtRule?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingComment" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingCommentEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingComment(RemovingCommentEventArgs e)
        {
            RemovingComment?.Invoke(this, e);
        }

        /// <summary>
        /// The default regex for disallowed CSS property values.
        /// </summary>
        public static readonly Regex DefaultDisallowedCssPropertyValue = new(@"[<>]", RegexOptions.Compiled);

        /// <summary>
        /// Raises the <see cref="E:RemovingCSSClass" /> event.
        /// </summary>
        /// <param name="e">The <see cref="RemovingCssClassEventArgs"/> instance containing the event data.</param>
        protected virtual void OnRemovingCssClass(RemovingCssClassEventArgs e)
        {
            RemovingCssClass?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the <see cref="E:RemovingUrl" /> event.
        /// </summary>
        /// <param name="e">The <see cref="FilterUrlEventArgs"/> instance containing the event data.</param>
        protected virtual void OnFilteringUrl(FilterUrlEventArgs e)
        {
            FilterUrl?.Invoke(this, e);
        }

        /// <summary>
        /// Return all nested subnodes of a node.
        /// </summary>
        /// <param name="dom">The root node.</param>
        /// <returns>All nested subnodes.</returns>
        private static IEnumerable<INode> GetAllNodes(INode? dom)
        {
            if (dom == null) yield break;

            foreach (var node in dom.ChildNodes)
            {
                yield return node;
                foreach (var child in GetAllNodes(node).Where(c => c != null))
                {
                    yield return child;
                }
            }
        }

        /// <summary>
        /// Sanitizes the specified Xml body fragment. If a document is given, only the body part will be returned.
        /// </summary>
        /// <param name="Xml">The Xml body fragment to sanitize.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against. No resolution if empty.</param>
        /// <param name="outputFormatter">The formatter used to render the DOM. Using the <see cref="OutputFormatter"/> if null.</param>
        /// <returns>The sanitized Xml body fragment.</returns>
        public string Sanitize(string Xml, string baseUrl = "", IMarkupFormatter? outputFormatter = null)
        {
            using var dom = SanitizeDom(Xml, baseUrl);
            if (dom == null) return string.Empty;
            var output = dom.ChildNodes.ToHtml(outputFormatter ?? OutputFormatter);
            return output;
        }

        /// <summary>
        /// Sanitizes the specified Xml body fragment. If a document is given, only the body part will be returned.
        /// </summary>
        /// <param name="Xml">The Xml body fragment to sanitize.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against. No resolution if empty.</param>
        /// <returns>The sanitized Xml document.</returns>
        public IXmlDocument SanitizeDom(string Xml, string baseUrl = "")
        {
            var parser = XmlParserFactory();
            var dom = parser.ParseDocument(Xml);

            if (dom != null)
                DoSanitize(dom, dom, baseUrl);

            // TODO - retirer le !
            return dom!;
        }

        /// <summary>
        /// Sanitizes the specified parsed Xml body fragment.
        /// If the document has not been parsed with CSS support then all styles will be removed.
        /// </summary>
        /// <param name="document">The parsed Xml document.</param>
        /// <param name="context">The node within which to sanitize.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against. No resolution if empty.</param>
        /// <returns>The sanitized Xml document.</returns>
        public IXmlDocument SanitizeDom(IXmlDocument document, IElement? context = null, string baseUrl = "")
        {
            DoSanitize(document, context ?? (IParentNode)document, baseUrl);
            return document;
        }

        /// <summary>
        /// Sanitizes the specified Xml document. Even if only a fragment is given, a whole document will be returned.
        /// </summary>
        /// <param name="Xml">The Xml document to sanitize.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against. No resolution if empty.</param>
        /// <param name="outputFormatter">The formatter used to render the DOM. Using the <see cref="OutputFormatter"/> if null.</param>
        /// <returns>The sanitized Xml document.</returns>
        public string SanitizeDocument(string Xml, string baseUrl = "", IMarkupFormatter? outputFormatter = null)
        {
            var parser = XmlParserFactory();
            var dom = parser.ParseDocument(Xml);

            DoSanitize(dom, dom, baseUrl);

            var output = dom.ToHtml(outputFormatter ?? OutputFormatter);

            return output;
        }

        /// <summary>
        /// Sanitizes the specified Xml document. Even if only a fragment is given, a whole document will be returned.
        /// </summary>
        /// <param name="Xml">The Xml document to sanitize.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against. No resolution if empty.</param>
        /// <param name="outputFormatter">The formatter used to render the DOM. Using the <see cref="OutputFormatter"/> if null.</param>
        /// <returns>The sanitized Xml document.</returns>
        public string SanitizeDocument(Stream Xml, string baseUrl = "", IMarkupFormatter? outputFormatter = null)
        {
            var parser = XmlParserFactory();
            var dom = parser.ParseDocument(Xml);

            DoSanitize(dom, dom, baseUrl);

            var output = dom.ToHtml(outputFormatter ?? OutputFormatter);

            return output;
        }

        /// <summary>
        /// Removes all comment nodes from a list of nodes.
        /// </summary>
        /// <param name="context">The node within which to remove comments.</param>
        /// <returns><c>true</c> if any comments were removed; otherwise, <c>false</c>.</returns>
        private void RemoveComments(INode? context)
        {
            foreach (var comment in GetAllNodes(context).OfType<IComment>().ToList())
            {
                var e = new RemovingCommentEventArgs(comment);
                OnRemovingComment(e);
                if (!e.Cancel)
                    comment.Remove();
            }
        }

        private void DoSanitize(IXmlDocument dom, IParentNode context, string baseUrl = "")
        {
            // remove disallowed tags
            foreach (var tag in context.QuerySelectorAll("*").Where(t => !IsAllowedTag(t)).ToList())
            {
                RemoveTag(tag, RemoveReason.NotAllowedTag);
            }

            SanitizeStyleSheets(dom, baseUrl);

            // cleanup attributes
            foreach (var tag in context.QuerySelectorAll("*").ToList())
            {
                // remove disallowed attributes
                foreach (var attribute in tag.Attributes.Where(a => !IsAllowedAttribute(a)).ToList())
                {
                    RemoveAttribute(tag, attribute, RemoveReason.NotAllowedAttribute);
                }

                // sanitize URLs in URL-marked attributes
                foreach (var attribute in tag.Attributes.Where(IsUriAttribute).ToList())
                {
                    var url = SanitizeUrl(tag, attribute.Value, baseUrl);
                    if (url == null)
                        RemoveAttribute(tag, attribute, RemoveReason.NotAllowedUrlValue);
                    else
                        tag.SetAttribute(attribute.Name, url);
                }

                // sanitize the style attribute
                var oldStyleEmpty = string.IsNullOrEmpty(tag.GetAttribute("style"));
                SanitizeStyle(tag, baseUrl);

                // sanitize the value of the attributes
                foreach (var attribute in tag.Attributes.ToList())
                {
                    // The '& Javascript include' is a possible method to execute Javascript and can lead to XSS.
                    // (see https://www.owasp.org/index.php/XSS_Filter_Evasion_Cheat_Sheet#.26_JavaScript_includes)
                    if (attribute.Value.Contains("&{"))
                    {
                        RemoveAttribute(tag, attribute, RemoveReason.NotAllowedValue);
                    }
                    else
                    {
                        if (AllowedClasses.Any() && attribute.Name == "class")
                        {
                            var removedClasses = tag.ClassList.Except(AllowedClasses).ToArray();

                            foreach (var removedClass in removedClasses)
                                RemoveCssClass(tag, removedClass, RemoveReason.NotAllowedCssClass);

                            if (!tag.ClassList.Any())
                                RemoveAttribute(tag, attribute, RemoveReason.ClassAttributeEmpty);
                        }
                        else if (!oldStyleEmpty && attribute.Name == "style" && string.IsNullOrEmpty(attribute.Value))
                        {
                            RemoveAttribute(tag, attribute, RemoveReason.StyleAttributeEmpty);
                        }
                    }
                }
            }

            RemoveComments(context as INode);

            DoPostProcess(dom, context as INode);
        }

        private void SanitizeStyleSheets(IXmlDocument dom, string baseUrl)
        {
            foreach (var styleSheet in dom.StyleSheets.OfType<ICssStyleSheet>())
            {
                var styleTag = styleSheet.OwnerNode;

                for (int i = 0; i < styleSheet.Rules.Length;)
                {
                    var rule = styleSheet.Rules[i];
                    if (!SanitizeStyleRule(rule, styleTag, baseUrl) && RemoveAtRule(styleTag, rule))
                        styleSheet.RemoveAt(i);
                    else i++;
                }

                styleTag.InnerHtml = styleSheet.ToCss().Replace("<", "\\3c");
            }
        }

        private bool SanitizeStyleRule(ICssRule rule, IElement styleTag, string baseUrl)
        {
            if (!AllowedAtRules.Contains(rule.Type)) return false;

            if (rule is ICssStyleRule styleRule)
            {
                SanitizeStyleDeclaration(styleTag, styleRule.Style, baseUrl);
            }
            else
            {
                if (rule is ICssGroupingRule groupingRule)
                {
                    for (int i = 0; i < groupingRule.Rules.Length;)
                    {
                        var childRule = groupingRule.Rules[i];
                        if (!SanitizeStyleRule(childRule, styleTag, baseUrl) && RemoveAtRule(styleTag, childRule))
                            groupingRule.RemoveAt(i);
                        else i++;
                    }
                }
                else if (rule is ICssPageRule pageRule)
                {
                    SanitizeStyleDeclaration(styleTag, pageRule.Style, baseUrl);
                }
                else if (rule is ICssKeyframesRule keyFramesRule)
                {
                    foreach (var childRule in keyFramesRule.Rules.OfType<ICssKeyframeRule>().ToList())
                    {
                        if (!SanitizeStyleRule(childRule, styleTag, baseUrl) && RemoveAtRule(styleTag, childRule))
                            keyFramesRule.Remove(childRule.KeyText);
                    }
                }
                else if (rule is ICssKeyframeRule keyFrameRule)
                {
                    SanitizeStyleDeclaration(styleTag, keyFrameRule.Style, baseUrl);
                }
            }

            return true;
        }

        /// <summary>
        /// Performs post processing on all nodes in the document.
        /// </summary>
        /// <param name="dom">The Xml document.</param>
        /// <param name="context">The node within which to post process all nodes.</param>
        private void DoPostProcess(IXmlDocument dom, INode? context)
        {
            if (PostProcessNode != null)
            {
                dom.Normalize();
                var nodes = GetAllNodes(context).ToList();

                foreach (var node in nodes)
                {
                    var e = new PostProcessNodeEventArgs(dom, node);
                    OnPostProcessNode(e);
                    if (e.ReplacementNodes.Any())
                    {
                        ((IChildNode)node).Replace(e.ReplacementNodes.ToArray());
                    }
                }
            }

            if (PostProcessDom != null)
            {
                var e = new PostProcessDomEventArgs(dom);
                OnPostProcessDom(e);
            }
        }

        /// <summary>
        /// Determines whether the specified attribute can contain a URI.
        /// </summary>
        /// <param name="attribute">The attribute.</param>
        /// <returns><c>true</c> if the attribute can contain a URI; otherwise, <c>false</c>.</returns>
        private bool IsUriAttribute(IAttr attribute)
        {
            return UriAttributes.Contains(attribute.Name);
        }

        /// <summary>
        /// Determines whether the specified tag is allowed.
        /// </summary>
        /// <param name="tag">The tag.</param>
        /// <returns><c>true</c> if the tag is allowed; otherwise, <c>false</c>.</returns>
        private bool IsAllowedTag(IElement tag)
        {
            return AllowedTags.Contains(tag.NodeName);
        }

        /// <summary>
        /// Determines whether the specified attribute is allowed.
        /// </summary>
        /// <param name="attribute">The attribute.</param>
        /// <returns><c>true</c> if the attribute is allowed; otherwise, <c>false</c>.</returns>
        private bool IsAllowedAttribute(IAttr attribute)
        {
            return AllowedAttributes.Contains(attribute.Name)
                // test Xml5 data- attributes
                || (AllowDataAttributes && attribute.Name != null && attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sanitizes the style.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <param name="baseUrl">The base URL.</param>
        protected void SanitizeStyle(IElement element, string baseUrl)
        {
            // filter out invalid CSS declarations
            // see https://github.com/AngleSharp/AngleSharp/issues/101
            var attribute = element.GetAttribute("style");
            if (attribute == null) return;
            if (element.GetStyle() == null)
            {
                element.RemoveAttribute("style");
                return;
            }
            element.SetAttribute("style", element.GetStyle().ToCss());

            var styles = element.GetStyle();
            if (styles == null || styles.Length == 0) return;

            SanitizeStyleDeclaration(element, styles, baseUrl);
        }

        private void SanitizeStyleDeclaration(IElement element, ICssStyleDeclaration styles, string baseUrl)
        {
            var removeStyles = new List<Tuple<ICssProperty, RemoveReason>>();
            var setStyles = new Dictionary<string, string>();

            foreach (var style in styles)
            {
                var key = DecodeCss(style.Name);
                var val = DecodeCss(style.Value);

                if (!AllowedCssProperties.Contains(key))
                {
                    removeStyles.Add(new Tuple<ICssProperty, RemoveReason>(style, RemoveReason.NotAllowedStyle));
                    continue;
                }

                if (CssExpression.IsMatch(val) || DisallowCssPropertyValue.IsMatch(val))
                {
                    removeStyles.Add(new Tuple<ICssProperty, RemoveReason>(style, RemoveReason.NotAllowedValue));
                    continue;
                }

                var urls = CssUrl.Matches(val);

                if (urls.Count > 0)
                {
                    if (urls.Cast<Match>().Any(m => SanitizeUrl(element, m.Groups[2].Value, baseUrl) == null))
                        removeStyles.Add(new Tuple<ICssProperty, RemoveReason>(style, RemoveReason.NotAllowedUrlValue));
                    else
                    {
                        var s = CssUrl.Replace(val, m => "url(" + m.Groups[1].Value + SanitizeUrl(element, m.Groups[2].Value, baseUrl) + m.Groups[3].Value);
                        if (s != val)
                        {
                            if (key != style.Name)
                            {
                                removeStyles.Add(new Tuple<ICssProperty, RemoveReason>(style, RemoveReason.NotAllowedUrlValue));
                            }
                            setStyles[key] = s;
                        }
                    }
                }
            }

            foreach (var style in setStyles)
            {
                styles.SetProperty(style.Key, style.Value);
            }

            foreach (var style in removeStyles)
            {
                RemoveStyle(element, styles, style.Item1, style.Item2);
            }
        }

        /// <summary>
        /// Decodes CSS unicode escapes and removes comments.
        /// </summary>
        /// <param name="css">The CSS string.</param>
        /// <returns>The decoded CSS string.</returns>
        protected static string DecodeCss(string css)
        {
            var r = CssUnicodeEscapes.Replace(css, m =>
            {
                if (m.Groups[1].Success)
                    return ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToString();
                var t = m.Groups[2].Value;
                return t == "\\" ? @"\\" : t;
            });

            r = CssComments.Replace(r, m => "");

            return r;
        }

        private static readonly Regex SchemeRegex = new(@"^\s*([^\/#]*?)(?:\:|&#0*58|&#x0*3a)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Tries to create a safe <see cref="Iri"/> object from a string.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <returns>The <see cref="Iri"/> object or null if no safe <see cref="Iri"/> can be created.</returns>
        protected Iri? GetSafeIri(string url)
        {
            var schemeMatch = SchemeRegex.Match(url);

            if (schemeMatch.Success)
            {
                var scheme = schemeMatch.Groups[1].Value;
                return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) ? new Iri(url, scheme) : null;
            }

            return new Iri(url);
        }

        /// <summary>
        /// Sanitizes a URL.
        /// </summary>
        /// <param name="element">The tag containing the URL being sanitized</param>
        /// <param name="url">The URL.</param>
        /// <param name="baseUrl">The base URL relative URLs are resolved against (empty or null for no resolution).</param>
        /// <returns>The sanitized URL or null if no safe URL can be created.</returns>
        protected virtual string? SanitizeUrl(IElement element, string url, string baseUrl)
        {
            var iri = GetSafeIri(url);

            if (iri != null && !iri.IsAbsolute && !string.IsNullOrEmpty(baseUrl))
            {
                // resolve relative uri
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri))
                {
                    try
                    {
                        return new Uri(baseUri, iri.Value).AbsoluteUri;
                    }
                    catch (UriFormatException)
                    {
                        iri = null;
                    }
                }
                else iri = null;
            }

            var e = new FilterUrlEventArgs(element, url, iri?.Value);
            OnFilteringUrl(e);

            return e.SanitizedUrl;
        }

        /// <summary>
        /// Removes a tag from the document.
        /// </summary>
        /// <param name="tag">Tag to be removed</param>
        /// <param name="reason">Reason for removal</param>
        private void RemoveTag(IElement tag, RemoveReason reason)
        {
            var e = new RemovingTagEventArgs(tag, reason);
            OnRemovingTag(e);
            if (!e.Cancel)
            {
                if (KeepChildNodes && tag.HasChildNodes)
                    tag.Replace(tag.ChildNodes.ToArray());
                else
                    tag.Remove();
            }
        }

        /// <summary>
        /// Removes an attribute from the document.
        /// </summary>
        /// <param name="tag">Tag the attribute belongs to</param>
        /// <param name="attribute">Attribute to be removed</param>
        /// <param name="reason">Reason for removal</param>
        private void RemoveAttribute(IElement tag, IAttr attribute, RemoveReason reason)
        {
            var e = new RemovingAttributeEventArgs(tag, attribute, reason);
            OnRemovingAttribute(e);
            if (!e.Cancel) tag.RemoveAttribute(attribute.Name);
        }

        /// <summary>
        /// Removes a style from the document.
        /// </summary>
        /// <param name="tag">Tag the style belongs to</param>
        /// <param name="styles">Style rule that contains the style to be removed</param>
        /// <param name="style">Style to be removed</param>
        /// <param name="reason">Reason for removal</param>
        private void RemoveStyle(IElement tag, ICssStyleDeclaration styles, ICssProperty style, RemoveReason reason)
        {
            var e = new RemovingStyleEventArgs(tag, style, reason);
            OnRemovingStyle(e);
            if (!e.Cancel) styles.RemoveProperty(style.Name);
        }

        /// <summary>
        /// Removes an at-rule from the document.
        /// </summary>
        /// <param name="tag">Tag the style belongs to</param>
        /// <param name="rule">Rule to be removed</param>
        /// <returns>true, if the rule can be removed; false, otherwise.</returns>
        private bool RemoveAtRule(IElement tag, ICssRule rule)
        {
            var e = new RemovingAtRuleEventArgs(tag, rule);
            OnRemovingAtRule(e);
            return !e.Cancel;
        }

        /// <summary>
        /// Removes a CSS class from a class attribute.
        /// </summary>
        /// <param name="tag">Tag the style belongs to</param>
        /// <param name="cssClass">Class to be removed</param>
        /// <param name="reason">Reason for removal</param>
        private void RemoveCssClass(IElement tag, string cssClass, RemoveReason reason)
        {
            var e = new RemovingCssClassEventArgs(tag, cssClass, reason);
            OnRemovingCssClass(e);
            if (!e.Cancel) tag.ClassList.Remove(cssClass);
        }
    }
}

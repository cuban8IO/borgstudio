#!/usr/bin/env node
// Builds the product page for GitHub Pages from site/:
//   index.html (English, the source)  -> <out>/index.html
//   index.html + i18n/de.json         -> <out>/de/index.html (a real German page, so search engines index it)
//   both get structured data (schema.org SoftwareApplication) with the current version from version.json,
//   and <out>/sitemap.xml lists both with their language alternates.
//
// Usage: node .github/scripts/build-site.mjs <source> <out> [site-url]
//   site-url defaults to the address the page is published at; it must end with "/".
// No dependencies: plain Node 18+.
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [source = "site", out = "_site", siteUrl = "https://cuban8io.github.io/borgstudio/"] = process.argv.slice(2);
const defaultUrl = "https://cuban8io.github.io/borgstudio/";
if (!siteUrl.endsWith("/")) throw new Error("The site URL must end with /");

const template = readFileSync(join(source, "index.html"), "utf8");
const de = JSON.parse(readFileSync(join(source, "i18n", "de.json"), "utf8"));
const versionFile = join(source, "version.json");
const release = existsSync(versionFile) ? JSON.parse(readFileSync(versionFile, "utf8")) : {};

rmSync(out, { recursive: true, force: true });
cpSync(source, out, { recursive: true, filter: (path) => !/[\\/]i18n([\\/]|$)/.test(path) });

const pages = {
  en: { url: siteUrl, description: "Desktop app for BorgBackup: deduplicated, encrypted backups to a disk, an SSH server or a Hetzner Storage Box; browse archives and restore files." },
  de: { url: `${siteUrl}de/`, description: de["meta.appDescription"] },
};

/** schema.org data for search engines. */
function structuredData(language) {
  const page = pages[language];
  const data = {
    "@context": "https://schema.org",
    "@type": "SoftwareApplication",
    name: "BorgStudio",
    applicationCategory: "UtilitiesApplication",
    operatingSystem: "Windows, macOS, Linux",
    description: page.description,
    url: page.url,
    inLanguage: language,
    downloadUrl: "https://github.com/cuban8IO/borgstudio/releases/latest",
    screenshot: `${siteUrl}images/${language}/main-archives.png`,
    softwareRequirements: "BorgBackup 1.2 or newer",
    isAccessibleForFree: true,
    offers: { "@type": "Offer", price: "0", priceCurrency: "EUR" },
  };
  if (release.version) data.softwareVersion = release.version;
  if (release.date) data.datePublished = release.date.slice(0, 10);
  const json = JSON.stringify(data, null, 2).replace(/</g, "\\u003c");
  return `<script type="application/ld+json">\n${json}\n</script>`;
}

function common(html, language) {
  html = html.replaceAll(defaultUrl, siteUrl);
  return html.replace("<!-- structured data: written by .github/scripts/build-site.mjs -->", structuredData(language));
}

function replaceOnce(html, pattern, replacement, what) {
  if (!pattern.test(html)) throw new Error(`Not found in index.html: ${what}`);
  return html.replace(pattern, replacement);
}

function attribute(text) {
  return text.replaceAll("&", "&amp;").replaceAll('"', "&quot;");
}

// --- English ---
writeFileSync(join(out, "index.html"), common(template, "en"));

// --- German ---
let html = template;
const used = new Set();

// Every element with data-i18n gets the German text (which may contain markup, like the English one).
html = html.replace(/<([a-z][a-z0-9]*)(\s[^>]*?\bdata-i18n="([^"]+)"[^>]*)>([\s\S]*?)<\/\1>/g, (whole, tag, attributes, key, inner) => {
  if (new RegExp(`<${tag}[\\s>]`).test(inner)) throw new Error(`data-i18n="${key}": <${tag}> nested in itself is not supported`);
  if (!(key in de)) throw new Error(`No German text for ${key}`);
  used.add(key);
  return `<${tag}${attributes}>${de[key]}</${tag}>`;
});
const unused = Object.keys(de).filter((key) => !key.startsWith("meta.") && !key.startsWith("alt.") && !used.has(key));
if (unused.length) throw new Error(`German texts without a place in index.html: ${unused.join(", ")}`);

html = replaceOnce(html, /<html lang="en">/, '<html lang="de">', "html lang");
html = replaceOnce(html, /<title>[^<]*<\/title>/, `<title>${de["meta.title"]}</title>`, "title");
html = replaceOnce(html, /(<meta name="description" content=")[^"]*/, `$1${attribute(de["meta.description"])}`, "description");
html = replaceOnce(html, /(<meta property="og:title" content=")[^"]*/, `$1${attribute(de["meta.ogTitle"])}`, "og:title");
html = replaceOnce(html, /(<meta property="og:description" content=")[^"]*/, `$1${attribute(de["meta.ogDescription"])}`, "og:description");
html = replaceOnce(html, /<meta property="og:locale" content="en_US">\s*<meta property="og:locale:alternate" content="de_DE">/,
  '<meta property="og:locale" content="de_DE">\n  <meta property="og:locale:alternate" content="en_US">', "og:locale");
html = replaceOnce(html, /(<link rel="canonical" href=")[^"]*/, `$1${pages.de.url}`, "canonical");
html = replaceOnce(html, /(<meta property="og:url" content=")[^"]*/, `$1${pages.de.url}`, "og:url");
html = html.replaceAll(`${defaultUrl}images/en/`, `${defaultUrl}images/de/`);

// Screenshots in German, with German descriptions.
html = html.replace(/<img([^>]*)\bdata-shot="([^"]+)"([^>]*)>/g, (whole, before, shot, after) => {
  let tag = `<img${before}data-shot="${shot}"${after}>`.replace(`images/en/${shot}.png`, `images/de/${shot}.png`);
  if (de[`alt.${shot}`]) tag = tag.replace(/alt="[^"]*"/, `alt="${attribute(de[`alt.${shot}`])}"`);
  return tag;
});

// The German page lives one folder down: relative links point up (anchors on the page itself stay).
html = html.replace(/\b(href|src)="([^"]*)"/g, (whole, name, value) => {
  if (/^([a-z]+:|#|\/)/i.test(value)) return whole;
  return `${name}="${value === "./" ? "../" : `../${value}`}"`;
});
// The language switch: German is this page now.
html = replaceOnce(html, /href="\.\.\/de\/"/, 'href="./"', "language switch");

mkdirSync(join(out, "de"), { recursive: true });
writeFileSync(join(out, "de", "index.html"), common(html, "de"));

// --- Sitemap ---
const today = new Date().toISOString().slice(0, 10);
const alternates = Object.entries(pages)
  .map(([language, page]) => `    <xhtml:link rel="alternate" hreflang="${language}" href="${page.url}"/>`)
  .concat(`    <xhtml:link rel="alternate" hreflang="x-default" href="${pages.en.url}"/>`)
  .join("\n");
const urls = Object.values(pages)
  .map((page) => `  <url>\n    <loc>${page.url}</loc>\n    <lastmod>${today}</lastmod>\n${alternates}\n  </url>`)
  .join("\n");
writeFileSync(join(out, "sitemap.xml"),
  `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n${urls}\n</urlset>\n`);

console.log(`Built ${out}: index.html, de/index.html (${used.size} texts), sitemap.xml${release.version ? `, version ${release.version}` : ""}`);

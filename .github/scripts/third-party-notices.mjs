#!/usr/bin/env node
// Writes THIRD-PARTY-NOTICES.txt for one published package: every NuGet package whose files end up in the app for
// that runtime (taken from the restore data), the .NET runtime that self-contained builds carry, and the Inter font.
// For each: name, version, license, copyright, project page and the license text shipped with the package (or the
// standard MIT text with the package's copyright), plus any third-party notices the package ships itself.
//
// Usage: node .github/scripts/third-party-notices.mjs <project.assets.json> <rid> <output file>
//   Run after "dotnet publish -r <rid>", so the restore data contains that runtime. No dependencies: plain Node 18+.
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const [assetsFile, rid, output] = process.argv.slice(2);
if (!assetsFile || !rid || !output) throw new Error("Usage: third-party-notices.mjs <project.assets.json> <rid> <output file>");

const here = dirname(fileURLToPath(import.meta.url));
const assets = JSON.parse(readFileSync(assetsFile, "utf8"));
const packageFolder = Object.keys(assets.packageFolders)[0];
const target = assets.targets[`net10.0/${rid}`] ?? (() => { throw new Error(`No restore data for ${rid}: publish for it first`); })();

// Development and build helpers that never reach the published app.
const notShipped = new Set(["avalonia.buildservices", "avaloniaui.diagnosticssupport"]);
const ships = (entry) => ["runtime", "native", "runtimeTargets", "resource"]
  .some((kind) => entry[kind] && Object.keys(entry[kind]).some((file) => !file.endsWith("/_._")));

const packages = Object.entries(target)
  .filter(([, entry]) => entry.type === "package" && ships(entry))
  .map(([key]) => key.split("/"))
  .filter(([name]) => !notShipped.has(name.toLowerCase()));

// The runtime self-contained builds include.
const runtime = (assets.project.frameworks["net10.0"].downloadDependencies ?? [])
  .find((dependency) => dependency.name.toLowerCase() === `microsoft.netcore.app.runtime.${rid}`);
if (runtime) packages.push([runtime.name, runtime.version.replace(/[[\]\s]/g, "").split(",")[0]]);
packages.sort(([a], [b]) => a.localeCompare(b, "en", { sensitivity: "base" }));

const mitText = readFileSync(join(here, "licenses", "MIT.txt"), "utf8").trim();

function tag(nuspec, name) {
  const match = nuspec.match(new RegExp(`<${name}(?:\\s[^>]*)?>([\\s\\S]*?)</${name}>`));
  return match ? match[1].trim() : null;
}

function packageFiles(folder) {
  return existsSync(folder) ? readdirSync(folder).filter((file) => !file.endsWith(".nupkg") && !file.endsWith(".sha512")) : [];
}

const sections = [];
for (const [name, version] of packages) {
  const folder = join(packageFolder, name.toLowerCase(), version.toLowerCase());
  const nuspecFile = packageFiles(folder).find((file) => file.endsWith(".nuspec"));
  if (!nuspecFile) throw new Error(`Package not found in ${folder}`);
  const nuspec = readFileSync(join(folder, nuspecFile), "utf8");
  const licenseTag = nuspec.match(/<license\s+type="(expression|file)">([^<]+)<\/license>/);
  const copyright = tag(nuspec, "copyright")?.replaceAll("&amp;", "&").replaceAll("&#169;", "©");
  const projectUrl = tag(nuspec, "projectUrl");

  // The license text: the file the package names, else a LICENSE file it ships, else the standard text.
  const files = packageFiles(folder);
  const licenseFile = licenseTag?.[1] === "file" ? licenseTag[2]
    : files.find((file) => /^licen[cs]e(\.(txt|md))?$/i.test(file));
  let licenseText = licenseFile && existsSync(join(folder, licenseFile)) ? readFileSync(join(folder, licenseFile), "utf8").trim() : null;
  const license = licenseTag?.[1] === "expression" ? licenseTag[2] : licenseFile ? "see license text" : "unknown";
  if (!licenseText && license === "MIT") {
    const holders = copyright ?? name;
    licenseText = mitText.replace("<copyright holders>", /^(copyright|©|\(c\))/i.test(holders) ? holders : `Copyright (c) ${holders}`);
  }
  if (!licenseText) throw new Error(`${name} ${version}: no license text found (license: ${license})`);

  const notices = files.filter((file) => /third-party-notices/i.test(file))
    .map((file) => readFileSync(join(folder, file), "utf8").trim());

  sections.push([
    `${name} ${version}`,
    `License: ${license}`,
    copyright ? `Copyright: ${copyright}` : null,
    projectUrl ? `Project: ${projectUrl}` : null,
    "",
    licenseText,
    ...notices.flatMap((text) => ["", `Third-party notices shipped with ${name}:`, "", text]),
  ].filter((line) => line !== null).join("\n"));
}

// The Inter font, embedded by Avalonia.Fonts.Inter.
if (packages.some(([name]) => name.toLowerCase() === "avalonia.fonts.inter")) {
  sections.push([
    "Inter typeface (embedded via Avalonia.Fonts.Inter)",
    "License: OFL-1.1",
    "Project: https://github.com/rsms/inter",
    "",
    readFileSync(join(here, "licenses", "Inter-OFL.txt"), "utf8").trim(),
  ].join("\n"));
}

const rule = "=".repeat(78);
const header = [
  "BorgStudio – third-party notices",
  "",
  `BorgStudio (${rid}) contains the following third-party software. Each part is used under the license shown;`,
  "BorgStudio's own license is in LICENSE.txt.",
].join("\n");
writeFileSync(output, `${header}\n\n${sections.map((section) => `${rule}\n${section}`).join("\n\n")}\n`);
console.log(`${output}: ${sections.length} components`);

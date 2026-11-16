// BorgStudio product page: the current release from GitHub, and the language choice (English at /, German at /de/,
// both pages built from index.html and i18n/de.json by .github/scripts/build-site.mjs).
(() => {
  "use strict";

  const repo = "cuban8IO/borgstudio";
  const releasesUrl = `https://github.com/${repo}/releases/latest`;
  // version.json sits next to this script (also when the German page loads it from ../).
  const versionFile = new URL("version.json", document.currentScript.src).href;
  const language = document.documentElement.lang === "de" ? "de" : "en";

  const texts = {
    en: { released: "released", download: "Download for", other: "Download" },
    de: { released: "veröffentlicht am", download: "Herunterladen für", other: "Herunterladen" },
  }[language];

  // --- Language ---

  function stored() {
    try {
      return localStorage.getItem("borgstudio-lang");
    } catch {
      return null; // Private mode: nothing remembered.
    }
  }

  function remember(value) {
    try {
      localStorage.setItem("borgstudio-lang", value);
    } catch {
      // Private mode: the choice is just not remembered.
    }
  }

  document.querySelectorAll(".lang a").forEach((link) => {
    link.classList.toggle("active", link.dataset.lang === language);
    link.addEventListener("click", () => remember(link.dataset.lang));
  });

  // First visit with a German browser: the German page (only from the English one, and never against a choice).
  const browser = ((navigator.languages && navigator.languages[0]) || navigator.language || "").toLowerCase();
  const german = document.querySelector('.lang a[data-lang="de"]');
  if (language === "en" && !stored() && browser.startsWith("de") && german) {
    location.replace(german.href + location.hash);
    return;
  }

  // --- Current release ---

  let release = null;

  // Which package fits this visitor (macOS can't tell Apple Silicon from Intel in the browser: Apple Silicon is the default).
  function platform() {
    const agent = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent;
    if (/win/i.test(agent)) return { rid: "win-x64", name: "Windows" };
    if (/mac/i.test(agent)) return { rid: "osx-arm64", name: "macOS" };
    if (/linux/i.test(agent) && !/android/i.test(navigator.userAgent)) return { rid: "linux-x64", name: "Linux" };
    return null;
  }

  function packageFor(rid) {
    const suffix = `-${rid}${rid.startsWith("linux") ? ".tar.gz" : ".zip"}`;
    return release && release.assets.find((asset) => asset.name.endsWith(suffix));
  }

  function showRelease() {
    const version = release ? release.version : null;
    document.querySelectorAll(".js-version").forEach((element) => {
      const inFooter = Boolean(element.closest(".footer"));
      element.textContent = version ? (inFooter ? `· ${version}` : version) : (inFooter ? "" : "–");
    });

    const date = document.querySelector(".js-date");
    if (date && release && release.date) {
      const day = new Date(release.date).toLocaleDateString(language === "de" ? "de-DE" : "en-GB", { day: "numeric", month: "long", year: "numeric" });
      date.textContent = `(${texts.released} ${day})`;
    }
    if (release && release.url) document.querySelectorAll(".js-notes").forEach((link) => { link.href = release.url; });

    const mine = platform();
    document.querySelectorAll(".js-asset").forEach((card) => {
      const asset = packageFor(card.dataset.rid);
      if (asset) {
        card.href = asset.url;
        const file = card.querySelector(".js-file");
        file.textContent = asset.name;
        const amount = document.createElement("span");
        amount.className = "size";
        amount.textContent = `${(asset.size / 1024 / 1024).toFixed(0)} MB`;
        file.append(amount);
      }
      card.classList.toggle("recommended", Boolean(mine && mine.rid === card.dataset.rid));
    });
    const sums = release && release.assets.find((asset) => asset.name === "SHA256SUMS");
    if (sums) document.querySelectorAll(".js-sums").forEach((link) => { link.href = sums.url; });

    const primary = document.querySelector(".js-primary-download");
    if (primary) {
      const asset = mine && packageFor(mine.rid);
      primary.textContent = mine ? `${texts.download} ${mine.name}` : texts.other;
      primary.href = asset ? asset.url : "#download";
    }
  }

  // The newest release straight from GitHub; version.json (written when the page is published) if that fails.
  async function loadRelease() {
    try {
      const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } });
      if (!response.ok) throw new Error(response.statusText);
      const data = await response.json();
      release = {
        version: data.tag_name.replace(/^v/, ""),
        date: data.published_at,
        url: data.html_url,
        assets: (data.assets || []).map((asset) => ({ name: asset.name, url: asset.browser_download_url, size: asset.size })),
      };
    } catch {
      try {
        const response = await fetch(versionFile, { cache: "no-store" });
        if (response.ok) {
          const data = await response.json();
          if (data.version) release = { version: data.version, date: data.date, url: releasesUrl, assets: data.assets || [] };
        }
      } catch {
        // Offline: the links still lead to the releases page.
      }
    }
    showRelease();
  }

  showRelease();
  loadRelease();
})();

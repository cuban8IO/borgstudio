// BorgStudio product page: language switch (English in the HTML, German here) and the current release from GitHub.
(() => {
  "use strict";

  const repo = "cuban8IO/borgstudio";
  const releasesUrl = `https://github.com/${repo}/releases/latest`;

  const de = {
    "nav.advantages": "Vorteile",
    "nav.features": "Funktionen",
    "nav.examples": "Beispiele",
    "nav.download": "Download",
    "nav.docs": "Anleitung",

    "hero.badge": "Version",
    "hero.title": "Backups, auf die du dich verlassen kannst.<br><span class=\"accent\">Ohne Kommandozeile.</span>",
    "hero.lead": "BorgStudio ist eine Desktop-App für <a href=\"https://www.borgbackup.org/\">BorgBackup</a>: deduplizierte, verschlüsselte Backups auf eine Festplatte, deinen eigenen Server oder eine Hetzner Storage Box – und jede Datei mit wenigen Klicks zurück.",
    "hero.download": "Herunterladen",
    "hero.examples": "Beispiele ansehen",
    "hero.platforms": "Windows · macOS · Linux — kostenlos, portabel, ohne Konto.",

    "adv.title": "Warum BorgStudio",
    "adv.lead": "BorgBackup gehört zu den zuverlässigsten Backup-Werkzeugen überhaupt. BorgStudio packt es in ein Fenster – mit allem, was borg kann, und ohne dass du etwas tippen musst.",
    "adv.1.title": "Klein und schnell ab dem zweiten Backup",
    "adv.1.text": "borg dedupliziert und komprimiert: Unveränderte Daten werden nie doppelt gespeichert, spätere Backups laden nur Neues hoch – oft nur ein paar Megabyte.",
    "adv.2.title": "Verschlüsselt, bevor es deinen Rechner verlässt",
    "adv.2.text": "Backups werden auf deinem Rechner verschlüsselt. Passphrasen und SSH-Schlüssel liegen in einem PIN-geschützten Tresor (Argon2id + AES-256) oder im Schlüsselbund des Systems.",
    "adv.3.title": "Jede Datei in Sekunden zurück",
    "adv.3.text": "Durchsuche jedes Backup wie einen Ordner, suche über alle Backups, sieh jede Version einer Datei – und stelle sie wieder her oder öffne sie einfach.",
    "adv.4.title": "Deine Daten bleiben deine",
    "adv.4.text": "Kein Konto, kein Cloud-Dienst von uns, keine Telemetrie. Deine Backups landen, wo du willst: USB-Platte, NAS, beliebiger SSH-Server oder Storage Box.",
    "adv.5.title": "Darunter ganz normales borg",
    "adv.5.text": "BorgStudio legt normale borg-Repositories an. Verbinde deine vorhandenen und nutze die borg-Kommandozeile weiter, wann immer du willst.",
    "adv.6.title": "Windows, macOS und Linux",
    "adv.6.text": "Entpacken und starten – die Laufzeit ist dabei. Unter Windows läuft borg in WSL. BorgStudio aktualisiert sich selbst und prüft jeden Download.",

    "feat.title": "Was es kann",
    "feat.1.title": "Backup-Jobs mit Live-Fortschritt",
    "feat.1.text": "Ordner wählen, Caches, Build-Ausgaben und Papierkörbe mit einem Klick auslassen, Kompression wählen. Während borg läuft, siehst du Upload-Geschwindigkeit und Restzeit; ein Abbruch behält einen Checkpoint.",
    "feat.1.a": "Mehrere Jobs pro Repository",
    "feat.1.b": "Alte Archive werden automatisch aufgeräumt (täglich, wöchentlich, monatlich …)",
    "feat.1.c": "Nicht lesbare Dateien werden aufgelistet, nicht verschwiegen",
    "feat.2.title": "Backups durchsuchen wie Ordner",
    "feat.2.text": "Öffne ein beliebiges Archiv: links der Ordnerbaum, rechts die Dateien mit Größe und Datum, dazu eine Suche über das ganze Archiv. Hake an, was du brauchst, und stelle es wieder her.",
    "feat.3.title": "Alle Archive durchsuchen, jede Version finden",
    "feat.3.text": "Welches Backup hat noch die alte Tabelle? Durchsuche alle Archive auf einmal nach Name oder Muster (<code>*.pdf</code>) und sieh, wie sich eine Datei verändert hat – jede Version lässt sich wiederherstellen.",
    "feat.4.title": "Sicher wiederherstellen",
    "feat.4.text": "In einen Ordner deiner Wahl oder zurück an den Originalort. Bevor etwas geschrieben wird, prüft BorgStudio, was überschrieben würde – und nennt Dateien, die nach dem Backup geändert wurden.",
    "feat.5.title": "Ein Archiv als Ordner einhängen",
    "feat.5.text": "Nur eine Datei gebraucht? Häng das Archiv – oder das ganze Repository – ein und öffne es im Explorer, Finder oder Dateimanager. Nur lesend, es kann nichts passieren.",
    "feat.6.title": "Gesperrt, verschlüsselt, aktuell",
    "feat.6.text": "App-Sperre mit PIN, ein verschlüsselter Tresor für Passphrasen und SSH-Schlüssel, Deutsch oder Englisch und Updates direkt in der App – vor der Installation gegen die Prüfsummen des Releases geprüft.",

    "ex.title": "Beispiele",
    "ex.lead": "So wird BorgStudio genutzt – jeweils in wenigen Minuten eingerichtet.",
    "ex.1.tag": "Außer Haus",
    "ex.1.title": "Laptop → Hetzner Storage Box",
    "ex.1.a": "Repository hinzufügen, <em>Hetzner Storage Box</em> wählen, <code>u123456</code> und <code>backups/laptop</code> eintragen.",
    "ex.1.b": "BorgStudio prüft den Host-Schlüssel der Box gegen Hetzners Fingerabdrücke und installiert einen Login-Schlüssel.",
    "ex.1.c": "Job „Documents“ anlegen, 7 tägliche, 4 wöchentliche und 6 monatliche Archive behalten.",
    "ex.1.d": "<em>Jetzt sichern</em> – spätere Läufe laden nur Änderungen hoch.",
    "ex.2.tag": "Lokale Kopie",
    "ex.2.title": "Fotos → USB-Platte",
    "ex.2.a": "Repository in einem Ordner auf der Platte anlegen, z. B. <code>D:\\Backups\\borg</code>.",
    "ex.2.b": "Job „Fotos“ mit schneller <code>lz4</code>-Kompression – Fotos lassen sich ohnehin kaum komprimieren.",
    "ex.2.c": "Alle Archive behalten: Dank Deduplizierung kostet ein weiteres Backup unveränderter Fotos fast nichts.",
    "ex.3.tag": "Eigene Hardware",
    "ex.3.title": "NAS zu Hause per SSH",
    "ex.3.a": "<em>SSH-Server</em> mit Host, Benutzer und Pfad hinzufügen.",
    "ex.3.b": "Fingerabdruck des Servers einmal bestätigen; BorgStudio erzeugt einen Schlüssel und installiert ihn mit deinem Passwort – das Passwort wird nicht gespeichert.",
    "ex.3.c": "Der Schlüssel bleibt verschlüsselt im Tresor und geht nur während eines Backups an borg.",
    "ex.4.tag": "Hoppla",
    "ex.4.title": "Die Tabelle vom letzten Monat",
    "ex.4.a": "<em>Alle Archive durchsuchen…</em> nach <code>budget.ods</code>.",
    "ex.4.b": "Ihre Versionen öffnen: eine Zeile pro Änderung, mit den Archiven, in denen sie steckt.",
    "ex.4.c": "Die gewünschte Version in einen Ordner wiederherstellen – die aktuelle Datei bleibt unberührt.",
    "ex.5.tag": "Kurz reinschauen",
    "ex.5.title": "Eine Datei, ohne Wiederherstellen",
    "ex.5.a": "Archiv auswählen und <em>Einhängen</em> klicken.",
    "ex.5.b": "Das Archiv öffnet sich als Ordner im Dateimanager – kopiere, was du brauchst.",
    "ex.5.c": "Über die Statusleiste aushängen oder BorgStudio einfach schließen.",
    "ex.6.tag": "Umzug",
    "ex.6.title": "Ein neuer Rechner",
    "ex.6.a": "<em>Extras → Einstellungen exportieren</em> schreibt Repositories, Passphrasen, SSH-Schlüssel und Jobs in eine verschlüsselte Datei.",
    "ex.6.b": "Auf dem neuen Rechner: <em>Einstellungen importieren</em>, Passwort der Datei eingeben.",
    "ex.6.c": "Alles ist wieder da – bereit für das nächste Backup.",

    "steps.title": "In drei Schritten startklar",
    "steps.1.title": "Installieren",
    "steps.1.text": "BorgStudio entpacken und borg installieren (unter Windows in WSL: <code>sudo apt install borgbackup</code>).",
    "steps.2.title": "Repository hinzufügen",
    "steps.2.text": "Ein lokaler Ordner, ein SSH-Server oder eine Storage Box – neu oder vorhanden.",
    "steps.3.title": "Sichern",
    "steps.3.text": "Job anlegen, <em>Jetzt sichern</em> klicken, fertig. Durchsuchen und wiederherstellen, wann immer du es brauchst.",
    "steps.guide": "Zur Schritt-für-Schritt-Anleitung (Englisch)",

    "dl.title": "Download",
    "dl.current": "Aktuelle Version:",
    "dl.notes": "Was ist neu",
    "dl.arm": "Apple Silicon (ab M1)",
    "dl.req": "<strong>Voraussetzung:</strong> <a href=\"https://borgbackup.readthedocs.io/en/stable/installation.html\">BorgBackup</a> 1.2 oder neuer (1.4 empfohlen) – unter Windows in WSL. Die .NET-Laufzeit ist enthalten.",
    "dl.check": "Jedes Release listet die SHA-256-Prüfsummen seiner Dateien in <a class=\"js-sums\" href=\"https://github.com/cuban8IO/borgstudio/releases/latest\">SHA256SUMS</a>; der eingebaute Updater prüft sie vor der Installation.",
    "dl.mac": "<strong>macOS:</strong> Die App ist nicht von Apple notarisiert. Verweigert macOS das Öffnen, erlaube es unter Systemeinstellungen → Datenschutz &amp; Sicherheit → „Trotzdem öffnen“.",

    "faq.title": "Fragen",
    "faq.1.q": "Kostet BorgStudio etwas?",
    "faq.1.a": "Nein. BorgStudio ist kostenlos, der Quellcode liegt auf GitHub. Du zahlst nur für Speicher, den du selbst mietest, etwa eine Storage Box.",
    "faq.2.q": "Kann ich meine vorhandenen borg-Repositories nutzen?",
    "faq.2.a": "Ja. Verbinde sie mit ihrer Passphrase. Alles, was BorgStudio anlegt, ist ein normales borg-Repository, das auch die borg-Kommandozeile und andere borg-Werkzeuge lesen können.",
    "faq.3.q": "Schickt BorgStudio meine Daten irgendwohin?",
    "faq.3.a": "Nur an die Repositories, die du hinzufügst. Für Updates fragt es die öffentliche Release-Liste von GitHub. Es gibt kein Konto und keine Telemetrie.",
    "faq.4.q": "Was passiert, wenn ich meine Passphrase verliere?",
    "faq.4.a": "Dann kann niemand die Backups entschlüsseln. Schreib die Passphrase auf und exportiere den Repository-Schlüssel; BorgStudio bietet das direkt nach dem Anlegen an.",
    "faq.5.q": "Gibt es zeitgesteuerte Backups?",
    "faq.5.a": "Noch nicht – sie sind die nächste große Funktion. Bis dahin startest du Backups mit einem Klick.",
    "faq.6.q": "Warum WSL unter Windows?",
    "faq.6.a": "borg selbst läuft nicht unter Windows. In WSL läuft es wie unter Linux; BorgStudio kümmert sich um die Pfade, deine Windows-Ordner erscheinen und kehren als <code>C:\\…</code> zurück.",

    "footer.releases": "Releases",
    "footer.issues": "Problem melden",
    "footer.note": "BorgStudio ist ein unabhängiges Projekt und gehört weder zum BorgBackup-Projekt noch zu Hetzner.",
  };

  const texts = {
    en: {
      title: "BorgStudio – BorgBackup without the command line",
      released: "released",
      download: "Download for",
      other: "Download",
    },
    de: {
      title: "BorgStudio – BorgBackup ohne Kommandozeile",
      released: "veröffentlicht am",
      download: "Herunterladen für",
      other: "Herunterladen",
    },
  };

  const english = new Map();
  document.querySelectorAll("[data-i18n]").forEach((element) => english.set(element, element.innerHTML));

  let language = "en";
  let release = null;

  function storedLanguage() {
    try {
      return localStorage.getItem("borgstudio-lang");
    } catch {
      return null;
    }
  }

  function setLanguage(next) {
    language = next === "de" ? "de" : "en";
    document.documentElement.lang = language;
    document.title = texts[language].title;
    document.querySelectorAll("[data-i18n]").forEach((element) => {
      const key = element.dataset.i18n;
      element.innerHTML = language === "de" && de[key] ? de[key] : english.get(element);
    });
    document.querySelectorAll("img[data-shot]").forEach((image) => {
      image.src = `images/${language}/${image.dataset.shot}.png`;
    });
    document.querySelectorAll(".lang button").forEach((button) =>
      button.classList.toggle("active", button.dataset.lang === language));
    try {
      localStorage.setItem("borgstudio-lang", language);
    } catch {
      // Private mode: the choice is just not remembered.
    }
    showRelease();
  }

  // Which package fits this visitor (macOS can't tell Apple Silicon from Intel in the browser: Apple Silicon is the default).
  function platform() {
    const agent = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent;
    if (/win/i.test(agent)) return { rid: "win-x64", name: "Windows" };
    if (/mac/i.test(agent)) return { rid: "osx-arm64", name: "macOS" };
    if (/linux/i.test(agent) && !/android/i.test(navigator.userAgent)) return { rid: "linux-x64", name: "Linux" };
    return null;
  }

  function size(bytes) {
    return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
  }

  function showRelease() {
    const version = release ? release.version : null;
    document.querySelectorAll(".js-version").forEach((element) => {
      element.textContent = version ? (element.closest(".footer") ? `· ${version}` : version) : (element.closest(".footer") ? "" : "–");
    });

    const date = document.querySelector(".js-date");
    if (date) {
      date.textContent = release && release.date
        ? `(${texts[language].released} ${new Date(release.date).toLocaleDateString(language === "de" ? "de-DE" : "en-GB", { day: "numeric", month: "long", year: "numeric" })})`
        : "";
    }
    if (release && release.url) document.querySelectorAll(".js-notes").forEach((link) => { link.href = release.url; });

    const mine = platform();
    document.querySelectorAll(".js-asset").forEach((card) => {
      const asset = release && release.assets.find((item) => item.name.endsWith(`-${card.dataset.rid}${card.dataset.rid.startsWith("linux") ? ".tar.gz" : ".zip"}`));
      if (asset) {
        card.href = asset.url;
        const file = card.querySelector(".js-file");
        file.textContent = asset.name;
        const amount = document.createElement("span");
        amount.className = "size";
        amount.textContent = size(asset.size);
        file.append(amount);
      }
      card.classList.toggle("recommended", Boolean(mine && mine.rid === card.dataset.rid));
    });
    const sums = release && release.assets.find((item) => item.name === "SHA256SUMS");
    if (sums) document.querySelectorAll(".js-sums").forEach((link) => { link.href = sums.url; });

    const primary = document.querySelector(".js-primary-download");
    if (primary) {
      const asset = mine && release && release.assets.find((item) => item.name.endsWith(`-${mine.rid}${mine.rid.startsWith("linux") ? ".tar.gz" : ".zip"}`));
      primary.textContent = mine ? `${texts[language].download} ${mine.name}` : texts[language].other;
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
        const response = await fetch("version.json", { cache: "no-store" });
        if (response.ok) {
          const data = await response.json();
          release = { version: data.version, date: data.date, url: releasesUrl, assets: data.assets || [] };
        }
      } catch {
        // Offline: the links still lead to the releases page.
      }
    }
    showRelease();
  }

  document.querySelectorAll(".lang button").forEach((button) =>
    button.addEventListener("click", () => setLanguage(button.dataset.lang)));

  const browser = (navigator.languages && navigator.languages[0]) || navigator.language || "en";
  setLanguage(storedLanguage() || (browser.toLowerCase().startsWith("de") ? "de" : "en"));
  loadRelease();
})();

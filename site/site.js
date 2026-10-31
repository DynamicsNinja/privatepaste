"use strict";

// A hairline under the sticky top bar once the page has scrolled.
const bar = document.querySelector(".bar");
const markScrolled = () => bar.classList.toggle("scrolled", window.scrollY > 4);
window.addEventListener("scroll", markScrolled, { passive: true });
markScrolled();

// Phone menu: the button opens and closes the links; picking one, Escape or a tap elsewhere closes it.
const menuButton = document.querySelector(".menu-button");
const menu = document.getElementById("site-nav");

function setMenu(open) {
    menu.classList.toggle("open", open);
    menuButton.setAttribute("aria-expanded", open);
}

menuButton.addEventListener("click", () => setMenu(!menu.classList.contains("open")));
menu.addEventListener("click", (event) => { if (event.target.closest("a")) setMenu(false); });
document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && menu.classList.contains("open")) {
        setMenu(false);
        menuButton.focus();
    }
});
document.addEventListener("click", (event) => {
    if (!event.target.closest(".top")) setMenu(false);
});

// Deploy tabs: buttons switch the panels; arrow keys move between tabs (WAI-ARIA tabs pattern).
const tabs = [...document.querySelectorAll('[role="tab"]')];

function selectTab(tab, focus) {
    for (const t of tabs) {
        const on = t === tab;
        t.setAttribute("aria-selected", on);
        t.tabIndex = on ? 0 : -1;
        document.getElementById(t.getAttribute("aria-controls")).hidden = !on;
    }
    if (focus) tab.focus();
}

for (const tab of tabs) {
    tab.addEventListener("click", () => selectTab(tab, false));
    tab.addEventListener("keydown", (event) => {
        const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
        if (!step) return;
        event.preventDefault();
        selectTab(tabs[(tabs.indexOf(tab) + step + tabs.length) % tabs.length], true);
    });
}

// Copy buttons on every code block, in its header strip so they never cover the code. They copy the text exactly
// as written, even where a narrow screen wraps the lines.
for (const pre of document.querySelectorAll("pre[data-copy]")) {
    const text = pre.textContent;
    // One block per line with a hanging indent, so a line that wraps on a narrow screen continues under its own
    // text instead of at the left edge (where YAML would look like it changed level).
    if (text.includes("\n")) {
        pre.replaceChildren(...text.split("\n").map((line) => {
            const span = document.createElement("span");
            span.className = "line";
            span.textContent = line;
            span.style.setProperty("--hang", `${line.match(/^ */)[0].length + 2}ch`);
            return span;
        }));
    }
    const button = document.createElement("button");
    button.type = "button";
    button.className = "copy";
    button.textContent = "Copy";
    button.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(text);
            button.textContent = "Copied";
        } catch {
            button.textContent = "Select and copy";
        }
        setTimeout(() => { button.textContent = "Copy"; }, 1500);
    });
    pre.parentElement.querySelector(".code-head").append(button);
}

// Download buttons: release files carry the version in their names, so look up the newest desktop and Android
// releases (GitHub's release list, cached by this site's nginx as /releases.json) and link straight to the files.
// Until that answers, or if it can't, they open the Releases page.
const assetFor = {
    windows: /-win-x64\.msi$/,
    macos: /-osx-arm64\.zip$/,
    "macos-x64": /-osx-x64\.zip$/,
    linux: /-linux-x64\.tar\.gz$/,
    android: /\.apk$/,
};
const tagFor = { windows: "desktop-v", macos: "desktop-v", "macos-x64": "desktop-v", linux: "desktop-v", android: "android-v" };

fetch("/releases.json")
    .then((response) => (response.ok ? response.json() : []))
    .then((releases) => {
        for (const link of document.querySelectorAll("[data-platform]")) {
            const platform = link.dataset.platform;
            const release = releases.find((r) => !r.draft && !r.prerelease && r.tag_name.startsWith(tagFor[platform]));
            const asset = release?.assets.find((a) => assetFor[platform].test(a.name));
            if (asset) {
                link.href = asset.browser_download_url;
                link.title = `${asset.name} (${release.tag_name.replace(/^\w+-v/, "version ")})`;
            }
        }
    })
    .catch(() => { /* the Releases page links stay */ });

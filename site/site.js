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

// The hero's "Deploy on Dokploy" / "Run it with Docker" buttons open the matching tab.
for (const link of document.querySelectorAll("[data-tab]")) {
    link.addEventListener("click", () => selectTab(document.getElementById(`tab-${link.dataset.tab}`), false));
}

// Copy buttons on every code block.
for (const pre of document.querySelectorAll("pre[data-copy]")) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "copy";
    button.textContent = "Copy";
    button.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(pre.textContent);
            button.textContent = "Copied";
        } catch {
            button.textContent = "Select and copy";
        }
        setTimeout(() => { button.textContent = "Copy"; }, 1500);
    });
    pre.parentElement.append(button);
}

// Download buttons: release files carry the version in their names, so look up the newest desktop and Android
// releases (GitHub's release list, cached by this site's nginx as /releases.json) and link straight to the files.
// Until that answers, or if it can't, they open the Releases page.
const assetFor = {
    windows: /-win-x64\.msi$/,
    macos: /-osx-arm64\.zip$/,
    linux: /-linux-x64\.tar\.gz$/,
    android: /\.apk$/,
};
const tagFor = { windows: "desktop-v", macos: "desktop-v", linux: "desktop-v", android: "android-v" };

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
